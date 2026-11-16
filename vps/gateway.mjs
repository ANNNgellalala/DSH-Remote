// dsh-remote-gateway — public TLS entry point in front of many desktops.
//
// One Node process on the VPS owns:
//
//   1. `https://0.0.0.0:9443` — self-signed TLS; the only public listener.
//      TLS terminates here, so the desktop launch token never leaves this hop.
//   2. `/__gateway/identity` + `/__gateway/health` — secret-free status for
//      phones/scripts (fingerprint, online desktop count, version).
//   3. `/__gateway/download` + `/__gateway/resources[/<id>/<name>]` — public
//      resource centre grouped by category ID (dsh-remote/smartbill/relay/docs):
//      HTML page, secret-free JSON index, streamed downloads (HEAD/Range/ETag)
//      and Bearer-token authenticated upload/delete with a per-upload
//      `keepVersions` retention option. The legacy
//      `/__gateway/download/<file>.apk` route still works (name lookup across
//      categories + the old APK_DIR fallback).
//   4. `/__gateway/tunnel` — WebSocket registration used by desktop connectors
//      (relay side). Each connector advertises `{remoteToken, name,
//      launchToken}`; frames multiplex HTTP and WebSocket streams to that
//      desktop. See docs/vps-gateway.md for the v1 frame protocol.
//   5. `/connect?token=<remoteToken>` — resolves the desktop by token, swaps the
//      query token for that desktop's launchToken, forwards through the
//      connector and indexes the resulting `dsh-auth*` cookies -> desktop.
//   6. every other path (`/api/*`, `/__relay/*`, ...) — routed to a desktop by
//      the `dsh-auth*` cookie, so one gateway serves any number of desktops
//      without them ever sharing a stream.
//
// Zero third-party runtime dependencies; Node >= 22.
import http from 'node:http'
import https from 'node:https'
import path from 'node:path'
import { fileURLToPath } from 'node:url'

import { loadConfig } from './config.mjs'
import { maybeCompressResponse } from './compress.mjs'
import { buildIdentity, readVersion, spkiFingerprintFromPem } from './identity.mjs'
import { DOWNLOAD_PAGE_PATH, summarizeDownloads } from './downloads.mjs'
import {
  RESOURCE_BASE_PATH,
  RESOURCE_INDEX_ALIAS,
  RESOURCE_INDEX_PATH,
  summarizeResources
} from './resources.mjs'
import { downloadPageHeaders } from './download-page.mjs'
import { handleResourcesRequest } from './resources-http.mjs'
import {
  TUNNEL_FRAME,
  WsConnection,
  WsProtocolError,
  authCookieFromSetCookie,
  buildHttpEnd,
  buildHttpFrame,
  buildWsClose,
  buildWsMsg,
  buildWsOpen,
  buildPong,
  connectorWsHeaders,
  maskSecret,
  normalizeResponseHeaders,
  parseCookies,
  randomStreamId,
  rejectUpgrade,
  sanitizeRequestHeaders,
  timingSafeEquals,
  toWireHeaders,
  writeUpgradeResponse
} from './proto.mjs'

const PKG_ROOT = path.dirname(fileURLToPath(import.meta.url))
const config = loadConfig(PKG_ROOT)
config.version = readVersion(PKG_ROOT)

const FINGERPRINT = config.tlsEnabled ? spkiFingerprintFromPem(config.tls.cert) : 'insecure-plaintext'
const STARTED_AT = Date.now()
const LOG_LEVELS = { debug: 10, info: 20, warn: 30, error: 40 }
const LOG_LEVEL = LOG_LEVELS[config.logLevel] ?? LOG_LEVELS.info
const RESPONSE_BODY_LIMIT = Math.max(config.maxFrameBytes, config.maxBodyBytes)
const OFFLINE_ERROR_STATUS = 404 // M6 spec: an offline/unknown token yields 404 from /connect
// One phone /connect mints a fresh DSH session cookie; a long-running gateway must
// not grow the routing index without bound. Oldest pairs are evicted first.
const MAX_AUTH_COOKIES_PER_TUNNEL = 512

const stats = {
  requests: 0,
  connectRequests: 0,
  webSocketStreams: 0,
  forwardedHttp: 0,
  forwardedWs: 0,
  rejected: 0,
  lastError: null
}

/** token -> Tunnel (kept while offline for friendly error pages). */
const tunnels = new Map()
/** `dsh-auth...=value` -> token. The only cross-desktop routing index. */
const cookieIndex = new Map()

function log (level, message) {
  if ((LOG_LEVELS[level] ?? LOG_LEVELS.info) < LOG_LEVEL) return
  const line = `[gateway] ${level} ${message}`
  if (level === 'error' || level === 'warn') console.error(line)
  else console.log(line)
}

function logError (message, error) {
  log('error', `${message}: ${error && error.stack ? error.stack : String(error)}`)
  stats.lastError = new Date().toISOString()
}

class Tunnel {
  constructor (token) {
    this.token = token
    this.name = 'desktop'
    this.launchToken = null
    this.conn = null
    this.state = 'offline'
    this.connectedAt = null
    this.offlineSince = Date.now()
    this.lastActivityAt = null
    this.remoteAddress = null
    this.streams = new Map()
    this.cookiePairs = new Set()
  }

  get online () {
    return this.state === 'online' && this.conn !== null
  }

