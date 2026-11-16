// Environment/CLI-less configuration for the DSH Remote public gateway.
//
// Everything is set through the systemd EnvironmentFile
// (/etc/dsh-remote/gateway.env); see scripts/deploy-gateway.ps1. All values have
// production-safe defaults so a fresh checkout can be smoke-tested locally by
// overriding CERT_FILE/KEY_FILE/PORT/APK_DIR.
import fs from 'node:fs'
import os from 'node:os'
import path from 'node:path'

const DEFAULT_PORT = 9443
const DEFAULT_REMOTE_ROOT = '/opt/dsh-remote'

function envString (name, fallback) {
  const raw = process.env[name]
  if (raw === undefined || raw === null) return fallback
  const value = String(raw).trim()
  return value === '' ? fallback : value
}

function envInt (name, fallback) {
  const raw = envString(name, undefined)
  if (raw === undefined) return fallback
  const value = Number.parseInt(raw, 10)
  if (!Number.isFinite(value)) throw new Error(`invalid ${name}: ${JSON.stringify(raw)} is not an integer`)
  return value
}

function envBool (name, fallback) {
  const raw = envString(name, undefined)
  if (raw === undefined) return fallback
  return !['0', 'false', 'no', 'off'].includes(raw.toLowerCase())
}

/** First non-loopback IPv4 address; only used when GATEWAY_PUBLIC_HOST is unset. */
function detectPublicHost () {
  for (const infos of Object.values(os.networkInterfaces())) {
    for (const info of infos ?? []) {
      if (info.family === 'IPv4' && !info.internal && !info.address.startsWith('169.254.')) return info.address
    }
  }
  return '127.0.0.1'
}

function readTlsFile (file, label) {
  try {
    return fs.readFileSync(file)
  } catch (error) {
    throw new Error(
      `dsh-remote-gateway: cannot read ${label} ${file}: ${error && error.code ? error.code : ''} ` +
      `${error && error.message ? error.message : String(error)} (run scripts/deploy-gateway.ps1 to generate it)`
    )
  }
}

/**
 * @param {string} pkgRoot directory containing gateway.mjs
 */
export function loadConfig (pkgRoot) {
  const remoteRoot = envString('GATEWAY_ROOT', DEFAULT_REMOTE_ROOT)
  const port = envInt('PORT', DEFAULT_PORT)
  if (!Number.isInteger(port) || port < 1 || port > 65535) throw new Error(`invalid PORT: ${port}`)

  const bindHost = envString('BIND_HOST', '0.0.0.0')
  const publicHost = envString('GATEWAY_PUBLIC_HOST', detectPublicHost())
  const publicPort = envInt('GATEWAY_PUBLIC_PORT', port)
  const urlScheme = envBool('TLS_ENABLED', true) ? 'wss' : 'ws'
  const explicitUrl = envString('GATEWAY_PUBLIC_URL', undefined)
  const publicUrl = explicitUrl ?? `${urlScheme}://${publicHost}:${publicPort}`

  const tlsEnabled = envBool('TLS_ENABLED', true)
  const certFile = path.resolve(envString('CERT_FILE', path.join(remoteRoot, 'tls', 'gateway-cert.pem')))
  const keyFile = path.resolve(envString('KEY_FILE', path.join(remoteRoot, 'tls', 'gateway-key.pem')))

  const apkDir = envString('APK_DIR', path.join(remoteRoot, 'apk'))
  // Category resource centre: <RESOURCE_DIR>/<id> (dsh-remote, smartbill, relay,
  // docs). APK_DIR is kept only as a legacy download fallback / rollback path.
  const resourceDir = envString('RESOURCE_DIR', path.join(remoteRoot, 'resources'))
  // Upload/delete bearer token. Empty -> upload endpoints answer 404 (disabled).
  const uploadToken = envString('UPLOAD_TOKEN', '') || null
  const maxUploadBytes = envInt('MAX_UPLOAD_BYTES', 200 * 1024 * 1024)
  if (!Number.isInteger(maxUploadBytes) || maxUploadBytes <= 0) {
    throw new Error(`invalid MAX_UPLOAD_BYTES: ${maxUploadBytes} (must be a positive integer)`)
  }
  const registerSecret = envString('REGISTER_SECRET', '') || null

  const config = {
    pkgRoot,
    version: '0.1.0', // overwritten by gateway.mjs from package.json
    name: envString('GATEWAY_NAME', 'DSH Remote Gateway'),
    port,
    bindHost,
    publicHost,
    publicPort,
    publicUrl,
    tlsEnabled,
    certFile,
    keyFile,
    apkDir,
    resourceDir,
    uploadToken,
    maxUploadBytes,
    registerSecret,
    // Connector heartbeat: connector sends {"kind":"ping"} every 20 s; the
    // gateway retires a tunnel that has been silent for this long.
    tunnelSilenceTimeoutMs: envInt('TUNNEL_SILENCE_TIMEOUT_MS', 60_000),
    // How long a disconnected tunnel stays in the registry (for better error
    // pages and so a same-token reconnect is instant). Cookies are forgotten
    // with the tunnel record.
    offlineTtlMs: envInt('OFFLINE_TTL_MS', 30 * 60_000),
    // One HTTP stream through the connector must finish within this window.
    httpTimeoutMs: envInt('HTTP_TIMEOUT_MS', 120_000),
    // Desktop-side connector must accept a ws-open within this window.
    wsOpenTimeoutMs: envInt('WS_OPEN_TIMEOUT_MS', 20_000),
    // Request bodies are carried base64 inside one JSON frame in v1.
    maxBodyBytes: envInt('MAX_BODY_BYTES', 32 * 1024 * 1024),
    maxWsMessageBytes: envInt('MAX_WS_MESSAGE_BYTES', 16 * 1024 * 1024),
    maxFrameBytes: envInt('MAX_FRAME_BYTES', 64 * 1024 * 1024),
    maxStreamsPerTunnel: envInt('MAX_STREAMS_PER_TUNNEL', 256),
    logLevel: envString('LOG_LEVEL', 'info').toLowerCase(),
    nodeEnv: envString('NODE_ENV', 'production'),
    tls: undefined
  }

  if (!Number.isInteger(publicPort) || publicPort < 1 || publicPort > 65535) {
    throw new Error(`invalid GATEWAY_PUBLIC_PORT: ${publicPort}`)
  }

  if (config.tlsEnabled) {
    config.tls = { cert: readTlsFile(certFile, 'TLS certificate'), key: readTlsFile(keyFile, 'TLS private key') }
  }

  return config
}
