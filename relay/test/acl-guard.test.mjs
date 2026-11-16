// Unit tests for the Windows workspace ACL admission guard (node:test, no deps).
//
// Run: node --test relay/test/   (or: cd relay && npm test)
//
// The real ACL writes only work from the DSH host process (the confined tool
// sandbox cannot change DACLs), so these tests drive the guard through an
// injected fake backend; the probe/repair command shapes are covered by the
// pure helpers plus the smoke script.
import assert from 'node:assert/strict'
import fs from 'node:fs'
import os from 'node:os'
import path from 'node:path'
import test from 'node:test'

import {
  DEFAULT_OPTIONS,
  GUARD_VERSION,
  createAclGuard,
  fingerprintOf,
  normalizeRootKey,
  parseSddl,
  psQuote,
  shouldSkipRoot,
  splitAces
} from '../src/acl-guard.mjs'
import { collectResultText } from '../src/index.mjs'

const USER_SID = 'S-1-5-21-3664737177-1396954351-2511721919-1001'
const CAPABILITY_SID = 'S-1-4-840176450-129811360'

// Captured from this machine's workspace root: the user owns a non-inheritable
// FullControl ACE and DSH's capability ACE is inheritable.
const ROOT_SDDL = [
  `O:${USER_SID}G:${USER_SID}D:AI`,
  '(D;CI;DT;;;WD)',
  `(A;OICI;0x110156;;;${CAPABILITY_SID})`,
  `(A;;FA;;;${USER_SID})`,
  '(A;ID;FA;;;BA)',
  '(A;OICIIOID;GA;;;BA)',
  '(A;ID;FA;;;SY)',
  '(A;OICIIOID;GA;;;SY)',
  '(A;ID;0x1301bf;;;AU)',
  '(A;OICIIOID;SDGXGWGR;;;AU)'
].join('')

function tempDir () {
  return fs.mkdtempSync(path.join(os.tmpdir(), 'dsh-relay-acl-guard-test-'))
}

function guardPaths (dir) {
  return {
    cacheFile: path.join(dir, 'acl-admission.json'),
    auditFile: path.join(dir, 'acl-audit.jsonl')
  }
}

/** Fake Windows backend whose root starts healthy or unhealthy. */
function fakeBackend ({ healthy = false, repair = null, sddl = ROOT_SDDL } = {}) {
  const state = {
    healthy,
    probes: 0,
    repairs: 0,
    repairedRoots: [],
    repairImpl: repair
  }
  return {
    state,
    async probe (root) {
      state.probes += 1
      return {
        ok: true,
        root,
        userSid: USER_SID,
        sddl: state.healthy ? `O:${USER_SID}G:${USER_SID}D:AI(A;OICI;FA;;;${USER_SID})` : sddl,
        reparse: false
      }
    },
    async repair (root, facts) {
      state.repairs += 1
      state.repairedRoots.push(root)
      if (state.repairImpl) return state.repairImpl(root, facts)
      state.healthy = true
      return { stdout: 'Successfully processed 1 files' }
    }
  }
}

test('psQuote escapes single quotes for a PowerShell literal', () => {
  assert.equal(psQuote("C:\\it's here"), "'C:\\it''s here'")
  assert.equal(psQuote('D:\\plain'), "'D:\\plain'")
})

test('splitAces keeps only well-formed ACE field lists', () => {
  const aces = splitAces(ROOT_SDDL)
  assert.equal(aces.length, 9)
  assert.deepEqual(aces[0], ['D', 'CI', 'DT', '', '', 'WD'])
  assert.equal(aces[1][5], CAPABILITY_SID)
})

test('parseSddl: the captured root needs repair (user FullControl is not inheritable)', () => {
  const facts = parseSddl(ROOT_SDDL, USER_SID)
  assert.equal(facts.hasUserFull, true)
  assert.equal(facts.hasUserFullInheritable, false)
  assert.equal(facts.hasCapability, true)
  assert.deepEqual(facts.capabilitySids, [CAPABILITY_SID])
  assert.equal(facts.hasPackageAce, false)
})

test('parseSddl: an inheritable FullControl ACE makes the root ready', () => {
  const sddl = `D:AI(A;OICI;FA;;;${USER_SID})`
  const facts = parseSddl(sddl, USER_SID)
  assert.equal(facts.hasUserFull, true)
  assert.equal(facts.hasUserFullInheritable, true)
})

