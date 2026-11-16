// Persistent relay configuration: where the public relay server (gateway) is.
//
// Why a file instead of only the Cordis patch:
//   The desktop profile patch (`~/.dsh/profiles/desktop/cordis.patch.yml`) is a
//   DSH-managed YAML layer: every install/upgrade script rewrites it, it needs a
//   reload tick to apply, and users must know YAML to change the server address.
//   The relay therefore owns a small JSON document next to pairing.json /
//   identity.json:
//
//     ~/.dsh/remote/relay-config.json
//
//   It is written atomically with mode 0600, is the authoritative source once it
//   exists, and can be changed three ways (all documented in
//   docs/relay-config.md):
//     1. the loopback management page https://127.0.0.1:8443/__relay/pair
//        (form: IP/domain, port, optional API route endpoint)
//     2. `POST /__relay/config` (same-origin JSON API, loopback only)
//     3. `pwsh -File scripts/relay-config.ps1` (CLI wrapper for the API)
//
// The legacy Cordis config (`- id: remote-relay` / `config.gateway`) stays
// supported as a fallback so an existing install keeps working; a one-time
// migration seeds this file from it when the file does not exist yet.
import crypto from 'node:crypto'
import fs from 'node:fs'
import net from 'node:net'
import os from 'node:os'
import path from 'node:path'

export const CONFIG_VERSION = 1
export const DEFAULT_GATEWAY_PORT = 9443
/** Default gateway tunnel endpoint (must match the vps/ gateway route). */
export const DEFAULT_GATEWAY_PATH = '/__gateway/tunnel'
export const DEFAULT_GATEWAY_NAME = 'DSH Remote Gateway'
export const MAX_HOST_LENGTH = 253

