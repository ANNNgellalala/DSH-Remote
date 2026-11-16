// Unit tests for phone-facing gzip on the VPS gateway (v0.3.0+).
//
// The compression decision is a pure function (`maybeCompressResponse`), so no
// server, socket or VPS is involved here.
//
// Run: node --test vps/test/   (or: cd vps && npm test)
import assert from 'node:assert/strict'
import { gunzipSync } from 'node:zlib'
import test from 'node:test'

import {
  MIN_COMPRESS_BYTES,
  acceptsGzip,
  isCompressibleContentType,
  maybeCompressResponse
} from '../compress.mjs'

const JSON_TYPE = 'application/json; charset=utf-8'

function jsonBody (bytes = 4096) {
  const rows = []
  let size = 0
  let index = 0
  while (size < bytes) {
    const row = { sessionId: `session-${index}`, title: '会话列表标题', updatedAt: 1794154489599 }
    rows.push(row)
    size += Buffer.byteLength(JSON.stringify(row))
    index += 1
  }
  return Buffer.from(JSON.stringify({ items: rows }), 'utf8')
}

function compress (overrides = {}) {
  return maybeCompressResponse({
    method: 'GET',
    status: 200,
    acceptEncoding: 'gzip, deflate, br',
    headers: { 'content-type': JSON_TYPE, 'content-length': '0' },
    body: jsonBody(),
    ...overrides
  })
}

test('helpers recognise compressible types and gzip acceptance', () => {
  assert.equal(isCompressibleContentType('application/json'), true)
  assert.equal(isCompressibleContentType('Application/JSON; charset=utf-8'), true)
  assert.equal(isCompressibleContentType('text/plain; charset=utf-8'), true)
  assert.equal(isCompressibleContentType('text/html'), true)
  assert.equal(isCompressibleContentType('application/javascript'), true)
  assert.equal(isCompressibleContentType('application/javascript; charset=utf-8'), true)
  assert.equal(isCompressibleContentType('image/png'), false)
  assert.equal(isCompressibleContentType('application/octet-stream'), false)
  assert.equal(isCompressibleContentType(''), false)
  assert.equal(isCompressibleContentType(undefined), false)

  assert.equal(acceptsGzip('gzip'), true)
  assert.equal(acceptsGzip('GZIP'), true)
  assert.equal(acceptsGzip('br, gzip, deflate'), true)
  assert.equal(acceptsGzip('gzip;q=0.8'), true)
  assert.equal(acceptsGzip('gzip;q=0'), false)
  assert.equal(acceptsGzip('br, deflate'), false)
  assert.equal(acceptsGzip(''), false)
  assert.equal(acceptsGzip(undefined), false)
})

test('large JSON is gzipped, headers updated and etag dropped', () => {
  const body = jsonBody(64 * 1024)
  const headers = { 'content-type': JSON_TYPE, 'content-length': String(body.length), etag: '"abc123"' }
  const result = maybeCompressResponse({ method: 'GET', status: 200, acceptEncoding: 'gzip', headers, body })

  assert.equal(result.compressed, true)
  assert.equal(result.headers['content-encoding'], 'gzip')
  assert.equal(result.headers.vary, 'accept-encoding')
  assert.equal(result.headers.etag, undefined)
  assert.equal(result.headers['content-type'], JSON_TYPE)
  assert.equal(result.headers['content-length'], String(result.body.length))
  assert.ok(result.body.length < body.length, 'compressed body must be smaller')
  assert.deepEqual(gunzipSync(result.body), body)
  // The caller's header object is not mutated.
  assert.equal(headers['content-encoding'], undefined)
  assert.equal(headers.etag, '"abc123"')
})

test('bodies below the threshold are left untouched', () => {
  const small = Buffer.from('{"ok":true}', 'utf8')
  const result = maybeCompressResponse({ method: 'GET', status: 200, acceptEncoding: 'gzip', headers: { 'content-type': JSON_TYPE }, body: small })
  assert.equal(result.compressed, false)
  assert.equal(result.body, small)
  assert.equal(result.headers['content-encoding'], undefined)

  const atThreshold = Buffer.alloc(MIN_COMPRESS_BYTES, 0x61)
  const compressed = maybeCompressResponse({ method: 'GET', status: 200, acceptEncoding: 'gzip', headers: { 'content-type': JSON_TYPE }, body: atThreshold })
  assert.equal(compressed.compressed, true)
  assert.deepEqual(gunzipSync(compressed.body), atThreshold)
})

test('non-compressible content types are skipped', () => {
  for (const contentType of ['image/png', 'application/octet-stream', 'application/pdf', '']) {
    const result = compress({ headers: { 'content-type': contentType } })
    assert.equal(result.compressed, false, contentType)
  }
})

test('HEAD responses are never compressed', () => {
  // The gateway sends an empty body for HEAD, so only the headers matter.
  const result = compress({ method: 'HEAD', headers: { 'content-type': JSON_TYPE, 'content-length': '1024' }, body: Buffer.alloc(0) })
  assert.equal(result.compressed, false)
  assert.equal(result.body.length, 0)
})

test('non-2xx and 206 partial responses are not compressed', () => {
  for (const status of [101, 206, 301, 404, 500]) {
    const result = compress({ status })
    assert.equal(result.compressed, false, `status ${status}`)
  }
})

test('a phone that does not accept gzip gets the plain body', () => {
  for (const acceptEncoding of ['', 'br', 'deflate', 'gzip;q=0', undefined]) {
    const result = compress({ acceptEncoding })
    assert.equal(result.compressed, false, String(acceptEncoding))
  }
})

test('an already encoded response is not double-compressed', () => {
  for (const contentEncoding of ['gzip', 'br', 'deflate']) {
    const result = compress({ headers: { 'content-type': JSON_TYPE, 'content-encoding': contentEncoding } })
    assert.equal(result.compressed, false, contentEncoding)
    assert.equal(result.headers['content-encoding'], contentEncoding)
  }
})

test('header lookups are case-insensitive and vary is appended once', () => {
  const body = jsonBody()
  const result = maybeCompressResponse({
    method: 'GET',
    status: 200,
    acceptEncoding: 'gzip',
    headers: { 'Content-Type': JSON_TYPE, ETag: '"x"', Vary: 'Origin', 'Content-Encoding': undefined },
    body
  })
  assert.equal(result.compressed, true)
  assert.equal(result.headers['Content-Encoding'], 'gzip')
  assert.equal(result.headers.ETag, undefined)
  assert.equal(result.headers.Vary, 'Origin, accept-encoding')

  const already = compress({ headers: { 'content-type': JSON_TYPE, vary: 'accept-encoding' } })
  assert.equal(already.headers.vary, 'accept-encoding')

  const existing = compress({ headers: { 'content-type': JSON_TYPE, 'content-encoding': 'br' } })
  assert.equal(existing.compressed, false)
})

test('string bodies are accepted and compressed losslessly', () => {
  const text = 'x'.repeat(4096)
  const result = maybeCompressResponse({ method: 'POST', status: 201, acceptEncoding: 'gzip', headers: { 'content-type': 'text/plain' }, body: text })
  assert.equal(result.compressed, true)
  assert.deepEqual(gunzipSync(result.body), Buffer.from(text, 'utf8'))
})