  attach (conn, { name, launchToken, remoteAddress }) {
    // A different launch token means the desktop process restarted: cookies
    // minted by the previous process can never authenticate again.
    if (this.launchToken && launchToken && this.launchToken !== launchToken) this.forgetAuthCookies()
    this.conn = conn
    if (name) this.name = name
    this.launchToken = launchToken
    this.state = 'online'
    this.connectedAt = Date.now()
    this.lastActivityAt = Date.now()
    this.remoteAddress = remoteAddress ?? null
    this.offlineSince = null
  }

  detach (reason) {
    const conn = this.conn
    this.conn = null
    this.state = 'offline'
    this.offlineSince = Date.now()
    this.failStreams(reason)
    return conn
  }

  send (frame) {
    if (!this.online || !this.conn) return false
    const ok = this.conn.sendText(JSON.stringify(frame))
    if (!ok) this.detach('send-failed')
    return ok
  }

  addAuthCookie (pair) {
    if (this.cookiePairs.has(pair)) return
    this.cookiePairs.add(pair)
    cookieIndex.set(pair, this.token)
    while (this.cookiePairs.size > MAX_AUTH_COOKIES_PER_TUNNEL) {
      const oldest = this.cookiePairs.values().next().value
      this.cookiePairs.delete(oldest)
      if (cookieIndex.get(oldest) === this.token) cookieIndex.delete(oldest)
    }
  }

  forgetAuthCookies () {
    for (const pair of this.cookiePairs) {
      if (cookieIndex.get(pair) === this.token) cookieIndex.delete(pair)
    }
    this.cookiePairs.clear()
  }

  failStreams (reason) {
    for (const id of [...this.streams.keys()]) {
      dropStream(this, id, {
        notify: true,
        code: 1011,
        reason: 'desktop-offline',
        httpStatus: 503,
        httpMessage: `desktop is offline (${reason})`
      })
    }
  }
}

// ---------------------------------------------------------------------------
// HTTP helpers
// ---------------------------------------------------------------------------

function writeJson (res, status, value, extraHeaders = {}) {
  const body = Buffer.from(JSON.stringify(value), 'utf8')
  res.writeHead(status, {
    'content-type': 'application/json; charset=utf-8',
    'cache-control': 'no-store',
    'content-length': body.length,
    'x-content-type-options': 'nosniff',
    ...extraHeaders
  })
  res.end(body)
}

function writeText (res, status, text, extraHeaders = {}) {
  const body = Buffer.from(text, 'utf8')
  res.writeHead(status, {
    'content-type': 'text/plain; charset=utf-8',
    'cache-control': 'no-store',
    'content-length': body.length,
    'x-content-type-options': 'nosniff',
    ...extraHeaders
  })
  res.end(body)
}

function wantsHtml (req) {
  const accept = String(req.headers.accept ?? '')
  return accept.includes('text/html')
}

function renderConnectErrorPage ({ status, code, title, message, detail, token }) {
  const lines = [
    `<p class="code">${status} · ${escapeHtml(code)}</p>`,
    `<h1>${escapeHtml(title)}</h1>`,
    `<p>${escapeHtml(message)}</p>`
  ]
  if (token) lines.push(`<p class="mono">token: ${escapeHtml(maskSecret(token))}</p>`)
  if (detail) lines.push(`<p class="muted">${escapeHtml(detail)}</p>`)
  return `<!doctype html>
<html lang="zh-CN">
<head>
<meta charset="utf-8">
<meta name="viewport" content="width=device-width, initial-scale=1">
<meta name="referrer" content="no-referrer">
<title>DSH Remote Gateway — ${escapeHtml(title)}</title>
<style>
  :root { color-scheme: light dark; }
  body { margin: 0; padding: 48px 20px; font: 15px/1.7 -apple-system, "Segoe UI", system-ui, sans-serif; max-width: 640px; margin-inline: auto; }
  h1 { font-size: 24px; margin: 8px 0 12px; }
  .code { font: 600 12px/1 ui-monospace, Consolas, monospace; letter-spacing: .08em; color: #b07d00; text-transform: uppercase; }
  .mono { font-family: ui-monospace, Consolas, monospace; font-size: 13px; word-break: break-all; }
  .muted { color: #777; font-size: 13px; }
  .card { border: 1px solid #8884; border-radius: 12px; padding: 20px 22px; }
</style>
</head>
<body>
<div class="card">
${lines.join('\n')}
<p class="muted">排查：确认桌面 DSH 正在运行、dsh-remote-relay 已配置网关连接器且 token 与配对信息一致；网关只会在桌面在线时接受该 token。</p>
</div>
</body>
</html>
`
}

function escapeHtml (value) {
  return String(value ?? '')
    .replaceAll('&', '&amp;')
    .replaceAll('<', '&lt;')
    .replaceAll('>', '&gt;')
    .replaceAll('"', '&quot;')
    .replaceAll("'", '&#39;')
}

function respondError (req, res, status, code, message, options = {}) {
  stats.rejected += 1
  if (options.page && wantsHtml(req)) {
    const html = Buffer.from(renderConnectErrorPage({
      status,
      code,
      title: options.title ?? '无法连接到桌面',
      message,
      detail: options.detail,
      token: options.token
    }), 'utf8')
    res.writeHead(status, {
      ...downloadPageHeaders('text/html; charset=utf-8'),
      'content-length': html.length
    })
    res.end(html)
    return
  }
  writeJson(res, status, {
    ok: false,
    error: code,
    message,
    ...(options.extra ?? {})
  })
}

