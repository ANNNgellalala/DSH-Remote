// Integration tests for the phone-facing compact session list
// (`GET /__relay/sessions/summary`, relay v0.6.0+).
//
// A fake DSH upstream HTTP server stands in for the desktop; the real
// UpstreamProxy + createSessionSummaryApi are exercised end to end, so the
// tests cover auth, routing, trimming, subagent filtering, detail mode, the
// upstream RPC envelope and error mapping without a real DSH install.
//
// Run: node --test relay/test/   (or: cd relay && npm test)
import assert from 'node:assert/strict'
import http from 'node:http'
import test from 'node:test'

import { SESSION_SUMMARY_PATH, UpstreamProxy } from '../src/proxy.mjs'
import {
  SUMMARY_FIELDS,
  SUMMARY_SCHEMA,
  buildSummaryPayload,
  createSessionSummaryApi,
  findSessionItem,
  isSubagentSession,
  trimSessionSummary
} from '../src/session-summary.mjs'

const TOKEN = 'pairing-token-for-tests'
const COOKIE = 'dsh-auth-test=v2.cookie-value'
const AGENT_TEAM_MARKER = 'AGENT_TEAM_MARKER_do_not_leak'

/** A fat projection the phone list never renders; marks the raw payload. */
function fatProjections (title, { blank = false, lastPromptAt = 1794154489599, model = 'deepseek-v4.1-flash', withLastUsed = true } = {}) {
  return {
    kind: 'sequenced',
    asOfSeq: 27,
    values: {
      title,
      modelSelection: {
        ...(withLastUsed ? { lastUsed: { provider: 'deepseek', model, reasoningEffort: 'high' } } : {}),
        next: { provider: 'deepseek', model: 'deepseek-v4.1-next' }
      },
      workingDirectory: 'C:\\Code\\fallback-cwd',
      sessionListMetadata: { blank, lastPromptAt },
      inbox: { items: Array.from({ length: 40 }, (_, i) => ({ id: `inbox-${i}`, text: 'x'.repeat(200) })) },
      permissions: { rules: Array.from({ length: 20 }, (_, i) => ({ id: `rule-${i}`, note: AGENT_TEAM_MARKER })) },
      tokenUsage: { input: 123456, output: 654321 },
      todos: null,
      agentTeam: {
        marker: AGENT_TEAM_MARKER,
        members: Array.from({ length: 30 }, (_, i) => ({ id: `agent-${i}`, prompt: 'p'.repeat(300) }))
      }
    }
  }
}

function rootSession (overrides = {}) {
  return {
    sessionId: 'session-root-1',
    updatedAt: 1794154489599,
    agentAvailable: true,
    formatStatus: 'current',
    running: true,
    blank: false,
    cwd: 'C:\\Code\\repo',
    projections: fatProjections('排查DSH运行系统卡顿'),
    ...overrides
  }
}

function subagentSession (overrides = {}) {
  return {
    sessionId: 'session-subagent-1',
    updatedAt: 1794154489000,
    agentAvailable: false,
    formatStatus: 'current',
    running: false,
    blank: false,
    parentSessionId: 'session-root-1',
    origin: 'subagent',
    projections: fatProjections('子代理：搜索资料', { withLastUsed: false }),
    ...overrides
  }
}

function envelope (value) {
  return JSON.stringify({ type: 'server-response', rpcId: 'test-rpc', result: { ok: true, value } })
}

/** Fake DSH upstream: records every request and answers via `respond`. */
async function withUpstream (respond, fn) {
  const requests = []
  const server = http.createServer((req, res) => {
    const chunks = []
    req.on('data', (chunk) => chunks.push(chunk))
    req.on('end', () => {
      const record = {
        method: req.method,
        url: req.url,
        headers: req.headers,
        body: Buffer.concat(chunks).toString('utf8')
      }
      requests.push(record)
      respond(req, res, record)
    })
  })
  await new Promise((resolve) => server.listen(0, '127.0.0.1', resolve))
  const port = server.address().port
  try {
    await fn({ port, requests })
  } finally {
    server.closeAllConnections?.()
    await new Promise((resolve) => server.close(resolve))
  }
}

