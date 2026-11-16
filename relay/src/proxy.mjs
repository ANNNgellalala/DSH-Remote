// TLS reverse proxy: one LAN/VPN address (plus loopback for local E2E) -> the
// DSH process-local web server on 127.0.0.1:<upstreamPort>.
//
// Thin on purpose: no body parsing, no rewriting of application semantics.
// The only request mutations are the ones the upstream trust fence requires:
//   - Host: 127.0.0.1:<upstreamPort>  (authority-bound cookie audience,
//     loopback Host/Origin fence, DNS-rebinding defense)
//   - Origin: http://127.0.0.1:<upstreamPort> when the client sent one
// Everything else (cookies, Set-Cookie, headers, bodies, status codes, 101
// responses) passes through untouched.
import http from 'node:http'
import https from 'node:https'
import dgram from 'node:dgram'
import fs from 'node:fs'
import os from 'node:os'
import net from 'node:net'
import path from 'node:path'
import { timingSafeEqual } from 'node:crypto'
import {
  isLoopbackAddress,
  CONFIG_PATH,
  PAIR_JS_ASSET_PATH,
  PAIR_JS_SOURCE,
  PAIR_PAGE_PATH,
  PAIR_ROTATE_PATH,
  PAIR_QR_ASSET_PATH,
  SELF_CHECK_PATH,
  pairingPageHeaders,
  renderPairingPage
} from './pair-page.mjs'
import {
  DOWNLOAD_PAGE_PATH,
  hashEntries,
  resolveDownload,
  scanDownloads
} from './downloads.mjs'
import { downloadPageHeaders, renderDownloadPage } from './download-page.mjs'
import { AUTO_APPROVE_PATH } from './auto-approve.mjs'