function parseRequestUrl (req) {
  try {
    return new URL(String(req.url ?? '/'), `https://${req.headers.host ?? 'gateway.invalid'}`)
  } catch {
    return new URL('/', 'https://gateway.invalid')
  }
}

function readRequestBody (req, limit) {
  return new Promise((resolve, reject) => {
    const chunks = []
    let size = 0
    let oversized = false
    req.on('data', (chunk) => {
      size += chunk.length
      if (size > limit) {
        oversized = true
        chunks.length = 0
        return
      }
      chunks.push(chunk)
    })
    req.on('end', () => {
      if (oversized) {
        const error = new Error('request body too large')
        error.code = 'BODY_TOO_LARGE'
        reject(error)
        return
      }
      resolve(Buffer.concat(chunks))
    })
    req.on('error', reject)
    req.on('aborted', () => {
      const error = new Error('request aborted')
      error.code = 'ABORTED'
      reject(error)
    })
  })
}

// ---------------------------------------------------------------------------
// Cookie routing
// ---------------------------------------------------------------------------

/**
 * Find the desktop a request belongs to from its `Cookie` header.
 * @returns {{ tunnel: Tunnel|null, ambiguous?: boolean, tokens?: string[] }}
 */
function resolveTunnelFromCookie (cookieHeader) {
  const cookies = parseCookies(cookieHeader)
  if (cookies.length === 0) return { tunnel: null }
  const tokens = new Set()
  for (const cookie of cookies) {
    const token = cookieIndex.get(cookie.pair)
    if (token) tokens.add(token)
  }
  if (tokens.size === 0) return { tunnel: null }
  if (tokens.size > 1) return { tunnel: null, ambiguous: true, tokens: [...tokens] }
  const [token] = [...tokens]
  return { tunnel: tunnels.get(token) ?? null }
}

function captureAuthCookies (tunnel, headers) {
  const setCookies = headers['set-cookie']
  if (!setCookies) return 0
  const list = Array.isArray(setCookies) ? setCookies : [setCookies]
  let added = 0
  for (const value of list) {
    const cookie = authCookieFromSetCookie(value)
    if (!cookie) continue
    tunnel.addAuthCookie(cookie.pair)
    added += 1
  }
  if (added > 0) log('debug', `indexed ${added} auth cookie(s) for desktop ${tunnel.name}`)
  return added
}

// ---------------------------------------------------------------------------
// Stream lifecycle
// ---------------------------------------------------------------------------

function dropStream (tunnel, id, options = {}) {
  const stream = tunnel.streams.get(id)
  if (!stream) return
  tunnel.streams.delete(id)
  if (stream.timer) clearTimeout(stream.timer)
  if (stream.settled) return
  stream.settled = true

  if (stream.kind === 'http') {
    const res = stream.res
    if (res.destroyed || res.writableEnded) return
    if (!res.headersSent) {
      const status = options.httpStatus ?? 502
      const message = options.httpMessage ?? 'desktop connector closed the stream'
      const body = Buffer.from(JSON.stringify({ ok: false, error: 'gateway-stream-failed', message }), 'utf8')
      try {
        res.writeHead(status, {
          'content-type': 'application/json; charset=utf-8',
          'cache-control': 'no-store',
          'content-length': body.length
        })
        res.end(body)
      } catch {
        try { res.destroy() } catch {}
      }
    } else {
      try { res.destroy() } catch {}
    }
    return
  }

  if (stream.kind === 'ws') {
    if (options.notify) {
      if (!stream.closeSent) {
        stream.closeSent = true
        tunnel.send(buildWsClose({ id, code: options.code ?? 1011, reason: options.reason ?? 'stream-closed' }))
      }
    }
    if (stream.state === 'opening') {
      rejectUpgrade(stream.socket, options.httpStatus ?? 502, options.httpMessage ?? 'desktop connector closed the stream')
      return
    }
    const ws = stream.ws
    if (ws) {
      try { ws.close(options.code ?? 1011, options.reason ?? 'stream-closed') } catch {}
      const timer = setTimeout(() => { try { ws.destroy() } catch {} }, 1200)
      if (typeof timer.unref === 'function') timer.unref()
    } else {
      try { stream.socket.destroy() } catch {}
    }
  }
}

// ---------------------------------------------------------------------------
// HTTP forwarding through a connector
// ---------------------------------------------------------------------------

async function forwardHttp (tunnel, req, res, { url, method }) {
  let body
  try {
    body = method === 'GET' || method === 'HEAD' ? Buffer.alloc(0) : await readRequestBody(req, config.maxBodyBytes)
  } catch (error) {
    if (error && error.code === 'BODY_TOO_LARGE') {
      respondError(req, res, 413, 'body-too-large', `request body exceeds ${config.maxBodyBytes} bytes`)
      return
    }
    if (error && error.code === 'ABORTED') return
    throw error
  }

  if (!tunnel.online) {
    respondError(req, res, 503, 'desktop-offline', 'desktop went offline before the request could be forwarded')
    return
  }
  if (tunnel.streams.size >= config.maxStreamsPerTunnel) {
    respondError(req, res, 429, 'too-many-streams', 'gateway stream limit reached for this desktop')
    return
  }

  const id = randomStreamId()
  const stream = {
    id,
    kind: 'http',
    method,
    res,
    // The phone's Accept-Encoding is remembered for the response path: the
    // desktop hop is a base64 JSON tunnel, so gzip belongs at this public edge.
    acceptEncoding: req.headers['accept-encoding'] === undefined
      ? ''
      : String(req.headers['accept-encoding']),
    status: 0,
    headers: {},
    chunks: [],
    size: 0,
    settled: false,
    timer: null
  }
  stream.timer = setTimeout(() => {
    log('warn', `http stream ${id} timed out after ${config.httpTimeoutMs}ms (desktop ${tunnel.name})`)
    dropStream(tunnel, id, { httpStatus: 504, httpMessage: 'desktop connector timed out' })
  }, config.httpTimeoutMs)
  if (typeof stream.timer.unref === 'function') stream.timer.unref()
  tunnel.streams.set(id, stream)
  stats.forwardedHttp += 1

  res.on('close', () => {
    if (!stream.settled) dropStream(tunnel, id, { notify: false })
  })

  tunnel.send(buildHttpFrame({
    id,
    method,
    url,
    headers: sanitizeRequestHeaders(req.headers),
    bodyB64: body.length > 0 ? body.toString('base64') : ''
  }))
  tunnel.send(buildHttpEnd({ id }))
}