/** Serve one UpstreamProxy over an ephemeral loopback port. */
async function withProxy ({ upstreamPort, helpers = 'health' }, fn) {
  const proxy = new UpstreamProxy({
    upstreamPort,
    helpers,
    aclApi: { tokens: () => [TOKEN], status: () => ({}), check: async () => ({}) },
    sessionSummaryApi: createSessionSummaryApi({ upstreamPort })
  })
  const server = http.createServer((req, res) => {
    proxy.handleRequest(req, res).catch(() => {
      try {
        res.writeHead(500)
        res.end('test proxy failure')
      } catch {}
    })
  })
  await new Promise((resolve) => server.listen(0, '127.0.0.1', resolve))
  const port = server.address().port
  const base = `http://127.0.0.1:${port}`
  const get = (path, headers = {}) => fetch(`${base}${path}`, { headers })
  try {
    await fn({ base, get })
  } finally {
    server.closeAllConnections?.()
    proxy.close()
    await new Promise((resolve) => server.close(resolve))
  }
}

function authHeaders (extra = {}) {
  return { authorization: `Bearer ${TOKEN}`, ...extra }
}

function sendJson (res, status, value) {
  const body = Buffer.from(JSON.stringify(value), 'utf8')
  res.writeHead(status, { 'content-type': 'application/json; charset=utf-8', 'content-length': body.length })
  res.end(body)
}

test('pure helpers trim, filter and locate sessions', () => {
  const root = rootSession()
  const subagent = subagentSession()
  assert.equal(isSubagentSession(root), false)
  assert.equal(isSubagentSession(subagent), true)

  const trimmed = trimSessionSummary(root)
  assert.deepEqual(Object.keys(trimmed), [...SUMMARY_FIELDS])
  assert.equal(trimmed.sessionId, 'session-root-1')
  assert.equal(trimmed.title, '排查DSH运行系统卡顿')
  assert.equal(trimmed.model, 'deepseek-v4.1-flash')
  assert.equal(trimmed.cwd, 'C:\\Code\\repo')
  assert.equal(trimmed.lastPromptAt, 1794154489599)
  assert.equal(trimmed.parentSessionId, null)
  assert.equal(trimmed.origin, null)

  // modelSelection.lastUsed missing -> next.model; cwd missing -> workingDirectory.
  const fallback = trimSessionSummary({
    sessionId: 'session-fallback',
    agentAvailable: false,
    running: false,
    projections: fatProjections('fallback', { withLastUsed: false, blank: true })
  })
  assert.equal(fallback.model, 'deepseek-v4.1-next')
  assert.equal(fallback.cwd, 'C:\\Code\\fallback-cwd')
  assert.equal(fallback.blank, true)

  const payload = buildSummaryPayload({ cursor: 42, items: [root, subagent] }, { generatedAt: '2026-11-12T00:00:00.000Z' })
  assert.equal(payload.schema, SUMMARY_SCHEMA)
  assert.deepEqual(payload.counts, { total: 2, hiddenSubagents: 1, returned: 1 })
  assert.equal(payload.cursor, 42)
  assert.equal(payload.generatedAt, '2026-11-12T00:00:00.000Z')
  assert.deepEqual(findSessionItem({ items: [root, subagent] }, 'session-subagent-1'), subagent)
  assert.equal(findSessionItem({ items: [root] }, 'session-missing'), null)
})

test('summary returns schema v1 fields, hides subagents and reports counts', async () => {
  const items = [rootSession(), rootSession({ sessionId: 'session-root-2', projections: fatProjections('第二个会话') }), subagentSession()]
  await withUpstream((req, res) => {
    sendJson(res, 200, JSON.parse(envelope({ cursor: 7, items })))
  }, async ({ port }) => {
    await withProxy({ upstreamPort: port }, async ({ get }) => {
      const response = await get(SESSION_SUMMARY_PATH, authHeaders())
      assert.equal(response.status, 200)
      assert.match(String(response.headers.get('content-type')), /application\/json/)
      const payload = await response.json()

      assert.equal(payload.ok, true)
      assert.equal(payload.schema, 1)
      assert.deepEqual(payload.fields, [...SUMMARY_FIELDS])
      assert.ok(Number.isFinite(Date.parse(payload.generatedAt)))
      assert.equal(payload.cursor, 7)
      assert.deepEqual(payload.counts, { total: 3, hiddenSubagents: 1, returned: 2 })
      assert.equal(payload.items.length, 2)
      for (const item of payload.items) assert.deepEqual(Object.keys(item), [...SUMMARY_FIELDS])
      assert.deepEqual(payload.items.map((item) => item.sessionId), ['session-root-1', 'session-root-2'])
      assert.equal(payload.items[0].title, '排查DSH运行系统卡顿')
      assert.equal(payload.items[0].model, 'deepseek-v4.1-flash')
      assert.equal(payload.items[0].cwd, 'C:\\Code\\repo')
      assert.equal(payload.items[0].updatedAt, 1794154489599)
      assert.equal(payload.items[0].running, true)
      assert.equal(payload.items[0].agentAvailable, true)
      assert.equal(payload.items[0].lastPromptAt, 1794154489599)
      assert.equal(payload.items[0].parentSessionId, null)
      assert.equal(payload.items[0].origin, null)
    })
  })
})

