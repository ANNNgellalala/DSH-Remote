// Phone-facing HTTP response compression (gateway v0.3.0+).
//
// The gateway's last hop is public TLS to the phone, while the desktop↔gateway
// hop is a JSON WebSocket that base64s every body — so compressing at the edge
// costs the connector nothing and cuts real traffic a lot: measured DSH history
// and session-list JSON compress 3.6–4.9× with gzip.
//
// Kept in its own module (no imports from gateway.mjs) so the pure decision can
// be unit-tested without booting the server; `gateway.mjs` only calls it from
// `onHttpEnd` after the desktop response has been fully buffered.
import { gzipSync } from 'node:zlib'

/** Bodies below this size are not worth a gzip frame (and inflate small payloads). */
export const MIN_COMPRESS_BYTES = 1024

function headerValue (headers, name) {
  const wanted = String(name).toLowerCase()
  for (const [key, value] of Object.entries(headers ?? {})) {
    if (String(key).toLowerCase() !== wanted) continue
    if (value === undefined || value === null) continue
    return Array.isArray(value) ? value.join(', ') : String(value)
  }
  return undefined
}

function setHeader (headers, name, value) {
  for (const key of Object.keys(headers)) {
    if (key.toLowerCase() === name) {
      headers[key] = value
      return
    }
  }
  headers[name] = value
}

function removeHeader (headers, name) {
  for (const key of Object.keys(headers)) {
    if (key.toLowerCase() === name) delete headers[key]
  }
}

/** `application/json`, `text/*` and `application/javascript*` are worth compressing. */
export function isCompressibleContentType (contentType) {
  const mediaType = String(contentType ?? '').split(';')[0].trim().toLowerCase()
  if (mediaType === '') return false
  return mediaType === 'application/json' ||
    mediaType.startsWith('text/') ||
    mediaType.startsWith('application/javascript')
}

/** Parse `Accept-Encoding` and honour `q=0` ("do not use gzip"). */
export function acceptsGzip (acceptEncoding) {
  for (const part of String(acceptEncoding ?? '').split(',')) {
    const [token, ...params] = part.trim().toLowerCase().split(';')
    if (token.trim() !== 'gzip') continue
    let quality = 1
    for (const param of params) {
      const [key, raw] = param.split('=').map((value) => value.trim())
      if (key !== 'q') continue
      const parsed = Number(raw)
      if (Number.isFinite(parsed)) quality = parsed
    }
    return quality > 0
  }
  return false
}

/**
 * Decide and perform gzip for one phone-facing response.
 *
 * Compress only when ALL of these hold:
 *   - method is not HEAD (a HEAD response has no body to compress)
 *   - status is 2xx but not 206 (a Range/partial response must keep byte offsets)
 *   - body is at least MIN_COMPRESS_BYTES
 *   - the response has no existing `content-encoding` (never double-compress)
 *   - content-type is application/json, text/* or application/javascript*
 *   - the phone's Accept-Encoding accepts gzip
 *
 * When compressing: `content-encoding: gzip`, `vary: accept-encoding`,
 * `content-length` = compressed size, `etag` removed (the entity changed; a
 * stale validator must not be revalidated as the uncompressed representation).
 * Otherwise the body/headers are returned untouched.
 *
 * @param {{
 *   method?: string,
 *   status?: number,
 *   acceptEncoding?: string,
 *   headers?: Record<string, unknown>,
 *   body?: Buffer|Uint8Array|string
 * }} input
 * @returns {{ compressed: boolean, headers: Record<string, unknown>, body: Buffer }}
 */
export function maybeCompressResponse ({ method, status, acceptEncoding, headers, body } = {}) {
  const source = Buffer.isBuffer(body) ? body : Buffer.from(body ?? '')
  const outputHeaders = { ...(headers ?? {}) }
  const unchanged = { compressed: false, headers: outputHeaders, body: source }

  if (String(method ?? 'GET').toUpperCase() === 'HEAD') return unchanged
  const code = Number(status)
  if (!Number.isInteger(code) || code < 200 || code >= 300 || code === 206) return unchanged
  if (source.length < MIN_COMPRESS_BYTES) return unchanged
  if (headerValue(outputHeaders, 'content-encoding') !== undefined) return unchanged
  if (!isCompressibleContentType(headerValue(outputHeaders, 'content-type'))) return unchanged
  if (!acceptsGzip(acceptEncoding)) return unchanged

  const compressed = gzipSync(source)
  removeHeader(outputHeaders, 'etag')
  setHeader(outputHeaders, 'content-encoding', 'gzip')
  setHeader(outputHeaders, 'content-length', String(compressed.length))
  const vary = headerValue(outputHeaders, 'vary')
  if (vary === undefined || vary.trim() === '') setHeader(outputHeaders, 'vary', 'accept-encoding')
  else if (!/(?:^|,)\s*accept-encoding\s*(?:,|$)/i.test(vary)) setHeader(outputHeaders, 'vary', `${vary}, accept-encoding`)

  return { compressed: true, headers: outputHeaders, body: compressed }
}
