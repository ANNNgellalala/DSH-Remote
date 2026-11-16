// Zero-dependency WebSocket client + gateway connector for the DSH Remote
// public gateway (M6).
//
// Wire protocol (must match vps/ gateway):
//   connector -> gateway: WSS upgrade to gateway.url with query
//     ?token=<remoteToken>&name=<desktopName>&launchToken=<dshLaunchToken>[&secret=..]
//   gateway -> connector: {"id","kind":"http","method","url","headers","bodyB64"}
//                         {"id","kind":"http-end"}
//                         {"id","kind":"ws-open","url","headers"}
//                         {"id","kind":"ws-msg","b64"}
//                         {"id","kind":"ws-close"}
//                         {"kind":"ping"} / {"kind":"pong"}
//   connector -> gateway: {"id","kind":"http-res","status","headers","bodyB64"}
//                         {"id","kind":"http-end"}
//                         {"id","kind":"ws-open-ok"|"ws-open-fail"}
//                         {"id","kind":"ws-msg","b64"} / {"id","kind":"ws-close"}
//                         {"kind":"ping"} / {"kind":"pong"}
// Requests arrive with the remote token already replaced by the DSH
// launchToken, so the connector forwards them to the loopback-only plain
// reverse proxy, which applies the usual Host/Origin normalization.
import crypto from 'node:crypto'
import http from 'node:http'
import https from 'node:https'
import net from 'node:net'
import tls from 'node:tls'

const CONNECT_TIMEOUT_MS = 10000
const PING_INTERVAL_MS = 20000
const PONG_TIMEOUT_MS = 60000
const MAX_BODY_BYTES = 64 * 1024 * 1024

export function maskToken (token) {
  const text = String(token ?? '')
  if (text.length <= 8) return '****'
  return `${text.slice(0, 4)}…${text.slice(-4)}`
}

/** Parse and validate `wss://host:port/path` (ws:// allowed for local tests). */
export function parseGatewayUrl (raw) {
  const url = new URL(String(raw))
  if (url.protocol !== 'wss:' && url.protocol !== 'ws:') {
    throw new Error(`dsh-remote-relay: gateway.url must start with wss:// (got ${url.protocol})`)
  }
  const secure = url.protocol === 'wss:'
  return {
    url,
    secure,
    host: url.hostname,
    port: Number(url.port || (secure ? 443 : 80)),
    path: `${url.pathname || '/'}${url.search || ''}`
  }
}

function spkiFingerprintFromDer (der) {
  const cert = new crypto.X509Certificate(der)
  const spki = cert.publicKey.export({ type: 'spki', format: 'der' })
  return crypto.createHash('sha256').update(spki).digest('hex')
}

/**
 * Derive the identity-endpoint path from the configured tunnel endpoint:
 * `.../__gateway/tunnel` -> `.../__gateway/identity`; a custom endpoint without
 * a trailing `/tunnel` gets `/identity` appended. Mirrors
 * `gatewayIdentityPath()` in config.mjs (kept local so this module stays
 * dependency-free; config.mjs is the single source of truth for the UI/API).
 */
export function defaultIdentityPath (tunnelPath) {
  const text = String(tunnelPath ?? '').trim()
  if (!text) return '/__gateway/identity'
  const normalized = text.startsWith('/') ? text : `/${text}`
  const trimmed = normalized.replace(/\/+$/, '')
  if (trimmed.endsWith('/tunnel')) {
    const base = trimmed.slice(0, -'/tunnel'.length)
    return base.length > 0 ? `${base}/identity` : '/identity'
  }
  return `${trimmed}/identity`
}

/**
 * Fetch GET <identityPath> from the gateway and capture the TLS SPKI
 * fingerprint (TOFU when no expected fingerprint is configured).
 * Never throws: failures are returned in `{ok:false,error}`.
 */
