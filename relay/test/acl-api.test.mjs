// Unit/integration tests for the relay-owned Windows ACL guard API
// (`/__relay/acl/status`, `/__relay/acl/check`) and its authentication rules.
//
// Run: node --test relay/test/   (or: cd relay && npm test)
import assert from 'node:assert/strict'
import http from 'node:http'
import test from 'node:test'

import { ACL_CHECK_PATH, ACL_STATUS_PATH, UpstreamProxy } from '../src/proxy.mjs'

const TOKEN = 'pairing-token-for-tests'

function fakeAclApi (overrides = {}) {
  return {
    tokens: () => [TOKEN],
    status: () => ({
      guard: {
        enabled: true,
        version: 1,
        roots: [{ root: 'D:\\Code\\repo', state: 'ready', preparedAt: '2026-11-11T00:00:00.000Z' }]
      },
      candidates: ['D:\\Code\\repo']
    }),
    check: async (body) => ({ repair: body.repair !== false, roots: [{ root: 'D:\\Code\\repo', ready: true, repaired: false }] }),
    ...overrides
  }
}

/** Serve exactly one proxy instance over an ephemeral loopback port. */
async function withProxy (options, fn) {
  const proxy = new UpstreamProxy({
    upstreamPort: 1, // never contacted: ACL routes answer before the proxy hop
    helpers: options.helpers ?? 'all',
    aclApi: options.aclApi
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
      get: (path, headers) => fetch(`${`http://127.0.0.1:${port}`}${path}`, { headers }),
      post: (path, body, headers) => fetch(`${`http://127.0.0.1:${port}`}${path}`, {
        method: 'POST',
        headers: { 'content-type': 'application/json', ...(headers ?? {}) },
        body: JSON.stringify(body ?? {})
      })
    })
  } finally {
    await new Promise((resolve) => server.close(resolve))
  }
}

test('ACL status requires the pairing token outside loopback trust', async () => {
  await withProxy({ aclApi: fakeAclApi(), helpers: 'health' }, async ({ get }) => {
    const anonymous = await get(ACL_STATUS_PATH)
    assert.equal(anonymous.status, 401)
    const wrong = await get(ACL_STATUS_PATH, { authorization: 'Bearer nope' })
    assert.equal(wrong.status, 401)
    const good = await get(ACL_STATUS_PATH, { authorization: `Bearer ${TOKEN}` })
    assert.equal(good.status, 200)
    const payload = await good.json()
    assert.equal(payload.ok, true)
    assert.equal(payload.guard.enabled, true)
    assert.deepEqual(payload.candidates, ['D:\\Code\\repo'])
  })
})

test('ACL status is trusted on the loopback management listener', async () => {
  await withProxy({ aclApi: fakeAclApi(), helpers: 'all' }, async ({ get }) => {
    const response = await get(ACL_STATUS_PATH)
    assert.equal(response.status, 200)
    const payload = await response.json()
    assert.equal(payload.ok, true)
  })
})

test('the gateway loopback hop does NOT inherit loopback trust', async () => {
  // helpers:'health' is the gateway's internal plain proxy: its socket is
  // loopback, so only the bearer credential may authorize the call.
  await withProxy({ aclApi: fakeAclApi(), helpers: 'health' }, async ({ get }) => {
    const response = await get(ACL_STATUS_PATH)
    assert.equal(response.status, 401)
  })
})

test('ACL check runs through the guard API with a bearer token', async () => {
  const calls = []
  const api = fakeAclApi({
    check: async (body) => {
      calls.push(body)
      return { repair: body.repair !== false, roots: [{ root: 'D:\\Code\\repo', ready: true, repaired: true, durationMs: 12 }] }
    }
  })
  await withProxy({ aclApi: api, helpers: 'health' }, async ({ post }) => {
    const response = await post(ACL_CHECK_PATH, { repair: true }, { authorization: `Bearer ${TOKEN}` })
    assert.equal(response.status, 200)
    const payload = await response.json()
    assert.equal(payload.ok, true)
    assert.equal(payload.roots[0].repaired, true)
    assert.deepEqual(calls, [{ repair: true }])
  })
})

test('ACL check rejects unsupported methods and malformed JSON', async () => {
  await withProxy({ aclApi: fakeAclApi(), helpers: 'health' }, async ({ base, get, post }) => {
    const wrongMethod = await get(ACL_CHECK_PATH, { authorization: `Bearer ${TOKEN}` })
    assert.equal(wrongMethod.status, 405)

    const malformed = await fetch(`${base}${ACL_CHECK_PATH}`, {
      method: 'POST',
      headers: { 'content-type': 'application/json', authorization: `Bearer ${TOKEN}` },
      body: '{not json'
    })
    assert.equal(malformed.status, 400)

    // An empty JSON object still reaches the guard API (repair defaults to true).
    const empty = await post(ACL_CHECK_PATH, {}, { authorization: `Bearer ${TOKEN}` })
    assert.equal(empty.status, 200)
  })
})

test('old relay builds without an ACL API proxy the path upstream instead of answering', async () => {
  await withProxy({ aclApi: null, helpers: 'all' }, async ({ get }) => {
    // Without aclApi the ACL path falls through to the upstream proxy (port 1
    // refuses), so the client sees a gateway error rather than a JSON payload.
    const response = await get(ACL_STATUS_PATH).catch(() => null)
    assert.ok(response === null || response.status >= 500)
  })
})