/** Headers that describe one hop only and must not be forwarded. */
const HOP_BY_HOP = new Set([
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

/** Binding these would expose the relay on every interface: never allowed. */
const WILDCARD_HOSTS = new Set(['0.0.0.0', '::', '[::]', '*'])/** Interface names that are almost never the phone-reachable LAN link. */
const VIRTUAL_IFACE = /(vethernet|wsl|hyper-v|vmware|virtualbox|docker|loopback|vgate|tailscale|zerotier|npcap|tap-)/i

export function isLoopbackHost (host) {
  return host === '127.0.0.1' || host === '::1' || host === '[::1]' || host === 'localhost'
}

/** Relay-owned Windows ACL guard API (served by both the TLS and gateway listeners). */
export const ACL_STATUS_PATH = '/__relay/acl/status'
export const ACL_CHECK_PATH = '/__relay/acl/check'

/**
 * Phone-facing compact session list (relay v0.6.0+). Served by both listeners,
 * token-authenticated like the ACL API; the upstream call/trimming lives in
 * session-summary.mjs and is injected as `sessionSummaryApi`.
 */
export const SESSION_SUMMARY_PATH = '/__relay/sessions/summary'

/**
 * Phone-facing auto-approval rules (relay 0.7.0+). Served by both listeners,
 * token-authenticated like the ACL API; the rules, matcher and listener live in
 * auto-approve.mjs and are injected as `autoApproveApi`.
 */
export { AUTO_APPROVE_PATH }

/**
 * Read a small JSON request body with a hard size cap.
 * @param {import('node:http').IncomingMessage} req
 * @param {number} limit
 * @returns {Promise<Record<string, unknown>>}
 */
function readJsonBody (req, limit) {
  return new Promise((resolve, reject) => {
    const chunks = []
    let total = 0
    req.on('data', (chunk) => {
      total += chunk.length
      if (total > limit) {
        reject(new Error('request body too large'))
        req.destroy()
        return
      }
      chunks.push(chunk)
    })
    req.on('end', () => {
      if (chunks.length === 0) {
        resolve({})
        return
      }
      try {
        resolve(JSON.parse(Buffer.concat(chunks).toString('utf8')))
      } catch (error) {
        reject(new Error(`invalid JSON body: ${error.message}`))
      }
    })
    req.on('error', reject)
  })
}

/** Boolean query flag: `1`, `true` or `yes` (case-insensitive) turn it on. */
function isTruthyQueryFlag (value) {
  if (typeof value !== 'string') return false
  const normalized = value.trim().toLowerCase()
  return normalized === '1' || normalized === 'true' || normalized === 'yes'
}

/**
 * Reject wildcard/invalid binds loudly. `dsh-host-webserver` itself refuses
 * wildcards; the relay has the same rule so a typo cannot open the whole host.
 */
export function assertSafeBindHost (host) {
  if (typeof host !== 'string' || host.trim() === '') {
    throw new Error('dsh-remote-relay: host must be a concrete IPv4/IPv6 literal (got empty value)')
  }
  const h = host.trim()
  if (WILDCARD_HOSTS.has(h)) {
    throw new Error(`dsh-remote-relay: refusing wildcard bind host ${JSON.stringify(h)}; pass a concrete LAN/VPN address`)
  }
  if (h.includes('/') || h.includes(':')) {
    throw new Error(`dsh-remote-relay: host must be a bare IP literal without port/scheme (got ${JSON.stringify(h)})`)
  }
  return h
}

/**
 * Best-effort discovery of the address the phone can reach: ask the OS routing
 * table which source address it would use toward the internet (UDP connect
 * sends no packet), then fall back to the first plausible physical interface.
 */
export function detectLanIPv4 () {
  const routed = routedIPv4()
  if (routed) return routed
  const candidates = []
  for (const [name, infos] of Object.entries(os.networkInterfaces())) {
    for (const info of infos ?? []) {
      if (info.family !== 'IPv4' || info.internal) continue
      if (info.address.startsWith('169.254.')) continue
      candidates.push({ name, address: info.address, virtual: VIRTUAL_IFACE.test(name) })
    }
  }
  const physical = candidates.filter((c) => !c.virtual)
  const pick = physical[0] ?? candidates[0]
  if (!pick) {
    throw new Error('dsh-remote-relay: no non-loopback IPv4 address found; set config.host explicitly')
  }
  return pick.address
}

function routedIPv4 () {
  let socket
  try {
    socket = dgram.createSocket('udp4')
    socket.connect(53, '8.8.8.8')
    const address = socket.address().address
    return net.isIPv4(address) && !address.startsWith('127.') ? address : undefined
  } catch {
    return undefined
  } finally {
    try { socket?.close() } catch {}
  }
}

/** Rewrite the two headers the upstream trust fence binds to its own authority. */
export function rewriteRequestHeaders (headers, upstreamPort, upstreamProtocol = 'http:') {
  const upstreamAuthority = `127.0.0.1:${upstreamPort}`
  const out = { host: upstreamAuthority }
  for (const [name, value] of Object.entries(headers)) {
    if (value === undefined) continue
    const key = name.toLowerCase()
    if (key === 'host') continue
    if (HOP_BY_HOP.has(key)) continue
    if (key === 'origin') {
      // The fence compares Origin against Host under the Origin's scheme; point
      // both at the loopback upstream. The TLS hop is ours and already ended.
      out.origin = `${upstreamProtocol}//${upstreamAuthority}`
      continue
    }
    out[key] = value
  }
  return out
}

export class UpstreamProxy {
  constructor ({ upstreamPort, upstreamProtocol = 'http:', health, pairingProvider, assetsDir, downloadDir, downloadProvider, rotateProvider, probeProvider, configProvider, configApplyProvider, configFilePath, aclApi, sessionSummaryApi, autoApproveApi, helpers = 'all' }) {
    this.upstreamPort = Number(upstreamPort)
    this.upstreamProtocol = upstreamProtocol === 'https:' ? 'https:' : 'http:'
    this.health = health
    this.pairingProvider = typeof pairingProvider === 'function' ? pairingProvider : () => null
    this.assetsDir = assetsDir ? String(assetsDir) : null
    this.downloadDir = downloadDir ? String(downloadDir) : null
    this.downloadProvider = typeof downloadProvider === 'function' ? downloadProvider : null
    this.rotateProvider = typeof rotateProvider === 'function' ? rotateProvider : null
    // Re-runs the DSH protocol self-check (waits for appReady, retries transient
    // mux failures) and returns the fresh health.protocolProbe state.
    this.probeProvider = typeof probeProvider === 'function' ? probeProvider : null
    this.configProvider = typeof configProvider === 'function' ? configProvider : null
    this.configApplyProvider = typeof configApplyProvider === 'function' ? configApplyProvider : null
    this.configFilePath = configFilePath ? String(configFilePath) : null
    // Helper routing policy: 'all' (public TLS listener), 'health' (gateway
    // connector's internal plain proxy: health only, never the pairing token),
    // 'none' (bare proxy).
    this.helpers = helpers === false ? 'none' : (helpers === 'health' ? 'health' : 'all')
    // Windows ACL guard status/check API. `aclApi` is { status(), check(body), token() }.
    // It is available on BOTH listeners (TLS for LAN, internal plain proxy for the
    // gateway) because the phone reaches this desktop either way; every non-loopback
    // call — and every call through the gateway's loopback hop — must present the
    // pairing token as a bearer credential.
    this.aclApi = aclApi && typeof aclApi === 'object' ? aclApi : null
    // Compact session list for phones. `sessionSummaryApi` is
    // { schema, list(cookieHeader, {includeSubagents}), detail(sessionId, cookieHeader) };
    // the proxy only routes, authenticates and serializes the response.
    this.sessionSummaryApi = sessionSummaryApi && typeof sessionSummaryApi === 'object' ? sessionSummaryApi : null
    // Desktop-side auto-approval rules. `autoApproveApi` is
    // { tokens(), status(), save(body) }; available on both listeners because the
    // phone reaches this desktop either directly or through the gateway.
    this.autoApproveApi = autoApproveApi && typeof autoApproveApi === 'object' ? autoApproveApi : null
    this.qrLibrary = null
    this.agent = new (this.upstreamProtocol === 'https:' ? https : http).Agent({
      keepAlive: true,
      maxSockets: 128,
      // Loopback is trusted: the desktop self-signs nothing (plain HTTP today),
      // but tolerate it if a future build serves TLS on loopback.
      rejectUnauthorized: false
    })
  }

  transport () {
    return this.upstreamProtocol === 'https:' ? https : http
  }

  /** Read the vendored QR library once; served only to loopback clients. */
  qrLibraryBytes () {
    if (this.qrLibrary !== null) return this.qrLibrary
    if (!this.assetsDir) return undefined
    try {
      this.qrLibrary = fs.readFileSync(path.join(this.assetsDir, 'qrcode.js'))
      return this.qrLibrary
    } catch {
      return undefined
    }
  }

  /**
   * Loopback-only pairing/config UI and their static assets.
   * Everything else (including these paths from a non-loopback socket) is
   * proxied/refused upstream, so the LAN never sees the pairing token or the
   * server configuration API.
   * @returns {Promise<boolean>} true when the response was handled here.
   */
  async handleLoopbackRoute (req, res) {
    const pathname = String(req.url ?? '/').split('?')[0]
    const loopbackOnly = pathname === CONFIG_PATH ||
      pathname === PAIR_PAGE_PATH ||
      pathname === PAIR_ROTATE_PATH ||
      pathname === SELF_CHECK_PATH ||
      pathname === PAIR_QR_ASSET_PATH ||
      pathname === PAIR_JS_ASSET_PATH
    if (!loopbackOnly) return false
    // The token/config live in these pages; never hand them to a non-loopback client.
    if (!isLoopbackAddress(req.socket?.remoteAddress)) {
      res.writeHead(404, {
        'content-type': 'text/plain; charset=utf-8',
        'cache-control': 'no-store',
        'x-content-type-options': 'nosniff'
      })
      res.end('not found\n')
      return true
    }
    if (pathname === CONFIG_PATH) {
      await this.handleConfigRequest(req, res)
      return true
    }
    if (pathname === SELF_CHECK_PATH) {
      if (req.method !== 'POST') {
        const body = JSON.stringify({ ok: false, error: 'method not allowed' })
        res.writeHead(405, {
          ...pairingPageHeaders('application/json; charset=utf-8'),
          allow: 'POST',
          'content-length': Buffer.byteLength(body)
        })
        res.end(body)
        return true
      }
      if (!this.probeProvider) {
        res.writeHead(501, pairingPageHeaders('application/json; charset=utf-8'))
        res.end(JSON.stringify({ ok: false, error: 'self-check is not available' }))
        return true
      }
      req.resume()
      Promise.resolve()
        .then(() => this.probeProvider())
        .then((probe) => {
          if (!probe || typeof probe !== 'object') {
            const body = JSON.stringify({ ok: false, status: 'unavailable', error: 'relay is not running; reload the plugin and retry' })
            res.writeHead(503, { ...pairingPageHeaders('application/json; charset=utf-8'), 'content-length': Buffer.byteLength(body) })
            res.end(body)
            return
          }
          const body = JSON.stringify({
            ok: probe.ok === true,
            status: probe.ok === true ? 'ok' : 'failed',
            checkedAt: probe.checkedAt ?? null,
            attempts: probe.attempts ?? null,
            appReady: probe.appReady ?? null,
            capabilities: probe.capabilities ?? null,
            missing: Array.isArray(probe.missing) ? probe.missing : [],
            errors: Array.isArray(probe.errors) ? probe.errors : []
          })
          res.writeHead(200, { ...pairingPageHeaders('application/json; charset=utf-8'), 'content-length': Buffer.byteLength(body) })
          res.end(body)
        })
        .catch((error) => {
          const body = JSON.stringify({ ok: false, status: 'failed', error: error && error.message ? error.message : String(error) })
          try {
            res.writeHead(500, { ...pairingPageHeaders('application/json; charset=utf-8'), 'content-length': Buffer.byteLength(body) })
            res.end(body)
          } catch {}
        })
      return true
    }
    if (pathname === PAIR_ROTATE_PATH) {
      if (req.method !== 'POST') {
        res.writeHead(405, { ...pairingPageHeaders('application/json; charset=utf-8'), allow: 'POST' })
        res.end(JSON.stringify({ ok: false, error: 'method not allowed' }))
        return true
      }
      if (!this.rotateProvider) {
        res.writeHead(501, pairingPageHeaders('application/json; charset=utf-8'))
        res.end(JSON.stringify({ ok: false, error: 'token rotation is not available (gateway disabled)' }))
        return true
      }
      // Drain the (ignored) request body, then rotate asynchronously.
      req.resume()
      Promise.resolve()
        .then(() => this.rotateProvider())
        .then((result) => {
          const body = JSON.stringify({ ok: true, tokenMasked: result?.tokenMasked ?? null, rotatedAt: result?.rotatedAt ?? null })
          res.writeHead(200, { ...pairingPageHeaders('application/json; charset=utf-8'), 'content-length': Buffer.byteLength(body) })
          res.end(body)
        })
        .catch((error) => {
          const body = JSON.stringify({ ok: false, error: error && error.message ? error.message : String(error) })
          try {
            const status = error && Number.isInteger(error.statusCode) ? error.statusCode : 500
            res.writeHead(status, { ...pairingPageHeaders('application/json; charset=utf-8'), 'content-length': Buffer.byteLength(body) })
            res.end(body)
          } catch {}
        })
      return true
    }
    if (req.method !== 'GET' && req.method !== 'HEAD') {
      res.writeHead(405, { ...pairingPageHeaders('text/plain; charset=utf-8'), allow: 'GET, HEAD' })
      res.end('method not allowed\n')
      return true
    }

    if (pathname === PAIR_QR_ASSET_PATH) {
      const bytes = this.qrLibraryBytes()
      if (!bytes) {
        res.writeHead(500, { ...pairingPageHeaders('text/plain; charset=utf-8') })
        res.end('qrcode.js is missing from the plugin assets\n')
        return true
      }
      res.writeHead(200, { ...pairingPageHeaders('application/javascript; charset=utf-8'), 'content-length': bytes.length })
      res.end(req.method === 'HEAD' ? undefined : bytes)
      return true
    }

    if (pathname === PAIR_JS_ASSET_PATH) {
      const body = Buffer.from(PAIR_JS_SOURCE, 'utf8')
      res.writeHead(200, { ...pairingPageHeaders('application/javascript; charset=utf-8'), 'content-length': body.length })
      res.end(req.method === 'HEAD' ? undefined : body)
      return true
    }

    const pairing = this.pairingProvider()
    if (!pairing || typeof pairing !== 'object') {
      const body = JSON.stringify({ ok: false, error: 'pairing-not-ready', message: 'relay 正在启动或自检中，请稍后刷新本页' })
      res.writeHead(503, {
        ...pairingPageHeaders('application/json; charset=utf-8'),
        'retry-after': '2',
        'content-length': Buffer.byteLength(body)
      })
      res.end(req.method === 'HEAD' ? undefined : body)
      return true
    }
    const config = this.configProvider ? this.configProvider() : null
    const body = Buffer.from(renderPairingPage(pairing, { config }), 'utf8')
    res.writeHead(200, { ...pairingPageHeaders('text/html; charset=utf-8'), 'content-length': body.length })
    res.end(req.method === 'HEAD' ? undefined : body)
    return true
  }

  /**
   * Loopback-only relay-server configuration API.
   *
   *   GET  /__relay/config  -> effective config + source + live gateway state
   *   POST /__relay/config  -> validate, persist (relay-config.json) and apply
   *                            live (connector restart), returns the new state
   *
   * Body: `{ gateway: { enabled, host, port, path?, secure?, fingerprint?, name? } }`
   * or the gateway object itself. Never exposed on the LAN interface.
   */
  async handleConfigRequest (req, res) {
    const jsonHeaders = pairingPageHeaders('application/json; charset=utf-8')
    const sendJson = (status, value, extra = {}) => {
      const body = JSON.stringify(value)
      try {
        res.writeHead(status, { ...jsonHeaders, ...extra, 'content-length': Buffer.byteLength(body) })
        res.end(req.method === 'HEAD' ? undefined : body)
      } catch {}
    }
    if (req.method === 'GET' || req.method === 'HEAD') {
      if (!this.configProvider) return sendJson(501, { ok: false, error: 'config api unavailable' })
      try {
        return sendJson(200, this.configProvider())
      } catch (error) {
        return sendJson(500, { ok: false, error: error && error.message ? error.message : String(error) })
      }
    }
    if (req.method !== 'POST') {
      return sendJson(405, { ok: false, error: 'method not allowed' }, { allow: 'GET, HEAD, POST' })
    }
    if (!this.configApplyProvider) return sendJson(501, { ok: false, error: 'config api is read-only' })
    // Require application/json: a cross-site page cannot set that header without
    // a CORS preflight (which this server never grants), so a malicious web page
    // cannot drive the loopback config API with a "simple" text/plain request.
    const contentType = String(req.headers['content-type'] ?? '').toLowerCase()
    if (!contentType.includes('application/json')) {
      return sendJson(415, { ok: false, error: 'content-type must be application/json' })
    }
    const limit = 64 * 1024
    const chunks = []
    let size = 0
    try {
      for await (const chunk of req) {
        size += chunk.length
        if (size > limit) {
          return sendJson(413, { ok: false, error: `request body too large (limit ${limit} bytes)` })
        }
        chunks.push(chunk)
      }
    } catch {
      return sendJson(400, { ok: false, error: 'cannot read request body' })
    }
    let payload
    try {
      const text = Buffer.concat(chunks).toString('utf8').trim()
      payload = text ? JSON.parse(text) : {}
    } catch (error) {
      return sendJson(400, { ok: false, error: `invalid JSON body: ${error && error.message ? error.message : String(error)}` })
    }
    if (!payload || typeof payload !== 'object' || Array.isArray(payload)) {
      return sendJson(400, { ok: false, error: 'body must be a JSON object' })
    }
    try {
      const state = await this.configApplyProvider(payload)
      return sendJson(200, state)
    } catch (error) {
      const status = error && Number.isInteger(error.statusCode)
        ? error.statusCode
        : (error && error.name === 'TypeError' ? 500 : 400)
      return sendJson(status, { ok: false, error: error && error.message ? error.message : String(error) })
    }
  }

  /**
   * APK download page and whitelisted APK files. Reachable from the LAN on
   * purpose (the phone downloads the release APK from here); it can only ever
   * serve files returned by the strict scan in downloads.mjs.
   */
  async handleDownloadRoute (req, res) {
    const pathname = String(req.url ?? '/').split('?')[0]
    if (pathname !== DOWNLOAD_PAGE_PATH && !pathname.startsWith(`${DOWNLOAD_PAGE_PATH}/`)) {
      return false
    }
    if (req.method !== 'GET' && req.method !== 'HEAD') {
      res.writeHead(405, { ...downloadPageHeaders('text/plain; charset=utf-8'), allow: 'GET, HEAD' })
      res.end('method not allowed\n')
      return true
    }
    if (!this.downloadDir) {
      res.writeHead(503, downloadPageHeaders('text/plain; charset=utf-8'))
      res.end('download directory is not configured\n')
      return true
    }

    if (pathname === DOWNLOAD_PAGE_PATH) {
      const files = await hashEntries(scanDownloads(this.downloadDir))
      const html = Buffer.from(renderDownloadPage({
        dir: this.downloadDir,
        files,
        relayVersion: this.health?.relayVersion,
        dshVersion: this.health?.dshVersion,
        generatedAt: new Date().toISOString()
      }), 'utf8')
      res.writeHead(200, { ...downloadPageHeaders('text/html; charset=utf-8'), 'content-length': html.length })
      res.end(req.method === 'HEAD' ? undefined : html)
      return true
    }

    const rawName = pathname.slice(DOWNLOAD_PAGE_PATH.length + 1)
    let requestedName
    try {
      requestedName = decodeURIComponent(rawName)
    } catch {
      requestedName = ''
    }
    const resolved = resolveDownload(this.downloadDir, requestedName)
    if (!resolved) {
      res.writeHead(404, downloadPageHeaders('text/plain; charset=utf-8'))
      res.end('not found\n')
      return true
    }

    let sha256 = 'unavailable'
    try {
      const [hashed] = await hashEntries([resolved.entry])
      sha256 = hashed.sha256
    } catch {
      // keep the file downloadable even if hashing failed
    }
    const encodedName = encodeURIComponent(resolved.entry.name)
    const headers = {
      'content-type': 'application/vnd.android.package-archive',
      'content-disposition': `attachment; filename="${resolved.entry.name}"; filename*=UTF-8''${encodedName}`,
      'content-length': resolved.entry.sizeBytes,
      'x-content-sha256': sha256,
      'cache-control': 'no-store',
      'referrer-policy': 'no-referrer',
      'x-content-type-options': 'nosniff'
    }
    res.writeHead(200, headers)
    if (req.method === 'HEAD') {
      res.end()
      return true
    }
    const stream = fs.createReadStream(resolved.file)
    stream.on('error', () => {
      try { res.destroy() } catch {}
    })
    res.on('close', () => {
      try { stream.destroy() } catch {}
    })
    stream.pipe(res)
    return true
  }

  /**
   * Authenticate a relay-owned phone API (ACL guard, session summary,
   * auto-approval rules).
   *
   * A bearer credential equal to the current pairing token always works. The
   * loopback management page is additionally trusted, but ONLY on the public
   * helper listener: the gateway's internal plain proxy is loopback too, so
   * trusting the address alone would let any public caller through.
   * @param {import('node:http').IncomingMessage} req
   * @returns {boolean}
   */
  pairingAuthorized (req) {
    let expected
    try {
      const candidates = []
      for (const api of [this.autoApproveApi, this.aclApi]) {
        const values = api?.tokens?.() ?? api?.token?.()
        if (Array.isArray(values)) candidates.push(...values)
        else candidates.push(values)
      }
      expected = candidates
    } catch {
      expected = undefined
    }
    const candidates = (Array.isArray(expected) ? expected : [expected])
      .filter((value) => typeof value === 'string' && value.length > 0)
    if (candidates.length > 0) {
      const header = String(req.headers?.authorization ?? '')
      const match = /^Bearer\s+(.+)$/i.exec(header)
      if (match) {
        const provided = Buffer.from(match[1].trim())
        for (const candidate of candidates) {
          const wanted = Buffer.from(candidate)
          if (provided.length === wanted.length && timingSafeEqual(provided, wanted)) return true
        }
      }
    }
    return this.helpers === 'all' && isLoopbackAddress(req.socket?.remoteAddress)
  }

  /**
   * Phone-facing auto-approval rules.
   *
   *   GET  /__relay/auto-approve -> current rules, revision, stats, recent decisions
   *   PUT  /__relay/auto-approve -> replace the rules (validated + persisted)
   *   POST /__relay/auto-approve -> same as PUT (kept for simple clients)
   *
   * Authentication mirrors the ACL API. Loopback is trusted only on the public
   * helper listener, never on the gateway's internal plain proxy.
   * @param {import('node:http').IncomingMessage} req
   * @param {import('node:http').ServerResponse} res
   * @returns {Promise<boolean>} true when the response was handled here.
   */
  async handleAutoApproveRoute (req, res) {
    if (!this.autoApproveApi) return false
    const pathname = String(req.url ?? '/').split('?')[0]
    if (pathname !== AUTO_APPROVE_PATH) return false
    const send = (status, payload) => {
      const body = JSON.stringify(payload)
      try {
        res.writeHead(status, {
          'content-type': 'application/json; charset=utf-8',
          'cache-control': 'no-store',
          'x-content-type-options': 'nosniff',
          'content-length': Buffer.byteLength(body)
        })
        res.end(req.method === 'HEAD' ? undefined : body)
      } catch {}
    }
    if (!this.pairingAuthorized(req)) {
      send(401, { ok: false, error: 'unauthorized' })
      return true
    }
    if (req.method === 'GET' || req.method === 'HEAD') {
      try {
        send(200, { ok: true, relayVersion: this.health?.relayVersion, ...this.autoApproveApi.status() })
      } catch (error) {
        send(500, { ok: false, error: error && error.message ? error.message : String(error) })
      }
      return true
    }
    if (req.method !== 'PUT' && req.method !== 'POST') {
      send(405, { ok: false, error: 'method not allowed' })
      return true
    }
    let body
    try {
      body = await readJsonBody(req, 256 * 1024)
    } catch (error) {
      send(400, { ok: false, error: error && error.message ? error.message : String(error) })
      return true
    }
    try {
      const result = this.autoApproveApi.save(body && typeof body === 'object' ? body : {})
      send(200, { ok: true, relayVersion: this.health?.relayVersion, ...result })
    } catch (error) {
      const invalid = error && error.code === 'invalid-auto-approve'
      send(invalid ? 400 : 500, { ok: false, error: error && error.message ? error.message : String(error) })
    }
    return true
  }

  /**
   * Relay-owned Windows ACL guard routes.
   *   GET  /__relay/acl/status -> guard status + known roots (no probe)
   *   POST /__relay/acl/check  -> inspect/repair known roots; body { repair?: boolean, root?: string }
   * @param {import('node:http').IncomingMessage} req
   * @param {import('node:http').ServerResponse} res
   * @returns {Promise<boolean>} true when the response was handled here.
   */
  async handleAclRoute (req, res) {
    if (!this.aclApi) return false
    const pathname = String(req.url ?? '/').split('?')[0]
    if (pathname !== ACL_STATUS_PATH && pathname !== ACL_CHECK_PATH) return false
    const send = (status, payload) => {
      const body = JSON.stringify(payload)
      res.writeHead(status, {
        'content-type': 'application/json; charset=utf-8',
        'cache-control': 'no-store',
        'x-content-type-options': 'nosniff',
        'content-length': Buffer.byteLength(body)
      })
      res.end(body)
    }
    if (!this.pairingAuthorized(req)) {
      send(401, { ok: false, error: 'unauthorized' })
      return true
    }
    if (pathname === ACL_STATUS_PATH) {
      if (req.method !== 'GET' && req.method !== 'HEAD') {
        send(405, { ok: false, error: 'method not allowed' })
        return true
      }
      try {
        const status = this.aclApi.status()
        send(200, { ok: true, ...status, generatedAt: new Date().toISOString() })
      } catch (error) {
        send(500, { ok: false, error: error && error.message ? error.message : String(error) })
      }
      return true
    }
    if (req.method !== 'POST') {
      send(405, { ok: false, error: 'method not allowed' })
      return true
    }
    let body
    try {
      body = await readJsonBody(req, 64 * 1024)
    } catch (error) {
      send(400, { ok: false, error: error && error.message ? error.message : String(error) })
      return true
    }
    try {
      const result = await this.aclApi.check(body && typeof body === 'object' ? body : {})
      send(200, { ok: true, ...result, generatedAt: new Date().toISOString() })
    } catch (error) {
      send(500, { ok: false, error: error && error.message ? error.message : String(error) })
    }
    return true
  }

  /**
   * Phone-facing compact session list (relay v0.6.0+).
   *
   *   GET /__relay/sessions/summary[?sessionId=<id>][&includeSubagents=1]
   *
   * Authentication mirrors the ACL API: a Bearer pairing credential (remote or
   * launch token) always works; the loopback address is trusted only on the
   * public helper listener, never on the gateway's internal plain proxy.
   * The upstream `session/list` call reuses the caller's own dsh-auth cookie, so
   * the relay never holds a phone credential itself.
   *
   * @param {import('node:http').IncomingMessage} req
   * @param {import('node:http').ServerResponse} res
   * @returns {Promise<boolean>} true when the response was handled here.
   */
  async handleSessionSummaryRoute (req, res) {
    if (!this.sessionSummaryApi) return false
    let url
    try {
      url = new URL(String(req.url ?? '/'), 'http://127.0.0.1')
    } catch {
      return false
    }
    if (url.pathname !== SESSION_SUMMARY_PATH) return false
    const send = (status, payload, extra = {}) => {
      const body = JSON.stringify(payload)
      try {
        res.writeHead(status, {
          'content-type': 'application/json; charset=utf-8',
          'cache-control': 'no-store',
          'x-content-type-options': 'nosniff',
          'content-length': Buffer.byteLength(body),
          ...extra
        })
        res.end(req.method === 'HEAD' ? undefined : body)
      } catch {}
    }
    if (!this.pairingAuthorized(req)) {
      send(401, { ok: false, error: 'unauthorized' })
      return true
    }
    if (req.method !== 'GET' && req.method !== 'HEAD') {
      send(405, { ok: false, error: 'method not allowed' }, { allow: 'GET, HEAD' })
      return true
    }
    // Never log the cookie (or anything derived from it).
    const cookieHeader = typeof req.headers?.cookie === 'string' ? req.headers.cookie : ''
    const includeSubagents = isTruthyQueryFlag(url.searchParams.get('includeSubagents'))
    const sessionId = url.searchParams.get('sessionId')
    try {
      if (sessionId !== null && sessionId !== '') {
        const found = await this.sessionSummaryApi.detail(sessionId, cookieHeader)
        if (!found || !found.item) {
          send(404, { ok: false, error: 'not-found' })
          return true
        }
        send(200, {
          ok: true,
          schema: found.schema ?? this.sessionSummaryApi.schema ?? 1,
          generatedAt: found.generatedAt ?? new Date().toISOString(),
          item: found.item
        })
        return true
      }
      const payload = await this.sessionSummaryApi.list(cookieHeader, { includeSubagents })
      send(200, payload)
    } catch {
      // Upstream failures (non-200, ok:false, timeout, bad JSON) are one opaque
      // 502: the phone only needs to know the desktop side failed.
      send(502, { ok: false, error: 'upstream-failed' })
    }
    return true
  }

  /** HTTP(S) request/response proxy. Bodies are streamed, never buffered. */
  async handleRequest (req, res) {
    const pathname = String(req.url ?? '/').split('?')[0]

    // Small out-of-band status channel: lets install/smoke scripts verify the
    // TLS hop before they have a launch token. Contains no secrets.
    if (this.helpers !== 'none' && req.method === 'GET' && pathname === '/__relay/health' && this.health) {
      let download
      if (this.downloadProvider) {
        try { download = await this.downloadProvider() } catch { download = undefined }
      }
      const body = JSON.stringify({ ok: true, ...this.health, ...(download ? { download } : {}) })
      res.writeHead(200, {
        'content-type': 'application/json; charset=utf-8',
        'cache-control': 'no-store',
        'content-length': Buffer.byteLength(body)
      })
      res.end(body)
      return
    }

    if (await this.handleAutoApproveRoute(req, res)) return
    if (await this.handleAclRoute(req, res)) return
    if (await this.handleSessionSummaryRoute(req, res)) return
    if (this.helpers === 'all' && await this.handleDownloadRoute(req, res)) return
    if (this.helpers === 'all' && await this.handleLoopbackRoute(req, res)) return

    const upstreamReq = this.transport().request({
      host: '127.0.0.1',
      port: this.upstreamPort,
      method: req.method,
      path: req.url,
      headers: rewriteRequestHeaders(req.headers, this.upstreamPort, this.upstreamProtocol),
      agent: this.agent,
      rejectUnauthorized: false
    })

    upstreamReq.on('response', (upstreamRes) => {
      if (res.headersSent) {
        upstreamRes.destroy()
        return
      }
      const headers = {}
      for (const [name, value] of Object.entries(upstreamRes.headers)) {
        if (value === undefined) continue
        if (HOP_BY_HOP.has(name.toLowerCase())) continue
        headers[name] = value
      }
      const status = upstreamRes.statusCode ?? 502
      try {
        if (upstreamRes.statusMessage) res.writeHead(status, upstreamRes.statusMessage, headers)
        else res.writeHead(status, headers)
      } catch {
        upstreamRes.destroy()
        res.destroy()
        return
      }
      upstreamRes.on('error', () => res.destroy())
      res.on('close', () => {
        if (!upstreamRes.complete) upstreamRes.destroy()
      })
      upstreamRes.pipe(res)
    })

    upstreamReq.on('error', (error) => {
      if (res.headersSent) {
        res.destroy()
        return
      }
      const body = `dsh-remote-relay: upstream unavailable: ${error && error.message ? error.message : String(error)}\n`
      res.writeHead(502, {
        'content-type': 'text/plain; charset=utf-8',
        'cache-control': 'no-store',
        'content-length': Buffer.byteLength(body)
      })
      res.end(body)
    })

    res.on('close', () => {
      if (!res.writableEnded) upstreamReq.destroy()
    })
    req.pipe(upstreamReq)
  }

  /** WebSocket/upgrade proxy: relay the 101 handshake byte-for-byte, then pipe. */
  handleUpgrade (req, clientSocket, clientHead) {
    clientSocket.on('error', () => {})
    const headers = rewriteRequestHeaders(req.headers, this.upstreamPort, this.upstreamProtocol)
    headers.connection = 'Upgrade'
    headers.upgrade = req.headers.upgrade ?? 'websocket'

    const upstreamReq = this.transport().request({
      host: '127.0.0.1',
      port: this.upstreamPort,
      method: req.method,
      path: req.url,
      headers,
      agent: false,
      rejectUnauthorized: false
    })

    upstreamReq.on('upgrade', (upstreamRes, upstreamSocket, upstreamHead) => {
      upstreamSocket.on('error', () => {})
      const lines = [`HTTP/1.1 ${upstreamRes.statusCode ?? 101} ${upstreamRes.statusMessage ?? 'Switching Protocols'}`.trimEnd()]
      for (const [name, value] of Object.entries(upstreamRes.headers)) {
        if (value === undefined) continue
        if (Array.isArray(value)) for (const item of value) lines.push(`${name}: ${item}`)
        else lines.push(`${name}: ${value}`)
      }
      try {
        clientSocket.write(`${lines.join('\r\n')}\r\n\r\n`)
      } catch {
        upstreamSocket.destroy()
        clientSocket.destroy()
        return
      }
      if (upstreamHead && upstreamHead.length > 0) clientSocket.write(upstreamHead)
      if (clientHead && clientHead.length > 0) upstreamSocket.write(clientHead)
      upstreamSocket.pipe(clientSocket)
      clientSocket.pipe(upstreamSocket)
      const teardown = () => {
        clientSocket.destroy()
        upstreamSocket.destroy()
      }
      upstreamSocket.on('close', teardown)
      clientSocket.on('close', teardown)
    })

    // Upstream answered the upgrade with a normal HTTP response (401/403/404):
    // forward it verbatim and close; there is no tunnel to pipe.
    upstreamReq.on('response', (upstreamRes) => {
      const lines = [`HTTP/1.1 ${upstreamRes.statusCode ?? 502} ${upstreamRes.statusMessage ?? ''}`.trimEnd()]
      for (const [name, value] of Object.entries(upstreamRes.headers)) {
        if (value === undefined) continue
        if (Array.isArray(value)) for (const item of value) lines.push(`${name}: ${item}`)
        else lines.push(`${name}: ${value}`)
      }
      try {
        clientSocket.write(`${lines.join('\r\n')}\r\n\r\n`)
      } catch {}
      upstreamRes.pipe(clientSocket)
      upstreamRes.on('error', () => clientSocket.destroy())
      upstreamRes.on('end', () => clientSocket.end())
    })

    upstreamReq.on('error', () => {
      try {
        clientSocket.write('HTTP/1.1 502 Bad Gateway\r\nconnection: close\r\ncontent-length: 0\r\n\r\n')
      } catch {}
      clientSocket.destroy()
    })

    clientSocket.on('close', () => upstreamReq.destroy())
    upstreamReq.end()
  }

  close () {
    try { this.agent.destroy() } catch {}
  }
}

/**
 * Start all listeners for the relay.
 *
 * Binds `host:port` (the phone-reachable address) and, unless that is already
 * loopback, `127.0.0.1:port` for local E2E. Never binds a wildcard address.
 *
 * @returns {Promise<{ host: string, port: number, loopbackPort: number, close: () => Promise<void> }>}
 */
export async function startRelay ({
  host,
  port,
  certFile,
  keyFile,
  upstreamPort,
  upstreamProtocol = 'http:',
  health,
  pairingProvider,
  assetsDir,
  downloadDir,
  downloadProvider,
  rotateProvider,
  probeProvider,
  configProvider,
  configApplyProvider,
  configFilePath,
  aclApi,
  sessionSummaryApi,
  autoApproveApi,
  log = () => {}
}) {
  const bindHost = assertSafeBindHost(host)
  const listenPort = Number(port)
  if (!Number.isInteger(listenPort) || listenPort < 1 || listenPort > 65535) {
    throw new Error(`dsh-remote-relay: invalid port ${JSON.stringify(port)}`)
  }
  let key
  let cert
  try {
    key = fs.readFileSync(keyFile)
    cert = fs.readFileSync(certFile)
  } catch (error) {
    throw new Error(`dsh-remote-relay: cannot read TLS material (certFile=${certFile}, keyFile=${keyFile}): ${error && error.message ? error.message : String(error)}`)
  }

  const proxy = new UpstreamProxy({
    upstreamPort,
    upstreamProtocol,
    health,
    pairingProvider,
    assetsDir,
    downloadDir,
    downloadProvider,
    rotateProvider,
    probeProvider,
    configProvider,
    configApplyProvider,
    configFilePath,
    aclApi,
    sessionSummaryApi,
    autoApproveApi
  })
  const tlsOptions = {
    key,
    cert,
    minVersion: 'TLSv1.2',
    // Node 22+ keeps client renegotiation off by default; explicit for clarity.
    honorCipherOrder: true
  }

  const servers = []
  const trackedSockets = new Set()
  const hosts = isLoopbackHost(bindHost) ? [bindHost] : [bindHost, '127.0.0.1']

  try {
    for (const listenHost of hosts) {
      const server = https.createServer(tlsOptions, (req, res) => {
        // handleRequest is async (download hashing); never leave a rejection
        // unhandled or a client hanging on a half-open response.
        proxy.handleRequest(req, res).catch((error) => {
          log(`[remote-relay] request handler error: ${error && error.message ? error.message : String(error)}`)
          try {
            if (!res.headersSent) {
              res.writeHead(500, { 'content-type': 'text/plain; charset=utf-8', 'cache-control': 'no-store' })
            }
            res.end('relay internal error\n')
          } catch {}
        })
      })
      server.on('upgrade', (req, socket, head) => proxy.handleUpgrade(req, socket, head))
      server.on('connection', (socket) => {
        trackedSockets.add(socket)
        socket.on('close', () => trackedSockets.delete(socket))
      })
      server.on('tlsClientError', (error) => {
        log(`[remote-relay] TLS client error from ${listenHost}: ${error && error.message ? error.message : String(error)}`)
      })
      await listenWithRetry(server, listenPort, listenHost)
      servers.push({ server, host: listenHost })
      log(`[remote-relay] TLS listening on https://${listenHost}:${listenPort}`)
    }
  } catch (error) {
    for (const { server } of servers) {
      try { server.close() } catch {}
    }
    proxy.close()
    throw error
  }

  return {
    host: bindHost,
    port: listenPort,
    loopbackPort: listenPort,
    listenHosts: servers.map((entry) => entry.host),
    async close () {
      proxy.close()
      await Promise.all(servers.map(({ server }) => new Promise((resolve) => {
        try { server.closeAllConnections?.() } catch {}
        try { server.closeIdleConnections?.() } catch {}
        for (const socket of trackedSockets) {
          try { socket.destroy() } catch {}
        }
        try {
          server.close(() => resolve())
        } catch {
          resolve()
        }
      })))
      trackedSockets.clear()
    }
  }
}

async function listenWithRetry (server, port, host, attempts = 8) {
  let lastError
  for (let attempt = 1; attempt <= attempts; attempt++) {
    try {
      await new Promise((resolve, reject) => {
        const onError = (error) => {
          server.removeListener('listening', onListening)
          reject(error)
        }
        const onListening = () => {
          server.removeListener('error', onError)
          resolve()
        }
        server.once('error', onError)
        server.once('listening', onListening)
        server.listen({ host, port, exclusive: true })
      })
      return
    } catch (error) {
      lastError = error
      // HMR dispose/reload can race the previous listener's close; retry briefly.
      if (error && error.code === 'EADDRINUSE' && attempt < attempts) {
        await delay(250 * attempt)
        continue
      }
      throw new Error(`dsh-remote-relay: cannot listen on ${host}:${port}: ${error && error.code ? error.code : ''} ${error && error.message ? error.message : String(error)}`)
    }
  }
  throw lastError
}

function delay (ms) {
  return new Promise((resolve) => setTimeout(resolve, ms))
}

/**
 * Loopback-only plain HTTP/WS reverse proxy used by the gateway connector.
 *
 * It is never exposed to the LAN: the gateway tunnels requests into it and the
 * connector forwards them here, so the same Host/Origin normalization as the
 * public TLS listener applies. Helper pages (/__relay/*) are disabled.
 *
 * @returns {Promise<{ host: string, port: number, close: () => Promise<void> }>}
 */
export async function startPlainProxy ({
  upstreamPort,
  upstreamProtocol = 'http:',
  host = '127.0.0.1',
  port = 0,
  health,
  aclApi,
  sessionSummaryApi,
  autoApproveApi,
  log = () => {}
}) {
  // The gateway connector's internal hop may serve only the secret-free health
  // endpoint, the token-authenticated ACL API, compact session list and
  // auto-approval rules; pairing/download pages stay exclusively on the TLS
  // listener.
  const proxy = new UpstreamProxy({ upstreamPort, upstreamProtocol, health, aclApi, sessionSummaryApi, autoApproveApi, helpers: 'health' })
  const server = http.createServer((req, res) => {
    proxy.handleRequest(req, res).catch((error) => {
      log(`[remote-relay] internal plain proxy error: ${error && error.message ? error.message : String(error)}`)
      try {
        if (!res.headersSent) res.writeHead(500, { 'content-type': 'text/plain; charset=utf-8' })
        res.end('internal relay error\n')
      } catch {}
    })
  })
  server.on('upgrade', (req, socket, head) => proxy.handleUpgrade(req, socket, head))
  server.on('tlsClientError', () => {})
  const tracked = new Set()
  server.on('connection', (socket) => {
    tracked.add(socket)
    socket.on('close', () => tracked.delete(socket))
  })
  await listenWithRetry(server, port, host)
  const address = server.address()
  const actualPort = typeof address === 'object' && address ? address.port : port
  return {
    host,
    port: actualPort,
    async close () {
      proxy.close()
      await new Promise((resolve) => {
        try { server.closeAllConnections?.() } catch {}
        for (const socket of tracked) {
          try { socket.destroy() } catch {}
        }
        try { server.close(() => resolve()) } catch { resolve() }
      })
      tracked.clear()
    }
  }
}