const HOSTNAME_RE = /^(?=.{1,253}$)[a-z0-9](?:[a-z0-9-]{0,61}[a-z0-9])?(?:\.[a-z0-9](?:[a-z0-9-]{0,61}[a-z0-9])?)*$/i
const PATH_RE = /^\/[A-Za-z0-9._~%:@!$&'()*+,;=\/-]*$/
const FINGERPRINT_RE = /^[0-9a-f]{64}$/

/**
 * Absolute path of the persistent configuration document.
 * Precedence: explicit override > $DSH_REMOTE_CONFIG > $DSH_HOME/remote/... >
 * ~/.dsh/remote/... (the location the rest of the remote tooling uses).
 */
export function relayConfigFilePath (override) {
  if (override) return path.resolve(String(override))
  if (process.env.DSH_REMOTE_CONFIG) return path.resolve(process.env.DSH_REMOTE_CONFIG)
  const dshHome = process.env.DSH_HOME
    ? String(process.env.DSH_HOME)
    : path.join(os.homedir(), '.dsh')
  return path.join(dshHome, 'remote', 'relay-config.json')
}

/**
 * Validate a user supplied server address. Accepts an IPv4/IPv6 literal, a DNS
 * name, `[::1]`, or a pasted URL (only its hostname is kept).
 */
export function normalizeHost (raw) {
  let text = String(raw ?? '').trim()
  if (!text) throw new Error('gateway.host is required (IP address or hostname of the relay server)')
  if (text.startsWith('[') && text.endsWith(']')) text = text.slice(1, -1)
  if (/^[a-z][a-z0-9+.-]*:\/\//i.test(text)) {
    let parsed
    try {
      parsed = new URL(text)
    } catch {
      throw new Error(`invalid gateway.host ${JSON.stringify(raw)}: not a valid URL`)
    }
    text = parsed.hostname
  }
  // A bare "host:port" paste keeps only the host.
  const bareHostPort = /^([^:/[\]]+):(\d{1,5})$/.exec(text)
  if (bareHostPort) text = bareHostPort[1]
  if (net.isIP(text)) {
    return net.isIP(text) === 6 ? text.toLowerCase() : text
  }
  const host = text.replace(/\.$/, '').toLowerCase()
  if (host.length > MAX_HOST_LENGTH || !HOSTNAME_RE.test(host)) {
    throw new Error(`invalid gateway.host ${JSON.stringify(raw)}: expected an IP address or DNS name`)
  }
  return host
}

export function normalizePort (raw, fallback = DEFAULT_GATEWAY_PORT) {
  if (raw === undefined || raw === null || raw === '') return fallback
  const port = Number(raw)
  if (!Number.isInteger(port) || port < 1 || port > 65535) {
    throw new Error(`invalid gateway.port ${JSON.stringify(raw)}: expected an integer 1-65535`)
  }
  return port
}

/**
 * Optional API route endpoint. This is the path the connector registers the
 * tunnel on; it may include a reverse-proxy prefix (e.g. `/dsh/__gateway/tunnel`).
 */
export function normalizeGatewayPath (raw) {
  let text = String(raw ?? '').trim()
  if (!text) return DEFAULT_GATEWAY_PATH
  if (/[\s?#]/.test(text)) {
    throw new Error(`invalid gateway.path ${JSON.stringify(raw)}: must be a path without spaces, "?" or "#"`)
  }
  if (!text.startsWith('/')) text = `/${text}`
  const collapsed = text.replace(/\/{2,}/g, '/').replace(/\/+$/, '')
  if (!collapsed) return DEFAULT_GATEWAY_PATH
  if (!PATH_RE.test(collapsed)) {
    throw new Error(`invalid gateway.path ${JSON.stringify(raw)}: unsupported characters`)
  }
  return collapsed
}

export function normalizeFingerprint (raw) {
  if (raw === undefined || raw === null || raw === '') return null
  const compact = String(raw).trim().toLowerCase().replace(/[:\s-]/g, '')
  if (!FINGERPRINT_RE.test(compact)) {
    throw new Error(`invalid gateway.fingerprint ${JSON.stringify(raw)}: expected a SHA-256 hex digest`)
  }
  return compact
}

/** Build `wss://host:port/path` (ws:// only when secure is explicitly false). */
export function gatewayUrlFor ({ host, port, path: routePath, secure = true } = {}) {
  const normalizedHost = normalizeHost(host)
  const normalizedPort = normalizePort(port)
  const normalizedPath = normalizeGatewayPath(routePath)
  const scheme = secure === false ? 'ws' : 'wss'
  const hostPart = net.isIP(normalizedHost) === 6 ? `[${normalizedHost}]` : normalizedHost
  return `${scheme}://${hostPart}:${normalizedPort}${normalizedPath}`
}

/**
 * Path of the gateway identity endpoint (`GET .../__gateway/identity`) derived
 * from the configured tunnel endpoint: the final `/tunnel` segment becomes
 * `/identity`; otherwise `/identity` is appended.
 */
export function gatewayIdentityPath (tunnelPath) {
  const normalized = normalizeGatewayPath(tunnelPath)
  if (normalized.endsWith('/tunnel')) {
    const base = normalized.slice(0, -'/tunnel'.length)
    return base.length > 0 ? `${base}/identity` : '/identity'
  }
  return `${normalized}/identity`
}

/**
 * Normalize one gateway settings object (persisted file or legacy Cordis
 * config). Throws on invalid values so a bad save never lands on disk.
 *
 * @param {object} raw
 * @param {{fallbackEnabled?: boolean, requireHostWhenEnabled?: boolean}} options
 * @returns {{enabled: boolean, host: string, port: number, path: string, secure: boolean,
 *            fingerprint: string|null, name: string, secret?: string}}
 */
export function normalizeGatewaySettings (raw, options = {}) {
  const value = raw && typeof raw === 'object' && !Array.isArray(raw) ? raw : {}
  const enabled = value.enabled === undefined
    ? options.fallbackEnabled === true
    : value.enabled === true
  const hostMissing = value.host === undefined || value.host === null || String(value.host).trim() === ''
  const host = hostMissing ? '' : normalizeHost(value.host)
  if (enabled && !host && options.requireHostWhenEnabled !== false) {
    throw new Error('gateway.enabled=true requires gateway.host (the relay server IP or hostname)')
  }
  const settings = {
    enabled,
    host,
    port: normalizePort(value.port),
    path: normalizeGatewayPath(value.path),
    secure: value.secure === undefined ? true : value.secure === true,
    fingerprint: normalizeFingerprint(value.fingerprint),
    name: value.name ? String(value.name).trim().slice(0, 64) || DEFAULT_GATEWAY_NAME : DEFAULT_GATEWAY_NAME
  }
  if (value.secret) settings.secret = String(value.secret)
  return settings
}

/** Placeholder document written for a fresh install (gateway disabled). */
export function emptyRelayConfig () {
  return {
    version: CONFIG_VERSION,
    updatedAt: new Date().toISOString(),
    gateway: {
      enabled: false,
      host: '',
      port: DEFAULT_GATEWAY_PORT,
      path: DEFAULT_GATEWAY_PATH,
      secure: true,
      fingerprint: null,
      name: DEFAULT_GATEWAY_NAME
    }
  }
}

/**
 * Read and validate the persistent configuration.
 * Never throws: a missing file is `{exists:false}`; a corrupt/invalid file is
 * `{exists:true, error}` so the caller can keep running on the legacy config
 * and surface the problem in the management page.
 *
 * @returns {{file: string, exists: boolean, gateway?: object, raw?: object,
 *            updatedAt?: string, initializedAt?: string, initializedBy?: string,
 *            error?: string}}
 */
export function readRelayConfig (file) {
  const target = relayConfigFilePath(file)
  let text
  try {
    text = fs.readFileSync(target, 'utf8')
  } catch (error) {
    if (error && error.code === 'ENOENT') return { file: target, exists: false }
    return { file: target, exists: false, error: `cannot read ${target}: ${error && error.message ? error.message : String(error)}` }
  }
  let raw
  try {
    raw = JSON.parse(text)
  } catch (error) {
    return { file: target, exists: true, error: `invalid JSON in ${target}: ${error && error.message ? error.message : String(error)}` }
  }
  if (!raw || typeof raw !== 'object' || Array.isArray(raw)) {
    return { file: target, exists: true, error: `invalid config document in ${target}: expected a JSON object` }
  }
  let gateway
  try {
    gateway = normalizeGatewaySettings(raw.gateway, { fallbackEnabled: false, requireHostWhenEnabled: true })
  } catch (error) {
    return { file: target, exists: true, raw, error: `invalid config.gateway in ${target}: ${error && error.message ? error.message : String(error)}` }
  }
  return {
    file: target,
    exists: true,
    raw,
    gateway,
    version: raw.version,
    updatedAt: typeof raw.updatedAt === 'string' ? raw.updatedAt : undefined,
    initializedAt: typeof raw.initializedAt === 'string' ? raw.initializedAt : undefined,
    initializedBy: typeof raw.initializedBy === 'string' ? raw.initializedBy : undefined
  }
}

/**
 * Persist gateway settings atomically (0600, tmp file + rename).
 *
 * @param {string|undefined} file
 * @param {object} input `{ gateway, initializedAt?, initializedBy? }`
 * @returns {{file: string, document: object, gateway: object}}
 */
export function writeRelayConfigFile (file, input = {}) {
  const target = relayConfigFilePath(file)
  const previous = readRelayConfig(target)
  const gateway = normalizeGatewaySettings(input.gateway ?? previous.gateway ?? {}, {
    fallbackEnabled: false,
    requireHostWhenEnabled: true
  })
  const now = new Date().toISOString()
  const document = {
    version: CONFIG_VERSION,
    updatedAt: now,
    gateway: { ...gateway }
  }
  const initializedAt = input.initializedAt ?? previous.initializedAt
  const initializedBy = input.initializedBy ?? previous.initializedBy
  if (initializedAt) document.initializedAt = initializedAt
  if (initializedBy) document.initializedBy = initializedBy

  const dir = path.dirname(target)
  fs.mkdirSync(dir, { recursive: true })
  const tmp = path.join(dir, `.relay-config.json.tmp-${process.pid}-${crypto.randomBytes(4).toString('hex')}`)
  fs.writeFileSync(tmp, `${JSON.stringify(document, null, 2)}\n`, { encoding: 'utf8', mode: 0o600 })
  try {
    fs.renameSync(tmp, target)
  } catch (error) {
    try { fs.unlinkSync(tmp) } catch {}
    throw error
  }
  return { file: target, document, gateway }
}

/** Public (page/API) projection: no secrets beyond an optional shared secret. */
export function gatewaySummary (gateway) {
  if (!gateway) return { enabled: false }
  const summary = {
    enabled: gateway.enabled === true,
    host: gateway.host || '',
    port: gateway.port,
    path: gateway.path,
    secure: gateway.secure !== false,
    fingerprint: gateway.fingerprint ?? null,
    name: gateway.name ?? DEFAULT_GATEWAY_NAME
  }
  if (gateway.host) summary.url = gatewayUrlFor(gateway)
  if (gateway.secret) summary.secretConfigured = true
  return summary
}