// ---------------------------------------------------------------------------
// Connector frames
// ---------------------------------------------------------------------------

function onConnectorFrame (tunnel, data, opcode) {
  if (opcode !== 1) {
    log('warn', `desktop ${tunnel.name} sent a non-text frame (opcode ${opcode}); ignored`)
    return
  }
  let frame
  try {
    frame = JSON.parse(data.toString('utf8'))
  } catch {
    log('warn', `desktop ${tunnel.name} sent invalid JSON frame; ignored`)
    return
  }
  if (!frame || typeof frame !== 'object') return
  tunnel.lastActivityAt = Date.now()
  const id = frame.id === undefined || frame.id === null ? '' : String(frame.id)

  switch (frame.kind) {
    case TUNNEL_FRAME.PING:
      tunnel.send(buildPong())
      return
    case TUNNEL_FRAME.PONG:
      return
    case TUNNEL_FRAME.HTTP_RES:
      onHttpResponse(tunnel, id, frame)
      return
    case TUNNEL_FRAME.HTTP_END:
      onHttpEnd(tunnel, id)
      return
    case TUNNEL_FRAME.HTTP_ERROR:
      onHttpError(tunnel, id, frame)
      return
    case TUNNEL_FRAME.WS_OPEN_OK:
      onWsOpenOk(tunnel, id, frame)
      return
    case TUNNEL_FRAME.WS_OPEN_FAIL:
      dropStream(tunnel, id, {
        notify: false,
        httpStatus: Number(frame.status) >= 400 && Number(frame.status) <= 599 ? Number(frame.status) : 502,
        httpMessage: frame.message ? String(frame.message) : 'desktop refused the WebSocket stream'
      })
      return
    case TUNNEL_FRAME.WS_MSG:
      onWsMessage(tunnel, id, frame)
      return
    case TUNNEL_FRAME.WS_CLOSE:
      dropStream(tunnel, id, {
        notify: false,
        code: Number.isInteger(frame.code) ? frame.code : 1000,
        reason: frame.reason ? String(frame.reason) : ''
      })
      return
    default:
      log('debug', `desktop ${tunnel.name} sent unknown frame kind ${String(frame.kind)}`)
  }
}

function onHttpResponse (tunnel, id, frame) {
  const stream = tunnel.streams.get(id)
  if (!stream || stream.kind !== 'http' || stream.settled) {
    log('debug', `ignoring http-res for unknown stream ${id} (desktop ${tunnel.name})`)
    return
  }
  if (!stream.status) {
    const status = Number(frame.status)
    stream.status = Number.isInteger(status) && status >= 100 && status <= 599 ? status : 502
    stream.headers = normalizeResponseHeaders(frame.headers)
    captureAuthCookies(tunnel, stream.headers)
  }
  if (typeof frame.bodyB64 === 'string' && frame.bodyB64.length > 0) {
    const chunk = Buffer.from(frame.bodyB64, 'base64')
    stream.size += chunk.length
    if (stream.size > RESPONSE_BODY_LIMIT) {
      log('warn', `http stream ${id} exceeded response limit (${stream.size} bytes)`)
      dropStream(tunnel, id, { httpStatus: 502, httpMessage: 'desktop response exceeded gateway limit' })
      return
    }
    stream.chunks.push(chunk)
  }
}

function onHttpEnd (tunnel, id) {
  const stream = tunnel.streams.get(id)
  if (!stream || stream.kind !== 'http' || stream.settled) return
  tunnel.streams.delete(id)
  if (stream.timer) clearTimeout(stream.timer)
  stream.settled = true

  const res = stream.res
  if (res.destroyed || res.writableEnded) return
  const body = Buffer.concat(stream.chunks)
  const status = stream.status || 502
  const headers = toWireHeaders(stream.headers)
  if (stream.method === 'HEAD') {
    if (headers['content-length'] === undefined) headers['content-length'] = '0'
  } else {
    headers['content-length'] = String(body.length)
  }
  if (body.length > 0 && headers['content-type'] === undefined) headers['content-type'] = 'application/octet-stream'
  // Phone-side gzip: large JSON/text bodies leave the public TLS edge compressed
  // (3.6–4.9× on DSH history/list payloads). The App sends Accept-Encoding and
  // decompresses transparently; HEAD/206/already-encoded responses pass through.
  const encoded = maybeCompressResponse({
    method: stream.method,
    status,
    acceptEncoding: stream.acceptEncoding,
    headers,
    body
  })
  try {
    res.writeHead(status, encoded.headers)
    res.end(encoded.body)
  } catch (error) {
    log('warn', `cannot write response for stream ${id}: ${error && error.message ? error.message : String(error)}`)
    try { res.destroy() } catch {}
  }
}