export function probeGatewayIdentity ({ gatewayUrl, expectedFp, identityPath, timeoutMs = 8000 }) {
  return new Promise((resolve) => {
    let parsed
    try {
      parsed = parseGatewayUrl(gatewayUrl)
    } catch (error) {
      resolve({ ok: false, error: error.message })
      return
    }
    const transport = parsed.secure ? https : http
    const requestPath = identityPath || defaultIdentityPath(parsed.path)
    const req = transport.request({
      host: parsed.host,
      port: parsed.port,
      method: 'GET',
      path: requestPath,
      headers: { accept: 'application/json' },
      rejectUnauthorized: false,
      timeout: timeoutMs
    }, (res) => {
      const chunks = []
      let fp
      try {
        const cert = res.socket?.getPeerCertificate ? res.socket.getPeerCertificate() : undefined
        if (cert && cert.raw) fp = spkiFingerprintFromDer(cert.raw)
      } catch {}
      res.on('data', (chunk) => chunks.push(chunk))
      res.on('end', () => {
        const text = Buffer.concat(chunks).toString('utf8')
        if (expectedFp && fp && fp !== String(expectedFp).toLowerCase()) {
          resolve({ ok: false, error: `gateway fingerprint mismatch (expected ${expectedFp}, got ${fp})`, fp })
          return
        }
        let identity
        try { identity = JSON.parse(text) } catch { identity = undefined }
        resolve({ ok: res.statusCode === 200, status: res.statusCode, fp, identity, raw: text.slice(0, 2000), error: res.statusCode === 200 ? undefined : `HTTP ${res.statusCode}` })
      })
    })
    req.on('timeout', () => req.destroy(new Error(`gateway identity timeout after ${timeoutMs}ms`)))
    req.on('error', (error) => resolve({ ok: false, error: error.message }))
    req.end()
  })
}

function encodeClientFrame (opcode, payload) {
  const mask = crypto.randomBytes(4)
  const len = payload.length
  let header
  if (len < 126) {
    header = Buffer.from([0x80 | opcode, 0x80 | len])
  } else if (len < 65536) {
    header = Buffer.alloc(4)
    header[0] = 0x80 | opcode
    header[1] = 0x80 | 126
    header.writeUInt16BE(len, 2)
  } else {
    header = Buffer.alloc(10)
    header[0] = 0x80 | opcode
    header[1] = 0x80 | 127
    header.writeBigUInt64BE(BigInt(len), 2)
  }
  const masked = Buffer.alloc(len)
  for (let i = 0; i < len; i++) masked[i] = payload[i] ^ mask[i % 4]
  return Buffer.concat([header, mask, masked])
}

function decodeFrame (buf) {
  if (buf.length < 2) return undefined
  const opcode = buf[0] & 0x0f
  const masked = (buf[1] & 0x80) !== 0
  let len = buf[1] & 0x7f
  let offset = 2
  if (len === 126) {
    if (buf.length < 4) return undefined
    len = buf.readUInt16BE(2)
    offset = 4
  } else if (len === 127) {
    if (buf.length < 10) return undefined
    len = Number(buf.readBigUInt64BE(2))
    offset = 10
  }
  let maskKey
  if (masked) {
    if (buf.length < offset + 4) return undefined
    maskKey = buf.subarray(offset, offset + 4)
    offset += 4
  }
  if (buf.length < offset + len) return undefined
  const payload = Buffer.from(buf.subarray(offset, offset + len))
  if (masked) for (let i = 0; i < payload.length; i++) payload[i] ^= maskKey[i % 4]
  return { opcode, payload, consumed: offset + len }
}

class GatewayWebSocket {
  constructor ({ url, onOpen, onMessage, onClose, onError }) {
    this.parsed = parseGatewayUrl(url)
    this.onOpen = onOpen
    this.onMessage = onMessage
    this.onClose = onClose
    this.onError = onError
    this.socket = null
    this.buffer = Buffer.alloc(0)
    this.closed = false
  }