test('parseSddl: inherit-only and partial rights do not count as readiness', () => {
  const inheritOnly = parseSddl(`D:(A;IO;FA;;;${USER_SID})`, USER_SID)
  assert.equal(inheritOnly.hasUserFull, true)
  assert.equal(inheritOnly.hasUserFullInheritable, false)

  const modifyOnly = parseSddl(`D:(A;OICI;0x1301bf;;;${USER_SID})`, USER_SID)
  assert.equal(modifyOnly.hasUserFull, false)
  assert.equal(modifyOnly.hasUserFullInheritable, false)

  const otherUser = parseSddl('D:(A;OICI;FA;;;S-1-5-21-1-2-3-1002)', USER_SID)
  assert.equal(otherUser.hasUserFull, false)
})

test('parseSddl: legacy AppContainer package ACEs are reported', () => {
  const packageSid = 'S-1-15-2-3417523965-2346221062-1120213168-3951166346-925925831-3642490801-3821394152'
  const facts = parseSddl(`D:(A;OICI;FA;;;${packageSid})`, USER_SID)
  assert.equal(facts.hasPackageAce, true)
  assert.deepEqual(facts.packageSids, [packageSid])
})

test('shouldSkipRoot skips UNC paths and protected system directories', () => {
  assert.equal(shouldSkipRoot('\\\\server\\share\\repo').skip, true)
  assert.equal(shouldSkipRoot('C:\\Windows\\System32').skip, true)
  assert.equal(shouldSkipRoot('C:\\Program Files\\App').skip, true)
  assert.equal(shouldSkipRoot('C:\\Windows10\\repo').skip, false)
  assert.equal(shouldSkipRoot('D:\\Code\\VibeCoding\\DSHRemote').skip, false)
  assert.equal(shouldSkipRoot('D:\\Code\\repo', { excludedPrefixes: ['D:\\Code\\repo'] }).skip, true)
})

test('normalizeRootKey resolves a relative path and tolerates missing paths', () => {
  assert.equal(normalizeRootKey('.'), fs.realpathSync.native(process.cwd()))
  assert.ok(normalizeRootKey('D:\\definitely\\missing\\path').startsWith('D:\\'))
})

test('guard: healthy root is admitted with one probe and then served from memory', async () => {
  const dir = tempDir()
  const backend = fakeBackend({ healthy: true })
  const guard = createAclGuard({ platform: 'win32', backend, paths: guardPaths(dir), log: () => {} })
  try {
    const first = await guard.ensureReady('D:\\Code\\repo')
    assert.equal(first.ready, true)
    assert.equal(backend.state.probes, 1)
    assert.equal(backend.state.repairs, 0)
    const second = await guard.ensureReady('D:\\Code\\repo')
    assert.equal(second.ready, true)
    assert.equal(second.cached, true)
    assert.equal(backend.state.probes, 1)
  } finally {
    guard.dispose()
    fs.rmSync(dir, { recursive: true, force: true })
  }
})

test('guard: an unhealthy root is repaired, verified and cached across instances', async () => {
  const dir = tempDir()
  const backend = fakeBackend({ healthy: false })
  const paths = guardPaths(dir)
  const guard = createAclGuard({ platform: 'win32', backend, paths, log: () => {} })
  try {
    const result = await guard.ensureReady('D:\\Code\\repo')
    assert.equal(result.ready, true)
    assert.equal(backend.state.repairs, 1)
    assert.deepEqual(backend.state.repairedRoots, ['D:\\Code\\repo'])
    // probe -> repair -> verify probe
    assert.equal(backend.state.probes, 2)
    const cache = JSON.parse(fs.readFileSync(paths.cacheFile, 'utf8'))
    assert.equal(cache.version, GUARD_VERSION)
    assert.equal(Object.keys(cache.entries).length, 1)
    const audit = fs.readFileSync(paths.auditFile, 'utf8').trim().split('\n').map(JSON.parse)
    assert.ok(audit.some((row) => row.event === 'repaired'))
  } finally {
    guard.dispose()
  }

  // A second guard instance (host restart) trusts the persisted cache.
  const backend2 = fakeBackend({ healthy: false })
  const guard2 = createAclGuard({ platform: 'win32', backend: backend2, paths, log: () => {} })
  try {
    const result = await guard2.ensureReady('D:\\Code\\repo')
    assert.equal(result.ready, true)
    assert.equal(result.cached, true)
    assert.equal(backend2.state.probes, 0)
  } finally {
    guard2.dispose()
    fs.rmSync(dir, { recursive: true, force: true })
  }
})