function onHttpError (tunnel, id, frame) {
  const stream = tunnel.streams.get(id)
  if (!stream || stream.kind !== 'http') return
  tunnel.streams.delete(id)
  if (stream.timer) clearTimeout(stream.timer)
  stream.settled = true
  const status = Number(frame.status) >= 400 && Number(frame.status) <= 599 ? Number(frame.status) : 502
  const res = stream.res
  if (res.destroyed || res.writableEnded) return
  const body = Buffer.from(JSON.stringify({
    ok: false,
    error: 'desktop-connector-error',
    message: frame.message ? String(frame.message) : 'desktop connector failed the request'
  }), 'utf8')
  try {
    res.writeHead(status, {
      'content-type': 'application/json; charset=utf-8',
      'cache-control': 'no-store',
      'content-length': body.length
    })
    res.end(body)
  } catch {
    try { res.destroy() } catch {}
  }
}

function onWsOpenOk (tunnel, id, frame) {
  const stream = tunnel.streams.get(id)
  if (!stream || stream.kind !== 'ws' || stream.state !== 'opening' || stream.settled) {
    log('debug', `ignoring ws-open-ok for unknown stream ${id} (desktop ${tunnel.name})`)
    return
  }
  if (stream.timer) clearTimeout(stream.timer)
  const protocol = typeof frame.protocol === 'string' && frame.protocol !== '' ? frame.protocol : stream.protocol
  const extraHeaders = protocol ? { 'sec-websocket-protocol': protocol } : {}
  if (!writeUpgradeResponse(stream.socket, stream.key, extraHeaders)) {
    dropStream(tunnel, id, { notify: true, code: 1011, reason: 'handshake-failed' })
    return
  }
  stream.state = 'open'
  stats.forwardedWs += 1

  const ws = new WsConnection(stream.socket, { masked: false, maxMessageBytes: config.maxWsMessageBytes })
  stream.ws = ws
  ws.on('message', (data, opcode) => {
    if (stream.settled) return
    tunnel.send(buildWsMsg({ id, op: opcode, b64: data.toString('base64') }))
  })
  ws.on('remoteClose', (code, reason) => {
    if (stream.settled) return
    stream.closeSent = true
    tunnel.send(buildWsClose({ id, code, reason }))
    stream.settled = true
    tunnel.streams.delete(id)
    const timer = setTimeout(() => { try { ws.destroy() } catch {} }, 1200)
    if (typeof timer.unref === 'function') timer.unref()
  })
  ws.on('close', () => {
    if (stream.settled) return
    stream.settled = true
    tunnel.streams.delete(id)
    stream.closeSent = true
    tunnel.send(buildWsClose({ id, code: 1001, reason: 'phone-closed' }))
  })
  ws.on('protocolError', (error) => {
    log('warn', `phone WebSocket protocol error for stream ${id}: ${error && error.message ? error.message : String(error)}`)
  })
  ws.on('error', () => {
    if (stream.settled) return
    stream.settled = true
    tunnel.streams.delete(id)
    stream.closeSent = true
    tunnel.send(buildWsClose({ id, code: 1006, reason: 'phone-socket-error' }))
  })
  if (stream.head && stream.head.length > 0) ws.feed(stream.head)
}

function onWsMessage (tunnel, id, frame) {
  const stream = tunnel.streams.get(id)
  if (!stream || stream.kind !== 'ws' || stream.state !== 'open' || stream.settled || !stream.ws) return
  const data = Buffer.from(String(frame.b64 ?? ''), 'base64')
  if (data.length > config.maxWsMessageBytes) {
    log('warn', `ws stream ${id} exceeded message limit (${data.length} bytes); closing`)
    dropStream(tunnel, id, { notify: true, code: 1009, reason: 'message-too-big' })
    return
  }
  stream.ws.sendMessage(frame.op === 2 ? 2 : 1, data)
}

// ---------------------------------------------------------------------------
// Connector registration (/__gateway/tunnel)
// ---------------------------------------------------------------------------

function sanitizeDesktopName (value) {
  const text = String(value ?? '')
    .replace(/[\u0000-\u001f\u007f]/g, '')
    .replace(/\s+/g, ' ')
    .trim()
  if (text === '') return 'desktop'
  return text.slice(0, 64)
}

function isValidLaunchToken (value) {
  if (typeof value !== 'string' || value.length === 0 || value.length > 1024) return false
  return !/[\s\u0000-\u001f\u007f]/.test(value)
}

