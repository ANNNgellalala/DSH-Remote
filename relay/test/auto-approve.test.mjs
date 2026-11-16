// Tests for the desktop-side auto-approval rules, matcher, listener ordering and
// phone-facing HTTP API.
//
// Run: node --test relay/test/auto-approve.test.mjs (also part of `npm test`).
import assert from 'node:assert/strict'
import fs from 'node:fs'
import http from 'node:http'
import os from 'node:os'
import path from 'node:path'
import test from 'node:test'

import {
  AUTO_APPROVE_PATH,
  AutoApproveValidationError,
  createAutoApproveService,
  createAutoApproveStore,
  matchAutoApproveRequest,
  normalizeAutoApproveDocument,
  normalizeWorkspacePath
} from '../src/auto-approve.mjs'
import { UpstreamProxy } from '../src/proxy.mjs'

const TOKEN = 'pairing-token-for-tests'

function tempFile (name = 'auto-approve.json') {
  const dir = fs.mkdtempSync(path.join(os.tmpdir(), 'dsh-auto-approve-'))
  return { dir, file: path.join(dir, name) }
}

function rules (overrides = {}) {
  return normalizeAutoApproveDocument({
    enabled: true,
    all: false,
    workspaces: [],
    sessions: [],
    ...overrides
  })
}

function approvalFor ({ sessionId = 'session-1', cwd = 'D:\\Code\\repo', parentSession = null, toolName = 'pwsh' } = {}) {
  return {
    toolName,
    callId: 'call-1',
    agent: {
      session: {
        id: sessionId,
        header: { id: sessionId, cwd, parentSession }
      }
    }
  }
}

// ---- document normalization -------------------------------------------------

test('normalizeAutoApproveDocument defaults to disabled and rejects malformed fields', () => {
  const empty = normalizeAutoApproveDocument({})
  assert.equal(empty.enabled, false)
  assert.equal(empty.all, false)
  assert.deepEqual(empty.workspaces, [])
  assert.deepEqual(empty.sessions, [])
  assert.equal(empty.schemaVersion, 2)

  assert.throws(() => normalizeAutoApproveDocument({ workspaces: 'nope' }), AutoApproveValidationError)
  assert.throws(() => normalizeAutoApproveDocument({ sessions: {} }), AutoApproveValidationError)
  assert.throws(() => normalizeAutoApproveDocument([]), AutoApproveValidationError)
})

test('a v1 document is migrated into the tree shape', () => {
  const all = normalizeAutoApproveDocument({ schemaVersion: 1, enabled: true, mode: 'all' })
  assert.equal(all.all, true)
  assert.deepEqual(all.workspaces, [])
  assert.deepEqual(all.sessions, [])

  const selected = normalizeAutoApproveDocument({
    schemaVersion: 1,
    enabled: true,
    mode: 'workspaces',
    workspaces: [{ id: 'ws-1', path: 'D:\\repo' }]
  })
  assert.equal(selected.all, false)
  assert.equal(selected.workspaces[0].id, 'ws-1')
})

test('normalizeAutoApproveDocument trims, dedupes and caps fields', () => {
  const doc = normalizeAutoApproveDocument({
    enabled: true,
    all: false,
    workspaces: [
      { id: ' ws-1 ', path: 'D:\\Code\\repo', title: 'repo' },
      { id: 'ws-1', path: 'D:\\Code\\repo', title: 'duplicate' }
    ],
    sessions: [
      { id: 'session-1', title: 'one' },
      { id: 'session-1', title: 'duplicate' },
      { id: 'session-2' }
    ]
  })
  assert.equal(doc.workspaces.length, 1)
  assert.equal(doc.workspaces[0].id, 'ws-1')
  assert.equal(doc.sessions.length, 2)
  assert.equal(doc.sessions[0].title, 'one')
})

