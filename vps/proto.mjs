// Wire helpers shared by the public gateway and its desktop connectors.
//
// Two independent layers live in this module so both halves of the protocol can
// be tested with plain `node --check` / unit-ish scripts:
//
//   Layer 1: RFC 6455 WebSocket handshake + framing. The gateway is a WS *server*
//            for phones and for connector registration; the probe acts as a WS
//            *client*. Zero third-party dependencies on purpose.
//   Layer 2: the JSON control-frame protocol spoken inside the
//            `GET /__gateway/tunnel` WebSocket (HTTP request/response and
//            WebSocket stream multiplexing), plus header/cookie helpers.
//
// The canonical frame protocol (v1) is documented in docs/vps-gateway.md and
// mirrored by the desktop connector in relay/src/gateway-connector.mjs.

import crypto from 'node:crypto'
import { EventEmitter } from 'node:events'

export const WS_GUID = '258EAFA5-E914-47DA-95CA-C5AB0DC85B11'

export const WS_OPCODE = Object.freeze({
  CONTINUATION: 0x0,
  TEXT: 0x1,
  BINARY: 0x2,
  CLOSE: 0x8,
  PING: 0x9,
  PONG: 0xa
})

/** Kinds in the gateway <-> connector JSON frame protocol (v1). */
export const TUNNEL_FRAME = Object.freeze({
  HTTP: 'http',
  HTTP_END: 'http-end',
  HTTP_RES: 'http-res',
  HTTP_ERROR: 'http-error',
  WS_OPEN: 'ws-open',
  WS_OPEN_OK: 'ws-open-ok',
  WS_OPEN_FAIL: 'ws-open-fail',
  WS_MSG: 'ws-msg',
  WS_CLOSE: 'ws-close',
  PING: 'ping',
  PONG: 'pong'
})

/** Headers that only describe one hop and must never be forwarded. */
export const HOP_BY_HOP = new Set([
  'connection',
  'keep-alive',
  'proxy-connection',
  'proxy-authenticate',
  'proxy-authorization',
  'te',
  'trailer',
  'transfer-encoding',
  'upgrade'
])

const AUTH_COOKIE_PREFIX = 'dsh-auth'

/** `Sec-WebSocket-Accept` for a client's `Sec-WebSocket-Key`. */
export function computeAcceptKey (key) {
  return crypto.createHash('sha1').update(`${String(key ?? '')}${WS_GUID}`).digest('base64')
}

/** New opaque stream id (16 lowercase hex chars). Never a token. */
export function randomStreamId () {
  return crypto.randomBytes(8).toString('hex')
}

/**
 * Mask a token for logs/error pages: first 4 + last 4 characters only. Tokens
 * are credentials, so nothing in this project may log them in full.
 */
export function maskSecret (value) {
  const text = String(value ?? '')
  if (text.length === 0) return '(none)'
  if (text.length <= 8) return `${text.slice(0, 1)}…${text.slice(-1)}`
  return `${text.slice(0, 4)}…${text.slice(-4)}`
}

/** Length-independent comparison for shared secrets. */
export function timingSafeEquals (a, b) {
  const left = crypto.createHash('sha256').update(String(a ?? '')).digest()
  const right = crypto.createHash('sha256').update(String(b ?? '')).digest()
  return crypto.timingSafeEqual(left, right)
}

/**
 * Copy client headers into the JSON frame shape: lowercase names, hop-by-hop and
 * WebSocket handshake headers removed, values stringified. `host` is dropped
 * because the desktop connector re-binds the authority to its loopback upstream.
 */
export function sanitizeRequestHeaders (headers) {
  const out = {}
  for (const [name, value] of Object.entries(headers ?? {})) {
    if (value === undefined || value === null) continue
    const key = String(name).toLowerCase()
    if (key === 'host') continue
    if (HOP_BY_HOP.has(key)) continue
    if (key.startsWith('sec-websocket-')) continue
    out[key] = Array.isArray(value) ? value.map(String) : String(value)
  }
  return out
}

/** Minimal header subset the connector needs to open the upstream WebSocket. */
export function connectorWsHeaders (headers) {
  const out = {}
  for (const key of ['cookie', 'origin', 'user-agent', 'accept-language']) {
    const value = headers?.[key]
    if (value === undefined) continue
    out[key] = Array.isArray(value) ? value.map(String) : String(value)
  }
  return out
}

/**
 * Normalise a `headers` object coming from the connector: lowercase names,
 * hop-by-hop removed, `set-cookie` kept as an array, other repeats joined.
 */
