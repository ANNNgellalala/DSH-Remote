// Integration tests for the loopback-only manual self-check endpoint
// (`POST /__relay/probe`, relay v0.6.1+).
//
// The relay exposes this route so an operator (or the pairing page button) can
// re-run the DSH protocol self-check after a transient failure, without
// restarting DSH. These tests pin its contract: method and loopback gating,
// the exact JSON field set (no credentials), error mapping and the fact that
// the gateway's internal plain proxy never serves it.
//
// Run: node --test relay/test/self-check.test.mjs   (or: cd relay && npm test)
import assert from 'node:assert/strict'
import http from 'node:http'
import test from 'node:test'

import { SELF_CHECK_PATH } from '../src/pair-page.mjs'
import { UpstreamProxy } from '../src/proxy.mjs'

/** The wrapper shape relay's runSelfCheck returns (health.protocolProbe). */
function fakeProbe (overrides = {}) {
  return {
    ok: true,
    status: 'ok',
    checkedAt: '2026-11-13T00:00:00.000Z',
    attempts: 1,
    appReady: true,
    required: ['$connect', 'session/list', 'session/events', '$events', 'session/follow'],
    capabilities: { $connect: true, 'session/list': true, 'session/events': true, $events: true, 'session/follow': true },
    missing: [],
    errors: [],
    ...overrides
  }
}

/** Serve one proxy instance over an ephemeral loopback port. */
async function withProxy (options, fn) {
  const proxy = new UpstreamProxy({
    upstreamPort: 1, // never contacted: the self-check route answers first
    helpers: options.helpers ?? 'all',
    probeProvider: options.probeProvider
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
  const { port } = server.address()
  try {
    await fn({
      base: `http://127.0.0.1:${port}`,
      get: (path) => fetch(`http://127.0.0.1:${port}${path}`),
      head: (path) => fetch(`http://127.0.0.1:${port}${path}`, { method: 'HEAD' }),
      post: (path, body) => fetch(`http://127.0.0.1:${port}${path}`, {
        method: 'POST',
        headers: { 'content-type': 'application/json' },
        body: JSON.stringify(body ?? {})
      })
    })
  } finally {
    await new Promise((resolve) => server.close(resolve))
  }
}

/** Minimal response double for calling handleLoopbackRoute directly. */
function fakeResponse () {
  const state = { status: null, headers: null, body: null, ended: false }
  return {
    state,
    writeHead (status, headers) {
      state.status = status
      state.headers = headers ?? null
      return this
    },
    end (body) {
      state.ended = true
      state.body = body === undefined ? '' : String(body)
      return this
    }
  }
}

function fakeRequest ({ method = 'POST', url = SELF_CHECK_PATH, remoteAddress = '127.0.0.1' } = {}) {
  return {
    method,
    url,
    headers: {},
    socket: { remoteAddress },
    resume () {}
  }
}

test('POST /__relay/probe re-runs the self-check and returns the fresh state', async () => {
  let calls = 0
  await withProxy({
    probeProvider: async () => {
      calls++
      return fakeProbe({ attempts: 2 })
    }
  }, async ({ post }) => {
    const response = await post(SELF_CHECK_PATH)
    assert.equal(response.status, 200)
    const payload = await response.json()
    assert.equal(calls, 1)
    assert.equal(payload.ok, true)
    assert.equal(payload.status, 'ok')
    assert.equal(payload.attempts, 2)
    assert.equal(payload.appReady, true)
    assert.deepEqual(payload.missing, [])
    assert.deepEqual(payload.capabilities, fakeProbe().capabilities)
  })
})

test('the response never leaks credentials or upstream session ids', async () => {
  await withProxy({
    probeProvider: async () => fakeProbe({
      token: 'launch-token-must-not-leak',
      remoteToken: 'remote-token-must-not-leak',
      secret: 'gateway-secret-must-not-leak',
      sessionSample: 'session-must-not-leak'
    })
  }, async ({ post }) => {
    const response = await post(SELF_CHECK_PATH)
    const text = await response.text()
    assert.equal(response.status, 200)
    assert.ok(!text.includes('must-not-leak'), `response leaked a secret: ${text}`)
    assert.deepEqual(Object.keys(JSON.parse(text)).sort(), [
      'appReady', 'attempts', 'capabilities', 'checkedAt', 'errors', 'missing', 'ok', 'status'
    ])
  })
})

test('a rerun that still fails reports ok=false with the missing capabilities', async () => {
  await withProxy({
    probeProvider: async () => fakeProbe({
      ok: false,
      status: 'failed',
      capabilities: { $connect: true, 'session/list': true, 'session/events': true, $events: false, 'session/follow': false },
      missing: ['$events', 'session/follow'],
      errors: ['mux upgrade: socket hang up'],
      attempts: 5
    })
  }, async ({ post }) => {
    const response = await post(SELF_CHECK_PATH)
    assert.equal(response.status, 200)
    const payload = await response.json()
    assert.equal(payload.ok, false)
    assert.equal(payload.status, 'failed')
    assert.deepEqual(payload.missing, ['$events', 'session/follow'])
    assert.equal(payload.attempts, 5)
  })
})

test('GET and HEAD are rejected with 405 and an explicit Allow header', async () => {
  await withProxy({ probeProvider: async () => fakeProbe() }, async ({ get, head }) => {
    for (const response of [await get(SELF_CHECK_PATH), await head(SELF_CHECK_PATH)]) {
      assert.equal(response.status, 405)
      assert.equal(response.headers.get('allow'), 'POST')
    }
  })
})

test('a relay that is shutting down answers 503', async () => {
  await withProxy({ probeProvider: async () => null }, async ({ post }) => {
    const response = await post(SELF_CHECK_PATH)
    assert.equal(response.status, 503)
    const payload = await response.json()
    assert.equal(payload.ok, false)
    assert.equal(payload.error, 'relay is not running; reload the plugin and retry')
  })
})

test('a probe rejection maps to 500 with a JSON error', async () => {
  await withProxy({ probeProvider: async () => { throw new Error('probe exploded') } }, async ({ post }) => {
    const response = await post(SELF_CHECK_PATH)
    assert.equal(response.status, 500)
    const payload = await response.json()
    assert.equal(payload.ok, false)
    assert.match(payload.error, /probe exploded/)
  })
})

test('without a probe provider the route reports 501', async () => {
  await withProxy({}, async ({ post }) => {
    const response = await post(SELF_CHECK_PATH)
    assert.equal(response.status, 501)
    assert.equal((await response.json()).ok, false)
  })
})

test('the gateway internal listener (helpers=health) never serves the endpoint', async () => {
  let called = 0
  await withProxy({
    helpers: 'health',
    probeProvider: async () => {
      called++
      return fakeProbe()
    }
  }, async ({ post }) => {
    const response = await post(SELF_CHECK_PATH)
    assert.equal(response.status, 502, 'the request must be proxied upstream, not answered locally')
    assert.equal(called, 0)
  })
})

test('non-loopback peers get 404 even for POST', async () => {
  let called = 0
  const proxy = new UpstreamProxy({
    upstreamPort: 1,
    helpers: 'all',
    probeProvider: async () => {
      called++
      return fakeProbe()
    }
  })
  const response = fakeResponse()
  const handled = await proxy.handleLoopbackRoute(fakeRequest({ remoteAddress: '192.0.2.50' }), response)
  assert.equal(handled, true)
  assert.equal(response.state.status, 404)
  assert.equal(called, 0)
})