test('normalizeWorkspacePath compares Windows paths case-insensitively', () => {
  if (process.platform !== 'win32') {
    assert.equal(normalizeWorkspacePath('/Code/Repo/'), '/Code/Repo')
    return
  }
  assert.equal(normalizeWorkspacePath('D:\\Code\\Repo\\'), 'd:\\code\\repo')
  assert.equal(normalizeWorkspacePath('D:/Code/Repo'), 'd:\\code\\repo')
  assert.equal(normalizeWorkspacePath('  '), '')
})

// ---- matcher ----------------------------------------------------------------

test('a fully selected workspace matches by its durable id resolved from cwd', async () => {
  const doc = rules({ workspaces: [{ id: 'ws-1', path: 'D:\\Code\\repo' }] })
  const hit = await matchAutoApproveRequest(approvalFor(), doc, {
    resolveWorkspaceId: async (cwd) => (cwd === 'D:\\Code\\repo' ? 'ws-1' : null)
  })
  assert.deepEqual(hit, { rule: 'workspace:ws-1' })
})

test('a fully selected workspace falls back to the normalized cwd when the registry is unavailable', async () => {
  const doc = rules({ workspaces: [{ id: 'ws-1', path: 'D:\\Code\\Repo' }] })
  const hit = await matchAutoApproveRequest(approvalFor({ cwd: 'd:/code/repo/' }), doc, {
    resolveWorkspaceId: async () => { throw new Error('registry offline') }
  })
  assert.equal(hit.rule.startsWith('workspace-path:'), true)
})

test('a fully selected workspace ignores unrelated workspaces', async () => {
  const doc = rules({ workspaces: [{ id: 'ws-1', path: 'D:\\Code\\repo' }] })
  const hit = await matchAutoApproveRequest(approvalFor({ cwd: 'D:\\Code\\other' }), doc)
  assert.equal(hit, null)
})

test('individually selected sessions cover the session and its subagent descendants', async () => {
  const doc = rules({
    sessions: [{ id: 'parent-session' }]
  })
  const headers = new Map([
    ['parent-session', { id: 'parent-session', cwd: 'D:\\Code\\repo' }],
    ['child-session', { id: 'child-session', cwd: 'D:\\Code\\repo', parentSession: 'parent-session' }]
  ])
  const direct = await matchAutoApproveRequest(approvalFor({ sessionId: 'parent-session' }), doc, { loadSessionHeaders: () => headers })
  assert.deepEqual(direct, { rule: 'session:parent-session' })

  const child = await matchAutoApproveRequest(approvalFor({ sessionId: 'child-session', parentSession: 'parent-session' }), doc, { loadSessionHeaders: () => headers })
  assert.deepEqual(child, { rule: 'session:parent-session' })

  const grandchild = await matchAutoApproveRequest(approvalFor({ sessionId: 'grandchild', parentSession: 'child-session' }), doc, {
    loadSessionHeaders: () => new Map([...headers, ['grandchild', { id: 'grandchild', cwd: 'D:\\Code\\repo', parentSession: 'child-session' }]])
  })
  assert.deepEqual(grandchild, { rule: 'session:parent-session' })
})

test('individually selected sessions do not match unrelated sessions or an empty selection', async () => {
  const doc = rules({ sessions: [{ id: 'parent-session' }] })
  assert.equal(await matchAutoApproveRequest(approvalFor({ sessionId: 'other' }), doc), null)
  assert.equal(await matchAutoApproveRequest(approvalFor(), rules({ sessions: [] })), null)
})

test('the all flag covers every session; disabled rules cover none', async () => {
  assert.deepEqual(await matchAutoApproveRequest(approvalFor(), rules({ all: true })), { rule: 'all' })
  assert.equal(await matchAutoApproveRequest(approvalFor(), rules({ enabled: false, all: true })), null)
})

test('a partially selected workspace matches only its checked session ids', async () => {
  // Workspace node unchecked, one session checked: the id match must still win,
  // while a sibling session in the same workspace stays manual.
  const doc = rules({ workspaces: [], sessions: [{ id: 'session-1' }] })
  assert.deepEqual(await matchAutoApproveRequest(approvalFor(), doc), { rule: 'session:session-1' })
  assert.equal(await matchAutoApproveRequest(approvalFor({ sessionId: 'session-2' }), doc), null)
})