export function normalizeResponseHeaders (raw) {
  const out = {}
  if (!raw || typeof raw !== 'object') return out
  for (const [name, value] of Object.entries(raw)) {
    if (value === undefined || value === null) continue
    const key = String(name).toLowerCase()
    if (HOP_BY_HOP.has(key)) continue
    if (key === 'set-cookie') {
      const list = Array.isArray(value) ? value.map(String) : [String(value)]
      out[key] = out[key] ? out[key].concat(list) : list
      continue
    }
    if (Array.isArray(value)) {
      if (out[key] === undefined) out[key] = value.map(String)
      else out[key] = [].concat(out[key], value.map(String))
      continue
    }
    out[key] = String(value)
  }
  return out
}

/** Turn the stored response headers into something `res.writeHead` accepts. */
export function toWireHeaders (headers) {
  const out = {}
  for (const [name, value] of Object.entries(headers ?? {})) {
    if (value === undefined || value === null) continue
    if (Array.isArray(value)) {
      if (name === 'set-cookie') out[name] = value
      else out[name] = value.join(', ')
      continue
    }
    out[name] = value
  }
  return out
}

/**
 * Parse a `Cookie` request header into name/value pairs, preserving the exact
 * `name=value` substring so it can be used as a routing key.
 */
export function parseCookies (cookieHeader) {
  if (!cookieHeader) return []
  const pairs = []
  for (const part of String(cookieHeader).split(';')) {
    const text = part.trim()
    if (text === '') continue
    const index = text.indexOf('=')
    if (index <= 0) continue
    const name = text.slice(0, index).trim()
    if (name === '') continue
    pairs.push({ name, value: text.slice(index + 1).trim(), pair: text })
  }
  return pairs
}

export function isAuthCookieName (name) {
  return String(name ?? '').toLowerCase().startsWith(AUTH_COOKIE_PREFIX)
}

/**
 * Extract the routing key (`name=value`) from one `Set-Cookie` header value when
 * the cookie belongs to DSH's own auth family (`dsh-auth*`). Everything else
 * (theme, locale, ...) is forwarded to the client untouched but never indexed.
 *
 * @returns {{ name: string, pair: string } | null}
 */
export function authCookieFromSetCookie (setCookieValue) {
  const text = String(setCookieValue ?? '')
  const first = text.split(';')[0]
  if (!first) return null
  const index = first.indexOf('=')
  if (index <= 0) return null
  const name = first.slice(0, index).trim()
  if (!isAuthCookieName(name)) return null
  const value = first.slice(index + 1).trim()
  if (value === '') return null
  return { name, pair: `${name}=${value}` }
}

// ---------------------------------------------------------------------------
// Frame builders (objects; the caller JSON.stringify's them). Keeping builders
// here makes the gateway/connector/probe share one definition of the protocol.
// ---------------------------------------------------------------------------

export function buildHttpFrame ({ id, method, url, headers, bodyB64 }) {
  return { id, kind: TUNNEL_FRAME.HTTP, method, url, headers: headers ?? {}, bodyB64: bodyB64 ?? '' }
}

export function buildHttpEnd ({ id }) {
  return { id, kind: TUNNEL_FRAME.HTTP_END }
}

export function buildHttpResponse ({ id, status, headers, bodyB64 }) {
  const frame = { id, kind: TUNNEL_FRAME.HTTP_RES, status }
  if (headers !== undefined) frame.headers = headers
  if (bodyB64 !== undefined) frame.bodyB64 = bodyB64
  return frame
}

export function buildHttpError ({ id, status, message }) {
  return { id, kind: TUNNEL_FRAME.HTTP_ERROR, status: status ?? 502, message: String(message ?? '') }
}

export function buildWsOpen ({ id, url, headers }) {
  return { id, kind: TUNNEL_FRAME.WS_OPEN, url, headers: headers ?? {} }
}

export function buildWsOpenOk ({ id, protocol }) {
  const frame = { id, kind: TUNNEL_FRAME.WS_OPEN_OK }
  if (protocol) frame.protocol = protocol
  return frame
}

export function buildWsOpenFail ({ id, status, message }) {
  return { id, kind: TUNNEL_FRAME.WS_OPEN_FAIL, status: status ?? 502, message: String(message ?? '') }
}

/** op: 1 = text, 2 = binary (matches WebSocket opcodes). */
export function buildWsMsg ({ id, op, b64 }) {
  return { id, kind: TUNNEL_FRAME.WS_MSG, op: op === 2 ? 2 : 1, b64: String(b64 ?? '') }
}