  connect () {
    const { host, port, path, secure } = this.parsed
    const key = crypto.randomBytes(16).toString('base64')
    const request = [
      `GET ${path} HTTP/1.1`,
      `Host: ${host}:${port}`,
      'Upgrade: websocket',
      'Connection: Upgrade',
      'Sec-WebSocket-Version: 13',
      `Sec-WebSocket-Key: ${key}`,
      '',
      ''
    ].join('\r\n')
    let socket
    const onConnected = () => {
      this.socket = socket
      socket.setNoDelay(true)
      socket.write(request)
    }
    if (secure) {
      socket = tls.connect({ host, port, servername: net.isIP(host) ? undefined : host, rejectUnauthorized: false }, onConnected)
    } else {
      socket = net.connect({ host, port }, onConnected)
    }
    this.socket = socket
    socket.setTimeout(CONNECT_TIMEOUT_MS, () => this.fail(new Error('gateway websocket handshake timeout')))
    let handshakeDone = false
    socket.on('data', (chunk) => {
      this.buffer = Buffer.concat([this.buffer, chunk])
      if (!handshakeDone) {
        const end = this.buffer.indexOf('\r\n\r\n')
        if (end < 0) return
        const head = this.buffer.subarray(0, end).toString('utf8')
        this.buffer = this.buffer.subarray(end + 4)
        const statusLine = head.split('\r\n')[0]
        if (!/^HTTP\/1\.1 101/.test(statusLine)) {
          this.fail(new Error(`gateway websocket handshake failed: ${statusLine}`))
          return
        }
        handshakeDone = true
        socket.setTimeout(0)
        try { this.onOpen?.() } catch {}
        if (this.buffer.length > 0) this.consume()
        return
      }
      this.consume()
    })
    socket.on('error', (error) => this.fail(error))
    socket.on('close', () => {
      if (this.closed) return
      this.closed = true
      try { this.onClose?.() } catch {}
    })
  }

  consume () {
    for (;;) {
      const decoded = decodeFrame(this.buffer)
      if (!decoded) return
      this.buffer = this.buffer.subarray(decoded.consumed)
      if (decoded.opcode === 0x9) { this.writeFrame(0xa, decoded.payload); continue }
      if (decoded.opcode === 0xa) { try { this.onMessage?.({ kind: 'pong', __pong: true }) } catch {} continue }
      if (decoded.opcode === 0x8) { this.close(); return }
      if (decoded.opcode !== 0x1 && decoded.opcode !== 0x2) continue
      const text = decoded.payload.toString('utf8')
      let value
      try { value = JSON.parse(text) } catch { continue }
      try { this.onMessage?.(value) } catch {}
    }
  }

  writeFrame (opcode, payload) {
    if (!this.socket || this.closed) return false
    try {
      this.socket.write(encodeClientFrame(opcode, payload))
      return true
    } catch {
      return false
    }
  }

  sendJson (value) {
    return this.writeFrame(0x1, Buffer.from(JSON.stringify(value), 'utf8'))
  }

  close () {
    if (this.closed) return
    this.closed = true
    try { this.socket?.destroy() } catch {}
    try { this.onClose?.() } catch {}
  }

  fail (error) {
    try { this.onError?.(error) } catch {}
    this.close()
  }
}

/**
 * Connector: one gateway tunnel, forwarding gateway HTTP/WS streams to the
 * loopback-only plain reverse proxy.
 *
 * @param {{
 *   gatewayUrl: string,
 *   token: string,
 *   name: string,
 *   launchToken: string,
 *   secret?: string,
 *   internalHost?: string,
 *   internalPort: number,
 *   log?: (line: string) => void,
 *   onState?: (state: object) => void
 * }} options
 */