test('requests without an agent session are never matched', async () => {
  assert.equal(await matchAutoApproveRequest({ toolName: 'pwsh' }, rules({ all: true })), null)
})

// ---- store ------------------------------------------------------------------

test('store saves atomically, assigns revisions and reloads from disk', () => {
  const { file } = tempFile()
  const store = createAutoApproveStore({ file })
  assert.equal(store.get().enabled, false)

  const saved = store.save({ enabled: true, all: false, workspaces: [{ id: 'ws-1', path: 'D:\\repo' }] })
  assert.equal(saved.enabled, true)
  assert.equal(saved.revision, 1)
  assert.equal(saved.updatedBy, 'phone')
  assert.equal(fs.statSync(file).isFile(), true)

  const second = createAutoApproveStore({ file })
  assert.equal(second.get().enabled, true)
  assert.equal(second.get().revision, 1)
  assert.equal(second.get().workspaces[0].path, 'D:\\repo')

  const next = store.save({ ...saved, sessions: [{ id: 'session-1' }] })
  assert.equal(next.revision, 2)
})

test('a malformed rules file disables auto-approval instead of keeping stale grants', () => {
  const { file } = tempFile()
  fs.writeFileSync(file, '{ not json')
  const store = createAutoApproveStore({ file })
  const snapshot = store.get()
  assert.equal(snapshot.enabled, false)
  assert.equal(snapshot.workspaces.length, 0)
  assert.match(snapshot.error, /invalid/)
})

// ---- listener + ordering ----------------------------------------------------

/** Minimal Cordis-like waterfall with prepend semantics. */
function fakeCtx () {
  const listeners = []
  return {
    listeners,
    on (_event, listener, options = {}) {
      if (options && options.prepend) listeners.unshift(listener)
      else listeners.push(listener)
      return () => {
        const index = listeners.indexOf(listener)
        if (index >= 0) listeners.splice(index, 1)
      }
    },
    async dispatch (request) {
      let index = 0
      const next = async () => {
        if (index >= listeners.length) return 'unavailable'
        const listener = listeners[index]
        index += 1
        return listener(request, next)
      }
      return next()
    },
    sessions: {
      list: () => []
    },
    workspaceRegistry: {
      resolveByPath: async () => null
    }
  }
}

test('the prepended listener settles covered approvals before the remote forwarder', async () => {
  const { file } = tempFile()
  const ctx = fakeCtx()
  const service = createAutoApproveService({ ctx, file, tokens: () => [TOKEN] })
  const dispose = service.install()
  const forwarded = []
  ctx.on('approval/request', async (request, next) => {
    forwarded.push(request.toolName)
    return next()
  })

  service.api.save({ enabled: true, all: true })
  const outcome = await ctx.dispatch(approvalFor())
  assert.equal(outcome, 'allowed-once')
  assert.deepEqual(forwarded, [])
  assert.equal(service.summary().stats.allowed, 1)
  assert.equal(service.status().recent[0].toolName, 'pwsh')

  dispose()
  assert.equal(ctx.listeners.length, 1)
})

test('uncovered approvals are delegated and a matcher failure falls back to manual approval', async () => {
  const { file } = tempFile()
  const ctx = fakeCtx()
  const service = createAutoApproveService({ ctx, file })
  const dispose = service.install()
  ctx.on('approval/request', async () => 'rejected')
  service.api.save({ enabled: true, all: false, workspaces: [{ id: 'ws-1', path: 'D:\\other' }] })

  assert.equal(await ctx.dispatch(approvalFor()), 'rejected')

  // A request whose session object throws must not break the approval chain.
  const throwing = fakeCtx()
  const service2 = createAutoApproveService({ ctx: throwing, file: tempFile().file })
  const dispose2 = service2.install()
  throwing.on('approval/request', async () => 'allowed-by-user')
  service2.api.save({ enabled: true, all: true, sessions: [] })
  const broken = {
    toolName: 'pwsh',
    agent: {
      get session () {
        throw new Error('boom')
      }
    }
  }
  assert.equal(await throwing.dispatch(broken), 'allowed-by-user')
  assert.equal(service2.summary().stats.errors, 1)
  dispose()
  dispose2()
})