export function buildWsClose ({ id, code, reason }) {
  const frame = { id, kind: TUNNEL_FRAME.WS_CLOSE, code: Number.isInteger(code) ? code : 1000 }
  if (reason) frame.reason = String(reason)
  return frame
}

export function buildPing () {
  return { kind: TUNNEL_FRAME.PING }
}

export function buildPong () {
  return { kind: TUNNEL_FRAME.PONG }
}

// ---------------------------------------------------------------------------
// RFC 6455 framing
// ---------------------------------------------------------------------------

export class WsProtocolError extends Error {
  constructor (message, closeCode = 1002) {
    super(message)
    this.name = 'WsProtocolError'
    this.closeCode = closeCode
  }
}

/** Incremental frame parser following one direction of a WebSocket. */
export class WsFrameParser {
  /**
   * @param {{ expectMasked?: boolean, maxMessageBytes?: number }} options
   *   expectMasked=true for server-side parsers (client frames must be masked),
   *   false for client-side parsers (server frames must not be masked).
   */
  constructor ({ expectMasked = false, maxMessageBytes = 64 * 1024 * 1024 } = {}) {
    this.expectMasked = expectMasked
    this.maxMessageBytes = maxMessageBytes
    this.buffer = Buffer.alloc(0)
    this.fragments = null
    this.fragmentOpcode = 0
    this.fragmentBytes = 0
  }

  /** @returns {Array<{type:'message',opcode:number,data:Buffer}|{type:'ping',data:Buffer}|{type:'pong',data:Buffer}|{type:'close',code:number,reason:string}>} */
  push (chunk) {
    const events = []
    if (!chunk || chunk.length === 0) return events
    this.buffer = this.buffer.length === 0 ? Buffer.from(chunk) : Buffer.concat([this.buffer, chunk])
    for (;;) {
      const frame = this.readFrame()
      if (!frame) break
      this.handleFrame(frame, events)
    }
    return events
  }

  readFrame () {
    const buffer = this.buffer
    if (buffer.length < 2) return null
    const first = buffer[0]
    const second = buffer[1]
    const fin = (first & 0x80) !== 0
    if ((first & 0x70) !== 0) throw new WsProtocolError('reserved bits set')
    const opcode = first & 0x0f
    const masked = (second & 0x80) !== 0
    let length = second & 0x7f
    let offset = 2
    if (length === 126) {
      if (buffer.length < 4) return null
      length = buffer.readUInt16BE(2)
      offset = 4
    } else if (length === 127) {
      if (buffer.length < 10) return null
      const big = buffer.readBigUInt64BE(2)
      if (big > BigInt(Number.MAX_SAFE_INTEGER)) throw new WsProtocolError('frame length out of range', 1009)
      length = Number(big)
      offset = 10
    }
    if (masked !== this.expectMasked) {
      throw new WsProtocolError(this.expectMasked ? 'client frame is not masked' : 'server frame must not be masked')
    }
    if (length > this.maxMessageBytes) throw new WsProtocolError('frame exceeds size limit', 1009)
    let maskKey = null
    if (masked) {
      if (buffer.length < offset + 4) return null
      maskKey = buffer.subarray(offset, offset + 4)
      offset += 4
    }
    if (buffer.length < offset + length) return null
    let payload = Buffer.from(buffer.subarray(offset, offset + length))
    if (masked) {
      for (let i = 0; i < payload.length; i++) payload[i] ^= maskKey[i & 3]
    }
    this.buffer = buffer.subarray(offset + length)
    return { fin, opcode, payload }
  }

  handleFrame ({ fin, opcode, payload }, events) {
    if (opcode >= 0x8) {
      if (!fin) throw new WsProtocolError('fragmented control frame')
      if (payload.length > 125) throw new WsProtocolError('control frame too large')
      if (opcode === WS_OPCODE.PING) {
        events.push({ type: 'ping', data: payload })
        return
      }
      if (opcode === WS_OPCODE.PONG) {
        events.push({ type: 'pong', data: payload })
        return
      }
      if (opcode === WS_OPCODE.CLOSE) {
        let code = 1005
        let reason = ''
        if (payload.length >= 2) {
          code = payload.readUInt16BE(0)
          reason = payload.subarray(2).toString('utf8')
        }
        events.push({ type: 'close', code, reason })
        return
      }
      throw new WsProtocolError(`unsupported control opcode ${opcode}`)
    }

    if (opcode === WS_OPCODE.CONTINUATION) {
      if (!this.fragments) throw new WsProtocolError('continuation frame without start')
      this.fragments.push(payload)
      this.fragmentBytes += payload.length
      if (this.fragmentBytes > this.maxMessageBytes) throw new WsProtocolError('message exceeds size limit', 1009)
      if (fin) {
        const data = Buffer.concat(this.fragments)
        const op = this.fragmentOpcode
        this.fragments = null
        this.fragmentOpcode = 0
        this.fragmentBytes = 0
        events.push({ type: 'message', opcode: op, data })
      }
      return
    }

    if (opcode !== WS_OPCODE.TEXT && opcode !== WS_OPCODE.BINARY) {
      throw new WsProtocolError(`unsupported opcode ${opcode}`)
    }
    if (fin) {
      events.push({ type: 'message', opcode, data: payload })
      return
    }
    this.fragments = [payload]
    this.fragmentOpcode = opcode
    this.fragmentBytes = payload.length
  }
}

