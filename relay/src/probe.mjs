// Startup protocol self-check for the relay.
//
// DSH is a pre-release target with no compatibility promise, so the relay
// probes the exact Remote surface the phone client needs and publishes the
// result (health + pairing.json). A failed probe marks the pairing inactive
// instead of silently handing the phone a pairing that cannot work.
//
// Probes run against the loopback upstream (127.0.0.1:<webServer.port>), not
// through the TLS listener: they describe the DSH side, and the TLS hop is
// covered by scripts/relay-smoke.ps1.
//
// Wire facts reused from the Core client (app/src/DshRemote.Core/Protocol):
//   session/list     -> {"args":{"_request":{}}}
//   session/events   -> {"args":{"request":{address:{kind:'session',sessionId},maxMessages}}}
//   session/follow   -> gateway stream; payload {"args":{"request":{address,maxMessages}}}
//   $events          -> {"args":{}}
// One API socket owns exactly one Peer: closing the mux socket invalidates the
// cookie ("之后使用该 Cookie 的 HTTP 被拒绝"), so every stream probe must reuse
// the single mux connection opened for the lifetime of this self-check.
import crypto from 'node:crypto'
import fs from 'node:fs'
import http from 'node:http'
import https from 'node:https'
import os from 'node:os'
import path from 'node:path'

const REQUIRED_CAPABILITIES = ['$connect', 'session/list', 'session/events', '$events', 'session/follow']

/** Relay version straight from the package manifest (single source of truth). */
export function readRelayVersion (pkgRoot) {
  try {
    const pkg = JSON.parse(fs.readFileSync(path.join(pkgRoot, 'package.json'), 'utf8'))
    return typeof pkg.version === 'string' ? pkg.version : 'unknown'
  } catch {
    return 'unknown'
  }
}

/**
 * Resolve the running DSH runtime version from the packaged app (Electron's
 * patched fs can read app.asar paths from the main process), then from the
 * profile install as a fallback.
 */
export function resolveDshVersion () {
  const candidates = []
  const resources = typeof process.resourcesPath === 'string' ? process.resourcesPath : undefined
  if (resources) {
    candidates.push(path.join(resources, 'app.asar', 'dsh', 'node_modules', '@deepseek-ai', 'dsh', 'package.json'))
    candidates.push(path.join(resources, 'app.asar', 'node_modules', '@deepseek-ai', 'dsh', 'package.json'))
    candidates.push(path.join(resources, 'app.asar.unpacked', 'dsh', 'node_modules', '@deepseek-ai', 'dsh', 'package.json'))
    candidates.push(path.join(resources, 'app.asar.unpacked', 'node_modules', '@deepseek-ai', 'dsh', 'package.json'))
  }
  if (process.execPath) {
    const dir = path.dirname(process.execPath)
    candidates.push(path.join(dir, 'resources', 'app.asar', 'dsh', 'node_modules', '@deepseek-ai', 'dsh', 'package.json'))
  }
  candidates.push(path.join(os.homedir(), '.dsh', 'profiles', 'node_modules', '@deepseek-ai', 'dsh', 'package.json'))
  candidates.push(path.join(os.homedir(), '.dsh', 'profiles', 'desktop', 'node_modules', '@deepseek-ai', 'dsh', 'package.json'))

  for (const file of candidates) {
    try {
      const pkg = JSON.parse(fs.readFileSync(file, 'utf8'))
      if (typeof pkg.version === 'string' && pkg.version.length > 0) {
        return { version: pkg.version, source: file }
      }
    } catch {
      // Missing/unreadable candidate: try the next layout.
    }
  }
  return { version: 'unknown', source: null }
}

function request ({ protocol, host, port, method, requestPath, headers = {}, body, timeoutMs }) {
  const transport = protocol === 'https:' ? https : http
  return new Promise((resolve, reject) => {
    const req = transport.request({
      host,
      port,
      method,
      path: requestPath,
      headers: {
        ...headers,
        ...(body ? { 'content-length': Buffer.byteLength(body) } : {})
      },
      rejectUnauthorized: false,
      timeout: timeoutMs
    }, (res) => {
      const chunks = []
      res.on('data', (chunk) => chunks.push(chunk))
      res.on('end', () => resolve({ status: res.statusCode, headers: res.headers, body: Buffer.concat(chunks).toString('utf8') }))
    })
    req.on('timeout', () => req.destroy(new Error(`timeout after ${timeoutMs}ms`)))
    req.on('error', reject)
    if (body) req.write(body)
    req.end()
  })
}

function firstCookie (setCookie) {
  const all = Array.isArray(setCookie) ? setCookie : setCookie ? [setCookie] : []
  return all.map((entry) => String(entry).split(';')[0]).join('; ')
}