function registerTunnel (req, socket, head, url) {
  const params = url.searchParams
  const token = String(params.get('token') ?? '').toLowerCase()
  if (!/^[0-9a-f]{32}$/.test(token)) {
    log('warn', `connector registration rejected: malformed token ${maskSecret(token)}`)
    rejectUpgrade(socket, 400, 'token must be exactly 32 lowercase/uppercase hex characters')
    return
  }
  const key = req.headers['sec-websocket-key']
  if (!key || String(req.headers.upgrade ?? '').toLowerCase() !== 'websocket') {
    rejectUpgrade(socket, 400, 'missing WebSocket upgrade headers')
    return
  }
  if (config.registerSecret) {
    const provided = String(params.get('secret') ?? '')
    if (!provided || !timingSafeEquals(provided, config.registerSecret)) {
      log('warn', `connector registration rejected for token ${maskSecret(token)}: invalid register secret`)
      rejectUpgrade(socket, 401, 'invalid gateway register secret')
      return
    }
  }
  const name = sanitizeDesktopName(params.get('name'))
  const launchToken = String(params.get('launchToken') ?? '')
  if (!isValidLaunchToken(launchToken)) {
    log('warn', `connector registration rejected for token ${maskSecret(token)}: invalid launchToken`)
    rejectUpgrade(socket, 400, 'missing or invalid launchToken')
    return
  }

  if (!writeUpgradeResponse(socket, key)) return

  let tunnel = tunnels.get(token)
  const isNew = !tunnel
  if (!tunnel) {
    tunnel = new Tunnel(token)
    tunnels.set(token, tunnel)
  }

  // Same token re-registering replaces the previous connector (desktop relay
  // reconnect / token rotation). Streams owned by the old connector die with it.
  const previous = tunnel.conn
  if (previous) {
    log('info', `desktop ${name} re-registered (token ${maskSecret(token)}); replacing previous connector`)
    tunnel.detach('replaced')
    try { previous.close(4001, 'replaced') } catch {}
  }

  const conn = new WsConnection(socket, { masked: false, maxMessageBytes: config.maxFrameBytes })
  tunnel.attach(conn, { name, launchToken, remoteAddress: socket.remoteAddress })
  conn.on('message', (data, opcode) => {
    try {
      onConnectorFrame(tunnel, data, opcode)
    } catch (error) {
      logError(`connector frame handler failed for desktop ${tunnel.name}`, error)
    }
  })
  conn.on('close', (code, reason) => {
    if (tunnel.conn !== conn) return
    log('warn', `desktop ${tunnel.name} connector closed (code ${code}${reason ? `: ${reason}` : ''})`)
    tunnel.detach('connector-closed')
  })
  conn.on('error', (error) => {
    log('warn', `desktop ${tunnel.name} connector socket error: ${error && error.message ? error.message : String(error)}`)
    if (tunnel.conn === conn) tunnel.detach('connector-error')
  })
  conn.on('protocolError', (error) => {
    log('warn', `desktop ${tunnel.name} connector protocol error: ${error && error.message ? error.message : String(error)}`)
  })
  if (head && head.length > 0) conn.feed(head)
  if (isNew) log('info', `desktop registered: ${tunnel.name} token ${maskSecret(token)} (tunnels=${onlineCount()})`)
  else log('info', `desktop back online: ${tunnel.name} token ${maskSecret(token)} (tunnels=${onlineCount()})`)
}

// ---------------------------------------------------------------------------
// Phone WebSocket (/api/remote.mux and friends)
// ---------------------------------------------------------------------------

function handlePhoneUpgrade (req, socket, head, url) {
  socket.on('error', () => {})
  const resolution = resolveTunnelFromCookie(req.headers.cookie)
  if (resolution.ambiguous) {
    stats.rejected += 1
    rejectUpgrade(socket, 400, 'ambiguous dsh-auth cookies (more than one desktop)')
    return
  }
  const tunnel = resolution.tunnel
  if (!tunnel) {
    stats.rejected += 1
    rejectUpgrade(socket, 404, 'no known dsh-auth cookie; call /connect?token=<remoteToken> first')
    return
  }
  if (!tunnel.online) {
    stats.rejected += 1
    rejectUpgrade(socket, 503, 'desktop is offline')
    return
  }
  const key = req.headers['sec-websocket-key']
  if (!key || String(req.headers.upgrade ?? '').toLowerCase() !== 'websocket') {
    stats.rejected += 1
    rejectUpgrade(socket, 400, 'missing WebSocket upgrade headers')
    return
  }
  if (tunnel.streams.size >= config.maxStreamsPerTunnel) {
    stats.rejected += 1
    rejectUpgrade(socket, 429, 'gateway stream limit reached for this desktop')
    return
  }
  const requestedProtocol = String(req.headers['sec-websocket-protocol'] ?? '').split(',')[0].trim()
  const id = randomStreamId()
  const stream = {
    id,
    kind: 'ws',
    state: 'opening',
    socket,
    key,
    head,
    protocol: requestedProtocol || undefined,
    timer: null,
    closeSent: false,
    settled: false,
    ws: null
  }
  stream.timer = setTimeout(() => {
    log('warn', `ws-open for ${id} timed out after ${config.wsOpenTimeoutMs}ms (desktop ${tunnel.name})`)
    dropStream(tunnel, id, {
      notify: true,
      code: 1011,
      reason: 'timeout',
      httpStatus: 504,
      httpMessage: 'desktop connector did not answer ws-open in time'
    })
  }, config.wsOpenTimeoutMs)
  if (typeof stream.timer.unref === 'function') stream.timer.unref()
  tunnel.streams.set(id, stream)
  stats.webSocketStreams += 1
  socket.on('close', () => {
    if (stream.state !== 'opening') return
    if (stream.timer) clearTimeout(stream.timer)
    stream.settled = true
    tunnel.streams.delete(id)
  })
  tunnel.send(buildWsOpen({ id, url: req.url, headers: connectorWsHeaders(req.headers) }))
  log('debug', `ws stream ${id} opening for desktop ${tunnel.name} (${url.pathname})`)
}

// ---------------------------------------------------------------------------
// HTTP routing
// ---------------------------------------------------------------------------

function onlineCount () {
  let count = 0
  for (const tunnel of tunnels.values()) if (tunnel.online) count += 1
  return count
}