/**
 * Encode one WebSocket frame.
 * @param {{opcode:number,payload?:Buffer|string,fin?:boolean,mask?:boolean}} options
 */
export function encodeFrame ({ opcode, payload = Buffer.alloc(0), fin = true, mask = false }) {
  const data = Buffer.isBuffer(payload) ? payload : Buffer.from(String(payload), 'utf8')
  const length = data.length
  let header
  if (length < 126) {
    header = Buffer.alloc(2)
    header[1] = length
  } else if (length < 65536) {
    header = Buffer.alloc(4)
    header[1] = 126
    header.writeUInt16BE(length, 2)
  } else {
    header = Buffer.alloc(10)
    header[1] = 127
    header.writeBigUInt64BE(BigInt(length), 2)
  }
  header[0] = (fin ? 0x80 : 0x00) | (opcode & 0x0f)
  if (!mask) return Buffer.concat([header, data])
  header[1] |= 0x80
  const key = crypto.randomBytes(4)
  const masked = Buffer.allocUnsafe(length)
  for (let i = 0; i < length; i++) masked[i] = data[i] ^ key[i & 3]
  return Buffer.concat([header, key, masked])
}

/**
 * Thin WebSocket connection over a raw socket (TLS or plain). Emits:
 *   'message' (data: Buffer, opcode: number)
 *   'ping' (data), 'pong' (data)
 *   'remoteClose' (code, reason)
 *   'close' (code, reason)
 *   'error' (Error) / 'protocolError' (WsProtocolError)
 *
 * @param {import('node:net').Socket} socket
 * @param {{ masked?: boolean, maxMessageBytes?: number }} options
 *   masked=true when acting as a WS client (outgoing frames masked).
 */
export class WsConnection extends EventEmitter {
  constructor (socket, { masked = false, maxMessageBytes = 64 * 1024 * 1024 } = {}) {
    super()
    this.socket = socket
    this.maskOutgoing = masked
    this.closed = false
    this.lastCloseCode = undefined
    this.lastCloseReason = ''
    this.parser = new WsFrameParser({ expectMasked: !masked, maxMessageBytes })
    socket.on('data', (chunk) => this.feed(chunk))
    socket.on('error', (error) => {
      this.emit('error', error)
      this.destroy()
    })
    socket.on('close', () => {
      this.closed = true
      this.emit('close', this.lastCloseCode ?? 1006, this.lastCloseReason ?? '')
    })
  }

  /** Feed already-read bytes (e.g. the `head` of an HTTP upgrade event). */
  feed (chunk) {
    if (!chunk || chunk.length === 0) return
    let events
    try {
      events = this.parser.push(chunk)
    } catch (error) {
      if (error instanceof WsProtocolError) {
        this.emit('protocolError', error)
        this.close(error.closeCode, error.message)
        return
      }
      this.emit('error', error)
      this.destroy()
      return
    }
    for (const event of events) this.dispatch(event)
  }

  dispatch (event) {
    if (event.type === 'message') {
      this.emit('message', event.data, event.opcode)
      return
    }
    if (event.type === 'ping') {
      this.sendControl(WS_OPCODE.PONG, event.data)
      this.emit('ping', event.data)
      return
    }
    if (event.type === 'pong') {
      this.emit('pong', event.data)
      return
    }
    if (event.type === 'close') {
      this.lastCloseCode = event.code
      this.lastCloseReason = event.reason
      this.emit('remoteClose', event.code, event.reason)
      this.close(event.code === 1005 || event.code === 1006 ? 1000 : event.code, '')
    }
  }