function encodeFrame (opcode, payload) {
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

/** Decode one unmasked server->client WebSocket frame, or undefined when incomplete. */
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

/**
 * Open one `/api/remote.mux` socket and expose `openStream()` on it.
 * The returned socket is the probe's single API connection: every logical
 * stream must share it or the cookie would be invalidated by the first close.
 */
function openMux ({ protocol, port, cookie, timeoutMs }) {
  const transport = protocol === 'https:' ? https : http
  return new Promise((resolve, reject) => {
    const key = crypto.randomBytes(16).toString('base64')
    const pending = new Map()
    let socket
    let closed = false
    const failAll = (reason) => {
      for (const [streamId, entry] of pending) {
        pending.delete(streamId)
        clearTimeout(entry.timer)
        entry.reject(new Error(reason))
      }
    }
    const req = transport.request({
      host: '127.0.0.1',
      port,
      method: 'GET',
      path: '/api/remote.mux',
      headers: {
        connection: 'Upgrade',
        upgrade: 'websocket',
        'sec-websocket-version': '13',
        'sec-websocket-key': key,
        cookie,
        host: `127.0.0.1:${port}`
      },
      rejectUnauthorized: false,
      timeout: timeoutMs
    })
    req.on('upgrade', (res, upgradedSocket, head) => {
      socket = upgradedSocket
      let buffer = head && head.length > 0 ? Buffer.from(head) : Buffer.alloc(0)
      const consume = () => {
        for (;;) {
          const decoded = decodeFrame(buffer)
          if (!decoded) return
          buffer = buffer.subarray(decoded.consumed)
          if (decoded.opcode === 0x9) { try { socket.write(encodeFrame(0xa, decoded.payload)) } catch {} continue }
          if (decoded.opcode === 0x8) { closed = true; failAll('socket closed'); return }
          if (decoded.opcode !== 0x1) continue
          let value
          try { value = JSON.parse(decoded.payload.toString('utf8')) } catch { continue }
          const entry = pending.get(value.streamId)
          if (!entry) continue
          try {
            if (entry.predicate(value)) {
              pending.delete(value.streamId)
              clearTimeout(entry.timer)
              entry.resolve(value)
            }
          } catch (error) {
            pending.delete(value.streamId)
            clearTimeout(entry.timer)
            entry.reject(error)
          }
        }
      }
      socket.on('data', (chunk) => {
        buffer = Buffer.concat([buffer, chunk])
        consume()
      })
      socket.on('error', (error) => { closed = true; failAll(`socket error: ${error.message}`) })
      socket.on('close', () => { closed = true; failAll('socket closed') })
      consume()
      resolve({
        openStream (streamId, endpoint, payload, predicate, streamTimeoutMs) {
          if (closed) return Promise.reject(new Error('socket closed'))
          return new Promise((resolveStream, rejectStream) => {
            const timer = setTimeout(() => {
              pending.delete(streamId)
              rejectStream(new Error(`timeout after ${streamTimeoutMs}ms`))
            }, streamTimeoutMs)
            pending.set(streamId, { predicate, resolve: resolveStream, reject: rejectStream, timer })
            try {
              socket.write(encodeFrame(0x1, Buffer.from(JSON.stringify({ type: 'open', streamId, endpoint, payload }), 'utf8')))
            } catch (error) {
              pending.delete(streamId)
              clearTimeout(timer)
              rejectStream(error)
            }
          })
        },
        close () {
          closed = true
          failAll('mux closed')
          try { socket.destroy() } catch {}
        }
      })
    })
    req.on('timeout', () => req.destroy(new Error(`upgrade timeout after ${timeoutMs}ms`)))
    req.on('response', (res) => reject(new Error(`upgrade rejected with HTTP ${res.statusCode}`)))
    req.on('error', (error) => reject(error))
    req.end()
  })
}

/**
 * Probe the five Remote capabilities the phone client depends on.
 * Never throws: every failure is reported in the result.
 *
 * @param {{ port: number, token: string, protocol?: string, timeoutMs?: number, log?: (line: string) => void }} options
 * @returns {Promise<{ ok: boolean, checkedAt: string, capabilities: Record<string, boolean|string>, missing: string[], errors: string[], sessionSample: string|null }>}
 */
export async function probeProtocol ({ port, token, protocol = 'http:', timeoutMs = 12000, log = () => {} }) {
  const startedAt = Date.now()
  const remaining = () => Math.max(500, timeoutMs - (Date.now() - startedAt))
  const capabilities = {
    '$connect': false,
    'session/list': false,
    'session/events': 'unknown',
    'session/follow': 'unknown',
    '$events': false
  }
  const errors = []
  const base = { protocol, host: '127.0.0.1', port }

  let cookie = ''
  try {
    const connect = await request({
      ...base,
      method: 'GET',
      requestPath: `/connect?token=${encodeURIComponent(token)}&interactions=answer`,
      timeoutMs: remaining()
    })
    cookie = firstCookie(connect.headers['set-cookie'])
    if (connect.status === 302 && cookie) {
      capabilities['$connect'] = true
    } else {
      errors.push(`$connect: expected 302 + Set-Cookie, got HTTP ${connect.status}`)
    }
  } catch (error) {
    errors.push(`$connect: ${error && error.message ? error.message : String(error)}`)
  }

  if (!cookie) {
    return {
      ok: false,
      checkedAt: new Date().toISOString(),
      capabilities,
      missing: REQUIRED_CAPABILITIES.filter((name) => capabilities[name] === false),
      errors,
      sessionSample: null
    }
  }

  const rpc = async (endpoint, args) => {
    const body = JSON.stringify({
      type: 'client-request',
      rpcId: `probe-${crypto.randomUUID()}`,
      method: endpoint,
      payload: { args }
    })
    const res = await request({
      ...base,
      method: 'POST',
      requestPath: `/api/${endpoint}`,
      headers: { cookie, 'content-type': 'application/json' },
      body,
      timeoutMs: remaining()
    })
    let json
    try { json = JSON.parse(res.body) } catch {}
    return { status: res.status, json }
  }

  let sessionId = null
  try {
    const listed = await rpc('session/list', { _request: {} })
    const ok = listed.status === 200 && listed.json?.result?.ok === true
    capabilities['session/list'] = ok
    if (!ok) {
      const detail = listed.json?.result?.error?.code ?? `HTTP ${listed.status}`
      errors.push(`session/list: ${detail}`)
    } else {
      const items = Array.isArray(listed.json.result.value?.items) ? listed.json.result.value.items : []
      const candidate = items.find((item) => item && !item.blank && !item.parentSessionId && typeof item.sessionId === 'string')
      sessionId = candidate?.sessionId ?? null
    }
  } catch (error) {
    capabilities['session/list'] = false
    errors.push(`session/list: ${error && error.message ? error.message : String(error)}`)
  }

  if (sessionId) {
    try {
      const events = await rpc('session/events', {
        request: { address: { kind: 'session', sessionId }, maxMessages: 1 }
      })
      const ok = events.status === 200 && events.json?.result?.ok === true
      capabilities['session/events'] = ok
      if (!ok) {
        const detail = events.json?.result?.error?.code ?? `HTTP ${events.status}`
        errors.push(`session/events: ${detail}`)
      }
    } catch (error) {
      capabilities['session/events'] = false
      errors.push(`session/events: ${error && error.message ? error.message : String(error)}`)
    }
  } else if (capabilities['session/list'] === true) {
    // A fresh DSH home can legitimately have no sessions; the endpoint itself
    // was not disproven, so this is not a compatibility failure.
    capabilities['session/events'] = 'unknown'
    log('[remote-relay] probe: no session available, skipping session/events/follow sampling')
  }

  let mux
  try {
    mux = await openMux({ protocol, port, cookie, timeoutMs: Math.min(remaining(), 5000) })
  } catch (error) {
    capabilities['$events'] = false
    capabilities['session/follow'] = false
    errors.push(`mux upgrade: ${error && error.message ? error.message : String(error)}`)
  }

  if (mux) {
    try {
      // $events: opening the forwarded-event stream must yield the ready item.
      try {
        await mux.openStream('probe-events', '$events', { args: {} }, (frame) => frame?.type === 'item' && frame?.value?.type === 'ready', remaining())
        capabilities['$events'] = true
      } catch (error) {
        capabilities['$events'] = false
        errors.push(`$events: ${error && error.message ? error.message : String(error)}`)
      }

      // session/follow: a stream endpoint. A session-level rejection (e.g.
      // session/agent-busy) still proves the endpoint exists; gateway/* means
      // the endpoint is absent or its payload shape changed.
      if (sessionId) {
        try {
          const first = await mux.openStream('probe-follow', 'session/follow', {
            args: { request: { address: { kind: 'session', sessionId }, maxMessages: 1 } }
          }, (frame) => frame?.type === 'item' || frame?.type === 'end' || frame?.type === 'error', remaining())
          const code = first?.error?.code
          const gatewayFailure = typeof code === 'string' && code.startsWith('gateway/')
          capabilities['session/follow'] = !gatewayFailure
          if (gatewayFailure) errors.push(`session/follow: ${code}`)
        } catch (error) {
          capabilities['session/follow'] = false
          errors.push(`session/follow: ${error && error.message ? error.message : String(error)}`)
        }
      }
    } finally {
      try { mux.close() } catch {}
    }
  }

  const missing = REQUIRED_CAPABILITIES.filter((name) => capabilities[name] === false)
  return {
    ok: missing.length === 0,
    checkedAt: new Date().toISOString(),
    capabilities,
    missing,
    errors,
    sessionSample: sessionId
  }
}

export { REQUIRED_CAPABILITIES }