test('guard: an expired cache entry triggers a fresh probe', async () => {
  const dir = tempDir()
  const paths = guardPaths(dir)
  fs.mkdirSync(dir, { recursive: true })
  fs.writeFileSync(paths.cacheFile, JSON.stringify({
    version: GUARD_VERSION,
    entries: {
      'D:\\Code\\repo': {
        preparedAt: new Date(Date.now() - 48 * 3600_000).toISOString(),
        fingerprint: 'stale',
        version: GUARD_VERSION
      }
    }
  }), 'utf8')
  const backend = fakeBackend({ healthy: true })
  const guard = createAclGuard({ platform: 'win32', backend, paths, log: () => {} })
  try {
    const result = await guard.ensureReady('D:\\Code\\repo')
    assert.equal(result.ready, true)
    assert.equal(backend.state.probes, 1)
  } finally {
    guard.dispose()
    fs.rmSync(dir, { recursive: true, force: true })
  }
})

test('guard: concurrent first calls share one preparation', async () => {
  const dir = tempDir()
  const backend = fakeBackend({ healthy: false })
  const guard = createAclGuard({ platform: 'win32', backend, paths: guardPaths(dir), log: () => {} })
  try {
    const results = await Promise.all(Array.from({ length: 5 }, () => guard.ensureReady('D:\\Code\\repo')))
    assert.ok(results.every((row) => row.ready === true))
    assert.equal(backend.state.probes, 2)
    assert.equal(backend.state.repairs, 1)
  } finally {
    guard.dispose()
    fs.rmSync(dir, { recursive: true, force: true })
  }
})

test('guard: a slow first preparation yields a pending answer after waitMs', async () => {
  const dir = tempDir()
  const backend = fakeBackend({
    healthy: false,
    repair: () => new Promise(() => {}) // never settles: simulates a huge propagation
  })
  const guard = createAclGuard({
    platform: 'win32',
    backend,
    paths: guardPaths(dir),
    options: { waitMs: 40 },
    log: () => {}
  })
  try {
    const result = await guard.ensureReady('D:\\Code\\repo')
    assert.equal(result.ready, false)
    assert.equal(result.pending, true)
    assert.equal(result.reason, 'timeout')
    const audit = fs.readFileSync(guardPaths(dir).auditFile, 'utf8').trim().split('\n').map(JSON.parse)
    assert.ok(audit.some((row) => row.event === 'timeout'))
  } finally {
    guard.dispose()
    fs.rmSync(dir, { recursive: true, force: true })
  }
})

test('guard: a failed repair is remembered and not retried immediately', async () => {
  const dir = tempDir()
  const backend = fakeBackend({
    healthy: false,
    repair: async () => { throw new Error('access denied') }
  })
  const guard = createAclGuard({ platform: 'win32', backend, paths: guardPaths(dir), log: () => {} })
  try {
    const first = await guard.ensureReady('D:\\Code\\repo')
    assert.equal(first.ready, false)
    assert.match(first.reason, /repair-failed/)
    const second = await guard.ensureReady('D:\\Code\\repo')
    assert.equal(second.ready, false)
    assert.equal(backend.state.repairs, 1)
  } finally {
    guard.dispose()
    fs.rmSync(dir, { recursive: true, force: true })
  }
})

test('guard: reparse-point roots are skipped, not repaired', async () => {
  const dir = tempDir()
  const backend = fakeBackend({ healthy: false })
  const originalProbe = backend.probe
  backend.probe = async (root) => ({ ...(await originalProbe(root)), reparse: true })
  const guard = createAclGuard({ platform: 'win32', backend, paths: guardPaths(dir), log: () => {} })
  try {
    const result = await guard.ensureReady('D:\\Code\\junction')
    assert.equal(result.ready, false)
    assert.equal(result.reason, 'reparse-root')
    assert.equal(backend.state.repairs, 0)
  } finally {
    guard.dispose()
    fs.rmSync(dir, { recursive: true, force: true })
  }
})