test('includeSubagents=1 keeps subagent sessions', async () => {
  await withUpstream((req, res) => {
    sendJson(res, 200, JSON.parse(envelope({ items: [rootSession(), subagentSession()] })))
  }, async ({ port }) => {
    await withProxy({ upstreamPort: port }, async ({ get }) => {
      const response = await get(`${SESSION_SUMMARY_PATH}?includeSubagents=1`, authHeaders())
      assert.equal(response.status, 200)
      const payload = await response.json()
      assert.deepEqual(payload.counts, { total: 2, hiddenSubagents: 0, returned: 2 })
      const subagent = payload.items.find((item) => item.sessionId === 'session-subagent-1')
      assert.ok(subagent)
      assert.equal(subagent.parentSessionId, 'session-root-1')
      assert.equal(subagent.origin, 'subagent')
    })
  })
})

test('detail returns the selected upstream item verbatim (untrimmed)', async () => {
  const items = [rootSession(), subagentSession()]
  await withUpstream((req, res) => {
    sendJson(res, 200, JSON.parse(envelope({ items })))
  }, async ({ port }) => {
    await withProxy({ upstreamPort: port }, async ({ get }) => {
      const response = await get(`${SESSION_SUMMARY_PATH}?sessionId=session-root-1`, authHeaders())
      assert.equal(response.status, 200)
      const payload = await response.json()
      assert.equal(payload.ok, true)
      assert.equal(payload.schema, 1)
      assert.deepEqual(payload.item, items[0])
      // Detail is the extension point for per-session data, so fat fields stay.
      assert.equal(payload.item.projections.values.agentTeam.marker, AGENT_TEAM_MARKER)

      const missing = await get(`${SESSION_SUMMARY_PATH}?sessionId=session-nope`, authHeaders())
      assert.equal(missing.status, 404)
      assert.deepEqual(await missing.json(), { ok: false, error: 'not-found' })
    })
  })
})

test('the upstream call is the official unary session/list envelope with the phone cookie', async () => {
  await withUpstream((req, res) => {
    sendJson(res, 200, JSON.parse(envelope({ items: [rootSession()] })))
  }, async ({ port, requests }) => {
    await withProxy({ upstreamPort: port }, async ({ get }) => {
      const response = await get(SESSION_SUMMARY_PATH, authHeaders({ cookie: COOKIE }))
      assert.equal(response.status, 200)
      assert.equal(requests.length, 1)
      const [request] = requests
      assert.equal(request.method, 'POST')
      assert.equal(request.url, '/api/session/list')
      assert.equal(request.headers.cookie, COOKIE)
      assert.equal(request.headers.host, `127.0.0.1:${port}`)
      assert.equal(request.headers['content-type'], 'application/json')
      const body = JSON.parse(request.body)
      assert.equal(body.type, 'client-request')
      assert.equal(body.method, 'session/list')
      assert.deepEqual(body.payload, { args: { _request: {} } })
      assert.equal(typeof body.rpcId, 'string')
    })
  })
})

test('summary requires a bearer credential on the gateway internal listener', async () => {
  await withUpstream((req, res) => {
    sendJson(res, 200, JSON.parse(envelope({ items: [rootSession()] })))
  }, async ({ port }) => {
    await withProxy({ upstreamPort: port, helpers: 'health' }, async ({ get }) => {
      const anonymous = await get(SESSION_SUMMARY_PATH)
      assert.equal(anonymous.status, 401)
      assert.deepEqual(await anonymous.json(), { ok: false, error: 'unauthorized' })

      const wrong = await get(SESSION_SUMMARY_PATH, { authorization: 'Bearer nope' })
      assert.equal(wrong.status, 401)

      const good = await get(SESSION_SUMMARY_PATH, authHeaders())
      assert.equal(good.status, 200)
    })
  })
})