/** Secret-free gateway facts shared by the resource HTTP surface. */
function gatewayInfo () {
  return {
    version: config.version,
    name: config.name,
    fp: FINGERPRINT,
    publicUrl: config.publicUrl,
    tunnels: onlineCount()
  }
}

function handleIdentity (req, res) {
  const download = summarizeDownloads(config)
  writeJson(res, 200, buildIdentity({
    version: config.version,
    name: config.name,
    fp: FINGERPRINT,
    tunnels: onlineCount(),
    url: config.publicUrl,
    node: process.version,
    uptimeSec: Math.floor((Date.now() - STARTED_AT) / 1000),
    download,
    resources: summarizeResources(config)
  }))
}

function handleHealth (req, res) {
  const download = summarizeDownloads(config)
  const resources = summarizeResources(config)
  writeJson(res, 200, {
    ok: true,
    version: config.version,
    name: config.name,
    fp: FINGERPRINT,
    tunnels: onlineCount(),
    node: process.version,
    uptimeSec: Math.floor((Date.now() - STARTED_AT) / 1000),
    apkCount: download.count,
    resourceCount: resources.total,
    resourceCategories: resources.categories.map((category) => ({ id: category.id, count: category.count })),
    // Deprecated alias kept for older ops scripts (same counts as resourceCategories).
    resourceKinds: resources.categories.map((category) => ({ kind: category.id, id: category.id, count: category.count })),
    lastErrorAt: stats.lastError,
    stats: {
      requests: stats.requests,
      connect: stats.connectRequests,
      forwardedHttp: stats.forwardedHttp,
      forwardedWs: stats.forwardedWs,
      rejected: stats.rejected
    }
  })
}

async function handleConnect (req, res, url) {
  stats.connectRequests += 1
  if (req.method !== 'GET' && req.method !== 'HEAD') {
    writeText(res, 405, 'method not allowed\n', { allow: 'GET, HEAD' })
    return
  }
  const token = String(url.searchParams.get('token') ?? '')
  if (token === '') {
    respondError(req, res, 400, 'missing-token', 'this endpoint needs ?token=<remoteToken>', {
      page: true,
      title: '缺少 token',
      token: ''
    })
    return
  }
  const tunnel = tunnels.get(token)
  if (!tunnel) {
    respondError(req, res, OFFLINE_ERROR_STATUS, 'unknown-token', '网关没有这个 token 对应的桌面（token 错误，或桌面从未通过网关注册）。', {
      page: true,
      title: '未知的桌面 token',
      detail: 'raw token 不会出现在网关日志或错误页中，只显示掩码。',
      token
    })
    return
  }
  if (!tunnel.online) {
    respondError(req, res, OFFLINE_ERROR_STATUS, 'desktop-offline', '桌面当前不在线（relay 连接器已断开或桌面正在重启）。', {
      page: true,
      title: '桌面离线',
      detail: tunnel.offlineSince ? `最近在线：${new Date(tunnel.connectedAt ?? tunnel.offlineSince).toISOString()}` : undefined,
      token
    })
    return
  }

  // Replace the public remote token with the desktop's process-local launch
  // token; every other query parameter (e.g. interactions) is preserved.
  const target = new URL(url.pathname + url.search, 'https://gateway.invalid')
  target.searchParams.set('token', tunnel.launchToken)
  const targetUrl = `${target.pathname}?${target.searchParams.toString()}`
  log('debug', `/connect -> desktop ${tunnel.name} (token ${maskSecret(token)})`)
  await forwardHttp(tunnel, req, res, { url: targetUrl, method: req.method })
}

async function handleCookieRouted (req, res, url) {
  const resolution = resolveTunnelFromCookie(req.headers.cookie)
  if (resolution.ambiguous) {
    respondError(req, res, 400, 'ambiguous-cookie', 'request carries dsh-auth cookies for more than one desktop; refusing to mix streams')
    return
  }
  if (!resolution.tunnel) {
    respondError(req, res, 404, 'unknown-session', 'no known dsh-auth cookie; call /connect?token=<remoteToken> first')
    return
  }
  const tunnel = resolution.tunnel
  if (!tunnel.online) {
    respondError(req, res, 503, 'desktop-offline', `desktop ${tunnel.name} is offline`)
    return
  }
  await forwardHttp(tunnel, req, res, { url: req.url, method: req.method })
}

async function handleRequest (req, res) {
  stats.requests += 1
  const startNs = process.hrtime.bigint()
  let logged = false
  const accessLog = (status) => {
    if (logged) return
    logged = true
    const ms = Number(process.hrtime.bigint() - startNs) / 1e6
    log('info', `${req.method} ${String(req.url ?? '/').split('?')[0]} ${status} ${ms.toFixed(1)}ms`)
  }
  res.on('finish', () => accessLog(res.statusCode))
  res.on('close', () => accessLog(res.statusCode || 499))

  try {
    const url = parseRequestUrl(req)
    const pathname = url.pathname

    if (pathname === '/__gateway/identity') return handleIdentity(req, res)
    if (pathname === '/__gateway/health') return handleHealth(req, res)
    // Download page + JSON index + resource download/upload/delete. The handler
    // validates every URL segment itself (resources.mjs) and returns false for
    // paths it does not own, so unknown /__gateway/* still falls through to 404.
    if (await handleResourcesRequest(req, res, url, { config, gateway: gatewayInfo(), log })) return
    if (pathname === '/connect') return await handleConnect(req, res, url)
    if (pathname.startsWith('/__gateway/')) {
      return respondError(req, res, 404, 'unknown-gateway-endpoint', 'unknown gateway endpoint')
    }
    return await handleCookieRouted(req, res, url)
  } catch (error) {
    if (error && (error.code === 'BODY_TOO_LARGE')) {
      respondError(req, res, 413, 'body-too-large', `request body exceeds ${config.maxBodyBytes} bytes`)
      return
    }
    logError(`request handler failed (${req.method} ${String(req.url ?? '/').split('?')[0]})`, error)
    if (!res.headersSent) {
      respondError(req, res, 500, 'gateway-error', 'gateway internal error')
    } else {
      try { res.destroy() } catch {}
    }
  }
}

