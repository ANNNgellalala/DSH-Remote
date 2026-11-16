// End-to-end smoke for the Windows workspace ACL admission guard.
//
// Must run UNCONFINED (the DSH host process, or a full-access one-shot): the
// confined sandbox token cannot write DACLs. That is exactly why the guard runs
// inside the relay plugin, not in a tool call.
//
// Run: node relay/test/acl-guard.smoke.mjs
// It creates a throw-away directory under %TEMP%, repairs its ACL, checks that a
// pre-existing child inherits the user's FullControl ACE, then deletes it.
import fs from 'node:fs'
import os from 'node:os'
import path from 'node:path'
import { execFileSync } from 'node:child_process'

import { createAclGuard, createWindowsBackend, parseSddl } from '../src/acl-guard.mjs'

function step (label, detail) {
  console.log(`[acl-guard-smoke] ${label}${detail ? `: ${typeof detail === 'string' ? detail : JSON.stringify(detail)}` : ''}`)
}

function icacls (...args) {
  return execFileSync('icacls', args, { windowsHide: true, maxBuffer: 4 * 1024 * 1024 }).toString()
}

async function main () {
  if (process.platform !== 'win32') {
    step('SKIP', 'not Windows')
    return 0
  }
  const root = fs.mkdtempSync(path.join(os.tmpdir(), 'dsh-acl-guard-smoke-'))
  const child = path.join(root, 'existing-child')
  fs.mkdirSync(child)
  const paths = {
    cacheFile: path.join(root, 'cache', 'admission.json'),
    auditFile: path.join(root, 'cache', 'audit.jsonl')
  }
  const backend = createWindowsBackend({
    probeTimeoutMs: 20_000,
    repairTimeoutMs: 120_000,
    log: (line) => step('backend', line)
  })
  const guard = createAclGuard({ log: (line) => step('guard', line), paths })

  let failures = 0
  const check = (label, condition, detail) => {
    if (condition) {
      step(`PASS ${label}`, detail)
    } else {
      failures += 1
      step(`FAIL ${label}`, detail)
    }
  }

  try {
    // A directory created by an unconfined process is healthy from birth, so
    // reproduce the real defect first: strip inherited ACEs and grant the user a
    // non-inheritable FullControl ACE (exactly the shape observed on the real
    // workspace root). That is the state DSH cannot provision from.
    const seed = await backend.probe(root)
    icacls(root, '/inheritance:r')
    icacls(root, '/grant', `*${seed.userSid}:F`)

    const before = await backend.probe(root)
    const beforeFacts = parseSddl(before.sddl, before.userSid)
    step('probe before', {
      hasUserFull: beforeFacts.hasUserFull,
      hasUserFullInheritable: beforeFacts.hasUserFullInheritable,
      hasCapability: beforeFacts.hasCapability
    })
    check('seeded root has a non-inheritable user ACE (the defect)', beforeFacts.hasUserFullInheritable === false)

    const result = await guard.ensureReady(root)
    step('ensureReady', result)
    check('ensureReady reports ready', result.ready === true)

    const after = await backend.probe(root)
    const afterFacts = parseSddl(after.sddl, after.userSid)
    check('root now carries an inheritable user FullControl ACE', afterFacts.hasUserFullInheritable === true, {
      hasUserFull: afterFacts.hasUserFull,
      hasUserFullInheritable: afterFacts.hasUserFullInheritable
    })

    const childFacts = parseSddl((await backend.probe(child)).sddl, before.userSid)
    check('pre-existing child inherited the user FullControl ACE', childFacts.hasUserFull === true, {
      hasUserFull: childFacts.hasUserFull
    })

    const cached = await guard.ensureReady(root)
    check('second call is served from cache', cached.cached === true, cached)

    const audit = fs.existsSync(paths.auditFile)
      ? fs.readFileSync(paths.auditFile, 'utf8').trim().split('\n').map((line) => JSON.parse(line))
      : []
    check('audit trail recorded a repair', audit.some((row) => row.event === 'repaired'), audit.map((row) => row.event))
    check('admission cache written', fs.existsSync(paths.cacheFile))
  } finally {
    guard.dispose()
    try {
      icacls(root, '/reset', '/T', '/C', '/Q')
    } catch {}
    try {
      fs.rmSync(root, { recursive: true, force: true })
    } catch (error) {
      step('WARN cleanup failed', String(error && error.message ? error.message : error))
    }
  }

  step(failures === 0 ? 'RESULT PASS' : `RESULT FAIL (${failures})`)
  return failures === 0 ? 0 : 1
}

process.exitCode = await main()