test('loopback management listener is trusted without a token (helpers=all)', async () => {
  await withUpstream((req, res) => {
    sendJson(res, 200, JSON.parse(envelope({ items: [rootSession()] })))
  }, async ({ port }) => {
    await withProxy({ upstreamPort: port, helpers: 'all' }, async ({ get }) => {
      const response = await get(SESSION_SUMMARY_PATH)
      assert.equal(response.status, 200)
      assert.equal((await response.json()).counts.returned, 1)
    })
  })
})

test('summary rejects unsupported methods with 405', async () => {
  await withUpstream((req, res) => {
    sendJson(res, 200, JSON.parse(envelope({ items: [] })))
  }, async ({ port }) => {
    await withProxy({ upstreamPort: port }, async ({ base }) => {
      const response = await fetch(`${base}${SESSION_SUMMARY_PATH}`, {
        method: 'POST',
        headers: authHeaders({ 'content-type': 'application/json' }),
        body: '{}'
      })
      assert.equal(response.status, 405)
      assert.deepEqual(await response.json(), { ok: false, error: 'method not allowed' })
      assert.match(String(response.headers.get('allow')), /GET/)
    })
  })
})

test('upstream HTTP failures map to 502 upstream-failed', async () => {
  await withUpstream((req, res) => {
    sendJson(res, 500, { type: 'server-response', rpcId: 'test-rpc', result: { ok: false, error: { code: 'gateway/transport', message: 'boom' } } })
  }, async ({ port }) => {
    await withProxy({ upstreamPort: port }, async ({ get }) => {
      const response = await get(SESSION_SUMMARY_PATH, authHeaders())
      assert.equal(response.status, 502)
      assert.deepEqual(await response.json(), { ok: false, error: 'upstream-failed' })
    })
  })
})

test('an ok:false upstream result also maps to 502', async () => {
  await withUpstream((req, res) => {
    sendJson(res, 200, {
      type: 'server-response',
      rpcId: 'test-rpc',
      result: { ok: false, error: { code: 'gateway/peer-disposed', message: 'cookie expired' } }
    })
  }, async ({ port }) => {
    await withProxy({ upstreamPort: port }, async ({ get }) => {
      const response = await get(SESSION_SUMMARY_PATH, authHeaders())
      assert.equal(response.status, 502)
      assert.deepEqual(await response.json(), { ok: false, error: 'upstream-failed' })
    })
  })
})

test('large agentTeam/unknown projections never reach the phone list', async () => {
  const items = Array.from({ length: 20 }, (_, i) => rootSession({ sessionId: `session-${i}` }))
  await withUpstream((req, res) => {
    sendJson(res, 200, JSON.parse(envelope({ items })))
  }, async ({ port }) => {
    await withProxy({ upstreamPort: port }, async ({ get }) => {
      const response = await get(SESSION_SUMMARY_PATH, authHeaders())
      const text = await response.text()
      assert.equal(response.status, 200)
      for (const marker of [AGENT_TEAM_MARKER, 'agentTeam', 'tokenUsage', 'inbox', 'permissions', 'formatStatus', 'projections']) {
        assert.equal(text.includes(marker), false, `summary payload leaked ${marker}`)
      }
    })
  })
})

test('summary payload is ~92% smaller than the upstream list (measured estimate)', async (t) => {
  const items = Array.from({ length: 241 }, (_, i) => rootSession({ sessionId: `session-${i}` }))
  const rawValue = { cursor: 999, items }
  const rawBytes = Buffer.byteLength(JSON.stringify(rawValue))
  await withUpstream((req, res) => {
    sendJson(res, 200, JSON.parse(envelope(rawValue)))
  }, async ({ port }) => {
    await withProxy({ upstreamPort: port }, async ({ get }) => {
      const response = await get(SESSION_SUMMARY_PATH, authHeaders())
      const text = await response.text()
      const summaryBytes = Buffer.byteLength(text)
      const ratio = summaryBytes / rawBytes
      t.diagnostic(`241 sessions: raw=${(rawBytes / 1024).toFixed(1)} KiB summary=${(summaryBytes / 1024).toFixed(1)} KiB ratio=${(ratio * 100).toFixed(1)}%`)
      assert.ok(ratio < 0.15, `expected a large reduction, got ${(ratio * 100).toFixed(1)}%`)
      assert.equal(JSON.parse(text).counts.total, 241)
    })
  })
})