test('guard: excluded roots are admitted untouched', async () => {
  const dir = tempDir()
  const backend = fakeBackend({ healthy: false })
  const guard = createAclGuard({ platform: 'win32', backend, paths: guardPaths(dir), log: () => {} })
  try {
    const result = await guard.ensureReady('\\\\server\\share')
    assert.equal(result.ready, true)
    assert.equal(result.skipped, true)
    assert.equal(backend.state.probes, 0)
  } finally {
    guard.dispose()
    fs.rmSync(dir, { recursive: true, force: true })
  }
})

test('guard: disabled on non-Windows platforms', async () => {
  const guard = createAclGuard({ platform: 'linux', paths: guardPaths(tempDir()), log: () => {} })
  try {
    assert.equal(guard.disabled, true)
    const result = await guard.ensureReady('D:\\Code\\repo')
    assert.equal(result.ready, true)
    assert.equal(result.skipped, true)
  } finally {
    guard.dispose()
  }
})

test('guard: an ACL failure signature forces one rate-limited re-check', async () => {
  const dir = tempDir()
  const backend = fakeBackend({ healthy: true })
  const guard = createAclGuard({ platform: 'win32', backend, paths: guardPaths(dir), log: () => {} })
  try {
    await guard.ensureReady('D:\\Code\\repo')
    assert.equal(backend.state.probes, 1)
    guard.noteInstrumentFailure('D:\\Code\\repo', '[sandbox: the sandbox runner itself failed under workspace-write mode]')
    await new Promise((resolve) => setTimeout(resolve, 20))
    assert.equal(backend.state.probes, 2)
    guard.noteInstrumentFailure('D:\\Code\\repo', 'grantWrite: SetNamedSecurityInfoW failed (Win32 5)')
    await new Promise((resolve) => setTimeout(resolve, 20))
    assert.equal(backend.state.probes, 2) // cooldown suppresses the second retry
  } finally {
    guard.dispose()
    fs.rmSync(dir, { recursive: true, force: true })
  }
})

test('guard: unrelated result text does not trigger a re-check', async () => {
  const dir = tempDir()
  const backend = fakeBackend({ healthy: true })
  const guard = createAclGuard({ platform: 'win32', backend, paths: guardPaths(dir), log: () => {} })
  try {
    await guard.ensureReady('D:\\Code\\repo')
    guard.noteInstrumentFailure('D:\\Code\\repo', 'build succeeded in 1.2s')
    await new Promise((resolve) => setTimeout(resolve, 20))
    assert.equal(backend.state.probes, 1)
  } finally {
    guard.dispose()
    fs.rmSync(dir, { recursive: true, force: true })
  }
})

test('guard: status reports roots and counters', async () => {
  const dir = tempDir()
  const backend = fakeBackend({ healthy: false })
  const guard = createAclGuard({ platform: 'win32', backend, paths: guardPaths(dir), log: () => {} })
  try {
    await guard.ensureReady('D:\\Code\\repo')
    const status = guard.status()
    assert.equal(status.enabled, true)
    assert.equal(status.version, GUARD_VERSION)
    assert.equal(status.roots['D:\\Code\\repo'].state, 'ready')
    assert.equal(status.stats.repaired, 1)
  } finally {
    guard.dispose()
    fs.rmSync(dir, { recursive: true, force: true })
  }
})

test('fingerprintOf changes with the SDDL and SID', () => {
  const a = fingerprintOf({ sddl: 'D:(A;;FA;;;S-1)', userSid: USER_SID })
  const b = fingerprintOf({ sddl: 'D:(A;OICI;FA;;;S-1)', userSid: USER_SID })
  const c = fingerprintOf({ sddl: 'D:(A;;FA;;;S-1)', userSid: 'S-2' })
  assert.notEqual(a, b)
  assert.notEqual(a, c)
  assert.equal(a.length, 32)
})

test('collectResultText flattens the tool result shapes the relay sees', () => {
  const text = collectResultText({
    content: [{ type: 'text', text: 'sandbox: file access denied under workspace-write mode' }],
    isError: true,
    meta: { cwd: 'D:\\Code\\repo' }
  })
  assert.match(text, /file access denied/)
})

test('DEFAULT_OPTIONS keeps the agreed defaults (mode A, 30s wait)', () => {
  assert.equal(DEFAULT_OPTIONS.enabled, true)
  assert.equal(DEFAULT_OPTIONS.propagate, true)
  assert.equal(DEFAULT_OPTIONS.waitMs, 30_000)
  assert.equal(DEFAULT_OPTIONS.cacheTtlHours, 24)
})