  send (buffer) {
    if (this.closed || this.socket.destroyed) return false
    try {
      this.socket.write(buffer)
      return true
    } catch {
      this.destroy()
      return false
    }
  }

  sendMessage (opcode, payload) {
    return this.send(encodeFrame({ opcode, payload, mask: this.maskOutgoing }))
  }

  sendText (text) {
    return this.sendMessage(WS_OPCODE.TEXT, Buffer.from(String(text), 'utf8'))
  }

  sendBinary (data) {
    return this.sendMessage(WS_OPCODE.BINARY, data)
  }

  sendControl (opcode, payload) {
    return this.send(encodeFrame({
      opcode,
      payload: payload ?? Buffer.alloc(0),
      mask: this.maskOutgoing
    }))
  }

  ping (payload) {
    return this.sendControl(WS_OPCODE.PING, payload)
  }

  pong (payload) {
    return this.sendControl(WS_OPCODE.PONG, payload)
  }

  close (code = 1000, reason = '') {
    if (this.closed) {
      this.destroy()
      return
    }
    this.closed = true
    const reasonBuffer = Buffer.from(String(reason).slice(0, 120), 'utf8')
    const payload = Buffer.alloc(2 + reasonBuffer.length)
    payload.writeUInt16BE(code, 0)
    reasonBuffer.copy(payload, 2)
    try {
      this.socket.write(encodeFrame({ opcode: WS_OPCODE.CLOSE, payload, mask: this.maskOutgoing }))
      this.socket.end()
    } catch {}
    const timer = setTimeout(() => {
      try { this.socket.destroy() } catch {}
    }, 1000)
    if (typeof timer.unref === 'function') timer.unref()
  }

  destroy () {
    this.closed = true
    try { this.socket.destroy() } catch {}
  }
}

// ---------------------------------------------------------------------------
// HTTP handshake helpers
// ---------------------------------------------------------------------------

const STATUS_TEXT = Object.freeze({
  400: 'Bad Request',
  401: 'Unauthorized',
  403: 'Forbidden',
  404: 'Not Found',
  409: 'Conflict',
  413: 'Payload Too Large',
  426: 'Upgrade Required',
  429: 'Too Many Requests',
  500: 'Internal Server Error',
  502: 'Bad Gateway',
  503: 'Service Unavailable',
  504: 'Gateway Timeout'
})

export function httpStatusText (status) {
  return STATUS_TEXT[status] ?? 'Error'
}

/** Write the 101 response for a server-side upgrade. */
export function writeUpgradeResponse (socket, key, extraHeaders = {}) {
  const lines = [
    'HTTP/1.1 101 Switching Protocols',
    'Upgrade: websocket',
    'Connection: Upgrade',
    `Sec-WebSocket-Accept: ${computeAcceptKey(key)}`
  ]
  for (const [name, value] of Object.entries(extraHeaders)) {
    if (value === undefined || value === null || value === '') continue
    lines.push(`${name}: ${value}`)
  }
  try {
    socket.write(`${lines.join('\r\n')}\r\n\r\n`)
    return true
  } catch {
    try { socket.destroy() } catch {}
    return false
  }
}

/** Reject an upgrade request before switching protocols (plain HTTP response). */
export function rejectUpgrade (socket, status, message, extraHeaders = {}) {
  const body = Buffer.from(`${message ?? httpStatusText(status)}\n`, 'utf8')
  const lines = [
    `HTTP/1.1 ${status} ${httpStatusText(status)}`,
    'connection: close',
    'content-type: text/plain; charset=utf-8',
    `content-length: ${body.length}`,
    'cache-control: no-store'
  ]
  for (const [name, value] of Object.entries(extraHeaders)) {
    if (value === undefined || value === null || value === '') continue
    lines.push(`${name}: ${value}`)
  }
  try {
    socket.write(`${lines.join('\r\n')}\r\n\r\n`)
    socket.end(body)
  } catch {
    try { socket.destroy() } catch {}
  }
}

/** Client-side opening handshake request (used by _scratch/gateway-probe.mjs). */
export function buildClientHandshakeRequest ({ host, path, key, headers = {} }) {
  const lines = [
    `GET ${path} HTTP/1.1`,
    `Host: ${host}`,
    'Upgrade: websocket',
    'Connection: Upgrade',
    `Sec-WebSocket-Key: ${key}`,
    'Sec-WebSocket-Version: 13'
  ]
  for (const [name, value] of Object.entries(headers)) {
    if (value === undefined || value === null) continue
    lines.push(`${name}: ${value}`)
  }
  return `${lines.join('\r\n')}\r\n\r\n`
}