export function createGatewayConnector (options) {
  const log = options.log ?? (() => {})
  const internalHost = options.internalHost ?? '127.0.0.1'
  const state = {
    enabled: true,
    connected: false,
    url: options.gatewayUrl,
    token: options.token,
    name: options.name,
    lastError: null,
    connectedAt: null,
    reconnects: 0,
    lastPongAt: null,
    internalPort: options.internalPort,
    fps: null
  }
  let socket
  let stopped = false
  let connecting = false
  let backoff = 1000
  let pingTimer
  let reconnectTimer
  let pending = new Map()
  let internalSockets = new Map()

  const emitState = () => {
    try { options.onState?.({ ...state }) } catch {}
  }

  function registrationUrl () {
    const parsed = parseGatewayUrl(options.gatewayUrl)
    const url = new URL(parsed.url.toString())
    url.searchParams.set('token', options.token)
    url.searchParams.set('name', options.name)
    url.searchParams.set('launchToken', options.launchToken)
    if (options.secret) url.searchParams.set('secret', options.secret)
    return url.toString()
  }

  function startPing () {
    stopPing()
    pingTimer = setInterval(() => {
      if (!state.connected) return
      if (state.lastPongAt && Date.now() - state.lastPongAt > PONG_TIMEOUT_MS) {
        log('[remote-relay] gateway: pong timeout, reconnecting')
        socket?.close()
        return
      }
      socket?.sendJson({ kind: 'ping' })
    }, PING_INTERVAL_MS)
    pingTimer.unref?.()
  }

  function stopPing () {
    if (pingTimer) clearInterval(pingTimer)
    pingTimer = undefined
  }

  function scheduleReconnect () {
    if (stopped || reconnectTimer) return
    const delay = backoff
    backoff = Math.min(backoff * 2, 30000)
    state.reconnects += 1
    log(`[remote-relay] gateway: reconnecting in ${Math.round(delay / 1000)}s (attempt ${state.reconnects})`)
    reconnectTimer = setTimeout(() => {
      reconnectTimer = undefined
      connect()
    }, delay)
    reconnectTimer.unref?.()
  }

  function connect () {
    if (stopped || connecting) return
    connecting = true
    state.connected = false
    state.lastError = null
    const url = registrationUrl()
    log(`[remote-relay] gateway: connecting to ${parseGatewayUrl(options.gatewayUrl).host}:${parseGatewayUrl(options.gatewayUrl).port} as "${options.name}" token=${maskToken(options.token)} port=${options.internalPort}`)
    socket = new GatewayWebSocket({
      url,
      onOpen: () => {
        connecting = false
        backoff = 1000
        state.connected = true
        state.connectedAt = new Date().toISOString()
        state.lastPongAt = Date.now()
        log('[remote-relay] gateway: tunnel registered')
        startPing()
        emitState()
      },
      onMessage: handleFrame,
      onError: (error) => {
        state.lastError = error?.message ?? String(error)
        connecting = false
      },
      onClose: () => {
        connecting = false
        if (state.connected) log('[remote-relay] gateway: tunnel closed')
        state.connected = false
        stopPing()
        cleanupEverything()
        emitState()
        scheduleReconnect()
      }
    })
    try {
      socket.connect()
    } catch (error) {
      state.lastError = error.message
      connecting = false
      scheduleReconnect()
    }
  }

  function cleanupEverything () {
    for (const [, entry] of pending) {
      try { entry.response?.destroy() } catch {}
    }
    pending.clear()
    for (const [, entry] of internalSockets) {
      try { entry.socket?.destroy() } catch {}
    }
    internalSockets.clear()
  }

  function handleFrame (frame) {
    if (!frame || typeof frame !== 'object') return
    if (process.env.GATEWAY_DEBUG === '1') log(`gateway frame: ${frame.kind} id=${frame.id ?? ''} b64=${frame.b64 ? frame.b64.length : ''}`)
    switch (frame.kind) {
      case 'ping':
        socket?.sendJson({ kind: 'pong' })
        return
      case 'pong':
        state.lastPongAt = Date.now()
        return
      case 'http':
        pending.set(String(frame.id), {
          id: String(frame.id),
          method: frame.method ?? 'GET',
          url: frame.url ?? '/',
          headers: frame.headers && typeof frame.headers === 'object' ? frame.headers : {},
          bodyB64: typeof frame.bodyB64 === 'string' ? frame.bodyB64 : '',
          timer: setTimeout(() => flushHttp(frame.id), 5000)
        })
        return
      case 'http-end':
        flushHttp(frame.id)
        return
      case 'ws-open':
        openInternalWs(frame)
        return
      case 'ws-msg':
        forwardToInternalWs(frame)
        return
      case 'ws-close':
        closeInternalWs(frame.id)
        return
      default:
        return
    }
  }

  function flushHttp (id) {
    const key = String(id)
    const entry = pending.get(key)
    if (!entry) return
    pending.delete(key)
    if (entry.timer) clearTimeout(entry.timer)
    let body
    try { body = entry.bodyB64 ? Buffer.from(entry.bodyB64, 'base64') : undefined } catch { body = undefined }
    const headers = { ...entry.headers }
    delete headers['connection']
    delete headers['keep-alive']
    delete headers['transfer-encoding']
    delete headers['upgrade']
    if (body) headers['content-length'] = String(body.length)
    const request = http.request({
      host: internalHost,
      port: options.internalPort,
      method: entry.method,
      path: entry.url,
      headers
    }, (response) => {
      const chunks = []
      let received = 0
      response.on('data', (chunk) => {
        received += chunk.length
        if (received > MAX_BODY_BYTES) {
          response.destroy(new Error('response too large'))
          return
        }
        chunks.push(chunk)
      })
      response.on('end', () => {
        const payload = {
          id: key,
          kind: 'http-res',
          status: response.statusCode ?? 502,
          headers: normalizeResponseHeaders(response.headers),
          bodyB64: Buffer.concat(chunks).toString('base64')
        }
        socket?.sendJson(payload)
        socket?.sendJson({ id: key, kind: 'http-end' })
      })
      response.on('error', () => {
        socket?.sendJson({ id: key, kind: 'http-res', status: 502, headers: {}, bodyB64: '' })
        socket?.sendJson({ id: key, kind: 'http-end' })
      })
    })
    request.on('error', (error) => {
      log(`[remote-relay] gateway: http forward failed: ${error.message}`)
      socket?.sendJson({ id: key, kind: 'http-res', status: 502, headers: { 'content-type': 'text/plain; charset=utf-8' }, bodyB64: Buffer.from(`relay connector error: ${error.message}\n`).toString('base64') })
      socket?.sendJson({ id: key, kind: 'http-end' })
    })
    if (body) request.end(body)
    else request.end()
  }

  function normalizeResponseHeaders (headers) {
    const out = {}
    for (const [name, value] of Object.entries(headers)) {
      if (value === undefined) continue
      const key = name.toLowerCase()
      if (['connection', 'keep-alive', 'transfer-encoding', 'upgrade', 'trailer', 'te'].includes(key)) continue
      out[key] = value
    }
    return out
  }

  function openInternalWs (frame) {
    const key = String(frame.id)
    const headers = { ...(frame.headers ?? {}) }
    delete headers['connection']
    delete headers['upgrade']
    delete headers['sec-websocket-extensions']
    delete headers['sec-websocket-accept']
    delete headers['sec-websocket-version']
    delete headers['sec-websocket-key']
    // The phone's handshake terminates at the gateway; the connector's hop to
    // the internal proxy needs its own fresh WebSocket handshake.
    headers['sec-websocket-key'] = crypto.randomBytes(16).toString('base64')
    headers['sec-websocket-version'] = '13'
    headers.connection = 'Upgrade'
    headers.upgrade = 'websocket'
    const request = http.request({
      host: internalHost,
      port: options.internalPort,
      method: 'GET',
      path: frame.url ?? '/',
      headers,
      agent: false
    })
    request.on('upgrade', (response, socket) => {
      internalSockets.set(key, { socket, buffer: Buffer.alloc(0) })
      socket.on('data', (chunk) => {
        const entry = internalSockets.get(key)
        if (!entry) return
        entry.buffer = Buffer.concat([entry.buffer, chunk])
        for (;;) {
          const decoded = decodeFrame(entry.buffer)
          if (!decoded) break
          entry.buffer = entry.buffer.subarray(decoded.consumed)
          if (decoded.opcode === 0x9) { entry.socket.write(encodeClientFrame(0xa, decoded.payload)); continue }
          if (decoded.opcode === 0x8) {
            closeInternalWs(key, true)
            continue
          }
          if (decoded.opcode !== 0x1 && decoded.opcode !== 0x2) continue
          if (process.env.GATEWAY_DEBUG === '1') log(`internal ws frame opcode=${decoded.opcode} len=${decoded.payload.length} -> gateway`)
          socketSendFrameToGateway(key, decoded.payload, decoded.opcode)
        }
      })
      socket.on('error', () => closeInternalWs(key, true))
      socket.on('close', () => closeInternalWs(key, true))
      socketSendToGateway(key, undefined, { kind: 'ws-open-ok' })
    })
    request.on('response', (response) => {
      socketSendToGateway(key, undefined, { kind: 'ws-open-fail', status: response.statusCode })
      response.resume()
    })
    request.on('error', () => socketSendToGateway(key, undefined, { kind: 'ws-open-fail', status: 502, error: 'connect failed' }))
    request.end()
  }

  function socketSendToGateway (id, payload, extra) {
    if (extra) {
      socket?.sendJson({ id, ...extra })
      return
    }
    socket?.sendJson({ id, kind: 'ws-msg', b64: payload.toString('base64') })
  }

  /** Forward one internal WS frame, tagging op (1=text, 2=binary) for the gateway. */
  function socketSendFrameToGateway (id, payload, opcode) {
    const op = opcode === 0x2 ? 2 : 1
    socket?.sendJson({ id, kind: 'ws-msg', b64: payload.toString('base64'), op })
  }

  function forwardToInternalWs (frame) {
    const key = String(frame.id)
    const entry = internalSockets.get(key)
    if (!entry || typeof frame.b64 !== 'string') return
    let payload
    try { payload = Buffer.from(frame.b64, 'base64') } catch { return }
    // DSH mux messages are JSON text frames; forward as text when the bytes are
    // valid UTF-8 JSON, otherwise as a binary frame.
    const text = payload.toString('utf8')
    if (process.env.GATEWAY_DEBUG === '1') log(`gateway ws-msg -> internal len=${payload.length}`)
    // Respect the gateway's optional `op` (phone frame opcode); otherwise infer:
    // DSH mux messages are JSON text frames, anything non-JSON is binary.
    let isText
    if (frame.op === 1) isText = true
    else if (frame.op === 2) isText = false
    else isText = Buffer.from(text, 'utf8').equals(payload) && /^[{[].*[}\]]$/s.test(text.trim())
    entry.socket.write(encodeClientFrame(isText ? 0x1 : 0x2, payload))
  }

  function closeInternalWs (id, fromInternal = false) {
    const key = String(id)
    const entry = internalSockets.get(key)
    if (entry) {
      internalSockets.delete(key)
      try { entry.socket.write(encodeClientFrame(0x8, Buffer.alloc(0))) } catch {}
      try { entry.socket.destroy() } catch {}
    }
    if (!fromInternal) socket?.sendJson({ id: key, kind: 'ws-close' })
    else socket?.sendJson({ id: key, kind: 'ws-close' })
  }

  return {
    state,
    start () {
      stopped = false
      connect()
    },
    stop () {
      stopped = true
      stopPing()
      if (reconnectTimer) clearTimeout(reconnectTimer)
      reconnectTimer = undefined
      socket?.close()
      cleanupEverything()
    },
    rotate (newToken) {
      options.token = newToken
      state.token = newToken
      log(`[remote-relay] gateway: rotating remote token to ${maskToken(newToken)}, reconnecting`)
      stopped = false
      socket?.close()
      if (reconnectTimer) clearTimeout(reconnectTimer)
      reconnectTimer = undefined
      backoff = 1000
      connect()
    },
    setIdentity ({ token, name }) {
      if (token) options.token = token
      if (name) options.name = name
      state.token = options.token
      state.name = options.name
    },
    getState () {
      return { ...state }
    }
  }
}