function handleUpgrade (req, socket, head) {
  socket.on('error', () => {})
  const url = parseRequestUrl(req)
  try {
    if (url.pathname === '/__gateway/tunnel') {
      registerTunnel(req, socket, head, url)
      return
    }
    if (url.pathname.startsWith('/__gateway/')) {
      stats.rejected += 1
      rejectUpgrade(socket, 404, 'unknown gateway endpoint')
      return
    }
    handlePhoneUpgrade(req, socket, head, url)
  } catch (error) {
    logError('upgrade handler failed', error)
    try { socket.destroy() } catch {}
  }
}

// ---------------------------------------------------------------------------
// Server bootstrap
// ---------------------------------------------------------------------------

function createServer () {
  const onRequest = (req, res) => {
    handleRequest(req, res).catch((error) => logError('unhandled request rejection', error))
  }
  if (!config.tlsEnabled) {
    log('warn', 'TLS_ENABLED=0: serving plain HTTP. This is for local smoke tests only; never expose it.')
    return http.createServer(onRequest)
  }
  return https.createServer({
    key: config.tls.key,
    cert: config.tls.cert,
    minVersion: 'TLSv1.2',
    honorCipherOrder: true
  }, onRequest)
}

const server = createServer()
server.on('upgrade', handleUpgrade)
server.on('clientError', (error, socket) => {
  log('debug', `TLS/HTTP client error: ${error && error.message ? error.message : String(error)}`)
  try { socket.destroy() } catch {}
})
server.keepAliveTimeout = 72_000
server.headersTimeout = 75_000
server.requestTimeout = 300_000

const watchdog = setInterval(() => {
  const now = Date.now()
  for (const tunnel of [...tunnels.values()]) {
    if (tunnel.online && tunnel.lastActivityAt && now - tunnel.lastActivityAt > config.tunnelSilenceTimeoutMs) {
      log('warn', `desktop ${tunnel.name} silent for >${config.tunnelSilenceTimeoutMs}ms; marking offline (token ${maskSecret(tunnel.token)})`)
      const conn = tunnel.detach('heartbeat-timeout')
      if (conn) {
        try { conn.close(4002, 'heartbeat-timeout') } catch {}
      }
      continue
    }
    if (!tunnel.online && tunnel.offlineSince && now - tunnel.offlineSince > config.offlineTtlMs) {
      tunnel.forgetAuthCookies()
      tunnels.delete(tunnel.token)
      log('debug', `pruned offline desktop ${tunnel.name} (token ${maskSecret(tunnel.token)})`)
    }
  }
}, 10_000)
if (typeof watchdog.unref === 'function') watchdog.unref()

function shutdown (signal) {
  log('info', `received ${signal}; shutting down`)
  clearInterval(watchdog)
  for (const tunnel of tunnels.values()) {
    const conn = tunnel.conn
    tunnel.detach('shutdown')
    if (conn) {
      try { conn.close(1001, 'gateway-shutdown') } catch {}
    }
  }
  try {
    server.closeAllConnections?.()
    server.closeAllIdleConnections?.()
  } catch {}
  server.close(() => {
    log('info', 'stopped')
    process.exit(0)
  })
  const timer = setTimeout(() => process.exit(0), 3000)
  if (typeof timer.unref === 'function') timer.unref()
}

process.on('SIGTERM', () => shutdown('SIGTERM'))
process.on('SIGINT', () => shutdown('SIGINT'))
process.on('uncaughtException', (error) => {
  logError('uncaught exception', error)
  process.exit(1)
})
process.on('unhandledRejection', (error) => {
  logError('unhandled rejection', error)
})

server.on('error', (error) => {
  logError('server error', error)
  process.exit(1)
})

server.listen(config.port, config.bindHost, () => {
  const scheme = config.tlsEnabled ? 'https' : 'http'
  log('info', `dsh-remote-gateway v${config.version} (node ${process.version})`)
  log('info', `listening on ${scheme}://${config.bindHost}:${config.port}`)
  log('info', `public url ${config.publicUrl}`)
  log('info', `certificate ${config.certFile} spki-sha256=${FINGERPRINT}`)
  log('info', `resource page ${DOWNLOAD_PAGE_PATH} (category ids in ${config.resourceDir})`)
  log('info', `resource root ${config.resourceDir} (index ${RESOURCE_INDEX_PATH}, alias ${RESOURCE_INDEX_ALIAS}, files ${RESOURCE_BASE_PATH}/<id>/<name>, legacy apk dir ${config.apkDir})`)
  log('info', `resource uploads ${config.uploadToken ? `enabled (Bearer token configured, max ${config.maxUploadBytes} bytes)` : 'disabled (UPLOAD_TOKEN not configured)'}`)
  log('info', `register secret ${config.registerSecret ? 'required' : 'not required (token is the credential)'}`)
  log('info', 'ready')
})