// ---- HTTP API ---------------------------------------------------------------

function fakeAutoApproveApi () {
  let doc = normalizeAutoApproveDocument({ enabled: false })
  return {
    tokens: () => [TOKEN],
    status: () => ({
      schemaVersion: 2,
      enabled: doc.enabled,
      all: doc.all,
      workspaces: doc.workspaces,
      sessions: doc.sessions,
      revision: doc.revision,
      stats: { allowed: 0, errors: 0, lastAllowedAt: null },
      recent: [],
      error: null
    }),
    save: (body) => {
      doc = normalizeAutoApproveDocument(body)
      return { ...doc }
    }
  }
}

async function withProxy (options, fn) {
  const proxy = new UpstreamProxy({
    upstreamPort: 1,
    helpers: options.helpers ?? 'all',
    autoApproveApi: options.autoApproveApi
  })
  const server = http.createServer((req, res) => {
    proxy.handleRequest(req, res).catch(() => {
      try { res.writeHead(500); res.end('test proxy failure') } catch {}
    })
  })
  await new Promise((resolve) => server.listen(0, '127.0.0.1', resolve))
  const { port } = server.address()
  try {
    await fn({
      base: `http://127.0.0.1:${port}`,
      get: (path, headers) => fetch(`http://127.0.0.1:${port}${path}`, { headers }),
      put: (path, body, headers) => fetch(`http://127.0.0.1:${port}${path}`, {
        method: 'PUT',
        headers: { 'content-type': 'application/json', ...(headers ?? {}) },
        body: JSON.stringify(body ?? {})
      })
    })
  } finally {
    await new Promise((resolve) => server.close(resolve))
  }
}

test('auto-approve API requires the pairing token outside loopback trust', async () => {
  await withProxy({ autoApproveApi: fakeAutoApproveApi(), helpers: 'health' }, async ({ get }) => {
    assert.equal((await get(AUTO_APPROVE_PATH)).status, 401)
    const good = await get(AUTO_APPROVE_PATH, { authorization: `Bearer ${TOKEN}` })
    assert.equal(good.status, 200)
    const payload = await good.json()
    assert.equal(payload.ok, true)
    assert.equal(payload.enabled, false)
  })
})

test('auto-approve API is trusted on the loopback management listener', async () => {
  await withProxy({ autoApproveApi: fakeAutoApproveApi(), helpers: 'all' }, async ({ get }) => {
    assert.equal((await get(AUTO_APPROVE_PATH)).status, 200)
  })
})

test('auto-approve API PUT validates and persists rules', async () => {
  await withProxy({ autoApproveApi: fakeAutoApproveApi(), helpers: 'health' }, async ({ put }) => {
    const auth = { authorization: `Bearer ${TOKEN}` }
    const saved = await put(AUTO_APPROVE_PATH, { enabled: true, all: false, sessions: [{ id: 'session-1' }] }, auth)
    assert.equal(saved.status, 200)
    const payload = await saved.json()
    assert.equal(payload.enabled, true)
    assert.equal(payload.all, false)
    assert.equal(payload.sessions[0].id, 'session-1')

    const invalid = await put(AUTO_APPROVE_PATH, { workspaces: 'nope' }, auth)
    assert.equal(invalid.status, 400)
  })
})

test('auto-approve API rejects unsupported methods', async () => {
  await withProxy({ autoApproveApi: fakeAutoApproveApi(), helpers: 'health' }, async ({ base }) => {
    const response = await fetch(`${base}${AUTO_APPROVE_PATH}`, {
      method: 'DELETE',
      headers: { authorization: `Bearer ${TOKEN}` }
    })
    assert.equal(response.status, 405)
  })
})
