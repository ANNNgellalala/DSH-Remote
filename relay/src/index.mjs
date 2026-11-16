// dsh-remote-relay — DSH host plugin (Cordis).
//
// Goal: let a phone reach the official DSH Remote protocol of this desktop.
// Two transports share one relay:
//   - LAN: TLS listener on one concrete LAN/VPN address (the desktop GUI keeps
//     binding loopback only; this plugin is an extra listener)
//   - Gateway (M6): an outbound WebSocket connector to a public relay server that
//     tunnels phone traffic into a loopback-only plain copy of the same proxy
//
// What it does:
//   1. reads ctx.webServer.port and the process launch token via
//      ctx.connection.authenticatedUrl()
//   2. starts the TLS reverse proxy (HTTP + WebSocket upgrade)
//   3. runs a startup protocol self-check (version + required Remote endpoints)
//   4. optionally connects to a public gateway and publishes a v2 pairing
//   5. writes ~/.dsh/remote/pairing.json and serves the loopback pairing page
//
// Relay-server configuration (v0.4.0):
//   The gateway address is no longer fixed in the DSH profile patch. It lives in
//   a persisted JSON document (`~/.dsh/remote/relay-config.json`, see config.mjs)
//   that the user edits through the loopback management page
//   (https://127.0.0.1:8443/__relay/pair), `POST /__relay/config`, or
//   scripts/relay-config.ps1. Changes are persisted and applied live (connector
//   restart) without restarting DSH. A file watch also picks up manual edits.
//   The legacy `config.gateway` patch block keeps working as a fallback and is
//   migrated once into the JSON document when the document does not exist yet.
//
// Business semantics never pass through this plugin: it is a transport hop.
import fs from 'node:fs'
import os from 'node:os'
import path from 'node:path'
import { fileURLToPath } from 'node:url'
import { detectLanIPv4, startPlainProxy, startRelay } from './proxy.mjs'
import { createSessionSummaryApi } from './session-summary.mjs'
import {
  buildPairing,
  buildPairingUri,
  pairingFilePath,
  spkiFingerprint,
  writePairingFile
} from './pairing.mjs'
import {
  DEFAULT_GATEWAY_NAME,
  gatewayIdentityPath,
  gatewaySummary,
  gatewayUrlFor,
  normalizeGatewaySettings,
  readRelayConfig,
  relayConfigFilePath,
  writeRelayConfigFile
} from './config.mjs'
import { probeProtocol, readRelayVersion, REQUIRED_CAPABILITIES, resolveDshVersion } from './probe.mjs'
import { APP_READY_TIMEOUT_MS, createSingleFlight, probeWithRetry, waitForAppReady } from './readiness.mjs'
import { describeLatest, latestSummarySync, resolveDownloadDir } from './downloads.mjs'
import { ensureIdentity, identityFilePath, maskToken, rotateIdentity } from './identity.mjs'
import { createGatewayConnector, parseGatewayUrl, probeGatewayIdentity } from './gateway.mjs'
import { createAclGuard } from './acl-guard.mjs'
import { createAutoApproveService } from './auto-approve.mjs'

export const name = 'remote-relay'
export const inject = ['webServer', 'connection']

const PKG_ROOT = path.resolve(path.dirname(fileURLToPath(import.meta.url)), '..')
const DEFAULT_TLS_DIR = path.join(os.homedir(), '.dsh', 'remote', 'certs')
const DEFAULT_PORT = 8443
// One initial attempt plus four backoff retries: covers the window in which a
// host registers the Remote mux route asynchronously after boot.
const SELF_CHECK_ATTEMPTS = 5
const VERSION = readRelayVersion(PKG_ROOT)

function log (line) {
  // The desktop host forwards console output to its log; keep one line per fact.
  console.log(line)
}

/**
 * Flatten a tool result into the small text window the ACL guard scans for
 * failure signatures. Bounded on purpose: results can be megabytes.
 * @param {unknown} value
 * @param {number} [budget]
 * @returns {string}
 */
export function collectResultText (value, budget = 4000) {
  const parts = []
  let left = budget
  const visit = (node, depth) => {
    if (left <= 0 || depth > 6 || node === null || node === undefined) return
    if (typeof node === 'string') {
      parts.push(node)
      left -= node.length
      return
    }
    if (Array.isArray(node)) {
      for (const item of node) visit(item, depth + 1)
      return
    }
    if (typeof node === 'object') {
      for (const key of ['text', 'content', 'message', 'detail', 'stdout', 'stderr']) {
        if (key in node) visit(node[key], depth + 1)
      }
    }
  }
  visit(value, 0)
  return parts.join('\n').slice(0, budget)
}

function resolveTlsPaths (config) {
  const tls = config && typeof config.tls === 'object' && config.tls !== null ? config.tls : {}
  const certFile = tls.certFile
    ? path.resolve(String(tls.certFile))
    : path.join(DEFAULT_TLS_DIR, 'relay-cert.pem')
  const keyFile = tls.keyFile
    ? path.resolve(String(tls.keyFile))
    : path.join(DEFAULT_TLS_DIR, 'relay-key.pem')
  return { certFile, keyFile }
}

/** Read the process launch token out of the host's own authenticated URL. */
function readLaunchToken (ctx, upstreamPort, upstreamProtocol) {
  const base = `${upstreamProtocol}//127.0.0.1:${upstreamPort}`
  const authenticated = ctx.connection.authenticatedUrl(base)
  const token = new URL(authenticated).searchParams.get('token')
  if (!token) {
    throw new Error(`dsh-remote-relay: authenticatedUrl(${base}) returned no token`)
  }
  return token
}

/**
 * Translate the legacy Cordis `config.gateway` block into gateway settings.
 * Supports both the historical `url` form and the granular host/port/path form.
 */
function legacyGatewaySettings (raw) {
  const value = raw && typeof raw === 'object' && !Array.isArray(raw) ? raw : {}
  const enabled = value.enabled === true
  const base = {
    enabled,
    name: value.name ? String(value.name) : DEFAULT_GATEWAY_NAME,
    fingerprint: value.fingerprint,
    secret: value.secret
  }
  if (value.url) {
    let parsed
    try {
      parsed = parseGatewayUrl(String(value.url))
    } catch (error) {
      throw new Error(`dsh-remote-relay: invalid config.gateway.url: ${error.message}`)
    }
    return {
      settings: normalizeGatewaySettings({
        ...base,
        enabled,
        host: parsed.host,
        port: parsed.port,
        path: String(parsed.path).split('?')[0],
        secure: parsed.secure
      }),
      identityFile: value.identityFile ? path.resolve(String(value.identityFile)) : undefined,
      token: value.token ? String(value.token) : undefined
    }
  }
  return {
    settings: normalizeGatewaySettings({
      ...base,
      enabled,
      host: value.host,
      port: value.port,
      path: value.path,
      secure: value.secure
    }),
    identityFile: value.identityFile ? path.resolve(String(value.identityFile)) : undefined,
    token: value.token ? String(value.token) : undefined
  }
}

/** Effective settings: the persisted document wins over the profile patch. */
function resolveEffectiveGateway (persisted, legacySettings) {
  if (persisted && persisted.exists && !persisted.error && persisted.gateway) {
    return { settings: persisted.gateway, source: 'file' }
  }
  if (legacySettings && legacySettings.enabled) return { settings: legacySettings, source: 'patch' }
  return { settings: legacySettings, source: 'default' }
}

function sameGatewaySettings (a, b) {
  if (!a || !b) return false
  return a.enabled === b.enabled &&
    a.url === b.url &&
    (a.fingerprint ?? null) === (b.fingerprint ?? null) &&
    a.name === b.name &&
    (a.secret ?? null) === (b.secret ?? null)
}

export function apply (ctx, config = {}) {
  const cfg = config && typeof config === 'object' ? config : {}
  const webServer = ctx.webServer
  if (!webServer) throw new Error('dsh-remote-relay: webServer service is unavailable')

  const upstreamPort = Number(webServer.port)
  if (!Number.isInteger(upstreamPort) || upstreamPort < 1) {
    throw new Error(`dsh-remote-relay: ctx.webServer.port is not bound yet (${String(webServer.port)})`)
  }
  const upstreamProtocol = webServer.protocol === 'https:' ? 'https:' : 'http:'

  const host = (cfg.host && String(cfg.host).trim()) || detectLanIPv4()
  const port = cfg.port === undefined || cfg.port === null ? DEFAULT_PORT : Number(cfg.port)
  if (!Number.isInteger(port) || port < 1 || port > 65535) {
    throw new Error(`dsh-remote-relay: invalid config.port ${JSON.stringify(cfg.port)}`)
  }

  // Legacy public address (e.g. an SSH/reverse tunnel to this relay). In gateway
  // mode the remote address comes from the configured gateway instead.
  const publicHost = cfg.publicHost ? String(cfg.publicHost).trim() : host
  const publicPort = cfg.publicPort === undefined || cfg.publicPort === null ? port : Number(cfg.publicPort)
  if (!publicHost) throw new Error('dsh-remote-relay: invalid config.publicHost')
  if (!Number.isInteger(publicPort) || publicPort < 1 || publicPort > 65535) {
    throw new Error(`dsh-remote-relay: invalid config.publicPort ${JSON.stringify(cfg.publicPort)}`)
  }
  const name = cfg.name ? String(cfg.name) : os.hostname()
  const { certFile, keyFile } = resolveTlsPaths(cfg)
  if (!fs.existsSync(certFile) || !fs.existsSync(keyFile)) {
    throw new Error(
      'dsh-remote-relay: TLS material not found. Expected ' +
      `${certFile} and ${keyFile}. Run "pwsh -File scripts/gen-tls.ps1" ` +
      '(it writes ~/.dsh/remote/certs by default), copy existing relay-cert.pem/relay-key.pem there, ' +
      'or set config.tls.certFile / config.tls.keyFile explicitly.'
    )
  }

  // config.token is an escape hatch (e.g. debugging, or a host build whose
  // authenticatedUrl is unavailable). Normally the token is discovered.
  const launchToken = cfg.token ? String(cfg.token) : readLaunchToken(ctx, upstreamPort, upstreamProtocol)
  if (!launchToken) throw new Error('dsh-remote-relay: no launch token available')

  const pairingFile = cfg.pairingFile ? path.resolve(String(cfg.pairingFile)) : pairingFilePath()
  const localFp = spkiFingerprint(certFile)
  const dsh = resolveDshVersion()

  // ---- persistent relay-server configuration -------------------------------
  const configFile = relayConfigFilePath(cfg.configFile)
  const legacy = legacyGatewaySettings(cfg.gateway)
  const identityFile = legacy.identityFile ?? identityFilePath()
  let persisted = readRelayConfig(configFile)
  let configError = persisted.error ?? null
  let effective = resolveEffectiveGateway(persisted, legacy.settings)
  let gatewaySource = effective.source
  let gatewayCfg = {
    ...effective.settings,
    url: effective.settings.host ? gatewayUrlFor(effective.settings) : '',
    identityFile
  }

  // One-time migration: an install that still carries the server address in the
  // DSH profile patch gets a persisted config document, so later edits no longer
  // need YAML/reload. Runs only while the document is missing; never hardcodes a
  // server address (the values come from the machine's own existing config).
  if (!persisted.exists && legacy.settings.enabled && legacy.settings.host) {
    try {
      writeRelayConfigFile(configFile, {
        gateway: legacy.settings,
        initializedAt: new Date().toISOString(),
        initializedBy: 'one-time-migration:profile-patch'
      })
      persisted = readRelayConfig(configFile)
      if (persisted.error) throw new Error(persisted.error)
      configError = null
      effective = resolveEffectiveGateway(persisted, legacy.settings)
      gatewaySource = effective.source
      gatewayCfg = {
        ...effective.settings,
        url: effective.settings.host ? gatewayUrlFor(effective.settings) : '',
        identityFile
      }
      log(`[remote-relay] config: one-time migration wrote ${configFile} from the legacy profile patch (server ${gatewayCfg.url})`)
    } catch (error) {
      log(`[remote-relay] WARN config: one-time migration failed (${error && error.message ? error.message : String(error)}); falling back to the profile patch`)
    }
  }

  let identity = null
  let remoteToken = null

  function ensureGatewayIdentity () {
    const ensured = ensureIdentity({ file: identityFile, token: legacy.token, name })
    identity = ensured.identity
    remoteToken = identity.token
    health.identity = {
      file: identityFile,
      name: identity.name,
      tokenMasked: maskToken(remoteToken),
      createdAt: identity.createdAt,
      rotatedAt: identity.rotatedAt ?? null
    }
    return ensured
  }

  // Release APK download catalogue: config.downloadDir > DSH_REMOTE_APK_DIR >
  // <repo>/dist/apk > ~/.dsh/remote/apk. The directory is rescanned per request,
  // so newly built APKs show up without reloading the relay.
  const downloadDir = resolveDownloadDir(cfg.downloadDir, PKG_ROOT)
  const downloadSummary = () => describeLatest(downloadDir)
  const downloadPairingField = () => {
    try {
      return latestSummarySync(downloadDir)
    } catch {
      return { dir: downloadDir, url: '/__relay/download', count: 0, latest: null }
    }
  }
  log(`[remote-relay] APK download dir: ${downloadDir} (page: /__relay/download)`)

  // A pairing document from a previous load may hold a dead token (the desktop
  // process token is per-process). Remove it, then write a fresh one only once
  // the relay listens (provisional inactive -> checked result).
  try {
    fs.unlinkSync(pairingFile)
  } catch {}

  let currentPairing = null
  const writePairing = (doc) => {
    writePairingFile(doc, pairingFile)
    currentPairing = doc
    return doc
  }

  // Runtime state shared by the pairing builder, health endpoint, config page
  // and the token rotation endpoint.
  let relayHandle = null
  let internalProxy = null
  let connector = null
  let protocolProbe = null
  let protocolActive = false
  // True while no relay effect owns the listener (startup, HMR dispose). The
  // self-check waits on appReady and must not publish stale state after dispose.
  let relayClosed = true
  const gatewayState = {
    url: gatewayCfg.url,
    name: gatewayCfg.name,
    fp: null,
    online: false,
    usable: false,
    error: gatewayCfg.enabled ? 'gateway identity not checked yet' : null,
    tunnels: null
  }

  const gatewayHealth = () => ({
    enabled: gatewayCfg.enabled === true,
    host: gatewayCfg.host || '',
    port: gatewayCfg.port,
    path: gatewayCfg.path,
    secure: gatewayCfg.secure !== false,
    url: gatewayCfg.url,
    name: gatewayCfg.name,
    transport: gatewayCfg.secure === false ? 'ws' : 'wss',
    identityFile,
    tokenMasked: maskToken(remoteToken),
    online: gatewayState.online,
    fp: gatewayState.fp,
    usable: gatewayState.usable,
    error: gatewayState.error,
    source: gatewaySource,
    configFile,
    configError,
    internalPort: internalProxy?.port ?? null
  })

  const health = {
    name,
    version: VERSION,
    relayVersion: VERSION,
    dshVersion: dsh.version,
    dshVersionSource: dsh.source,
    host,
    port,
    publicHost: publicHost !== host || publicPort !== port ? publicHost : undefined,
    publicPort: publicHost !== host || publicPort !== port ? publicPort : undefined,
    upstreamPort,
    fp: localFp,
    startedAt: new Date().toISOString(),
    pairingPage: '/__relay/pair',
    downloadPage: '/__relay/download',
    gateway: gatewayHealth(),
    identity: null,
    protocolProbe: {
      status: 'pending',
      required: REQUIRED_CAPABILITIES
    },
    autoApprove: {
      schemaVersion: 2,
      enabled: false,
      all: false,
      workspaces: 0,
      sessions: 0,
      revision: 0,
      updatedAt: null,
      stats: { allowed: 0, errors: 0, lastAllowedAt: null },
      error: null
    }
  }

  // ---- desktop-side auto-approval -----------------------------------------
  // The phone is not always running; approvals therefore need an answerer in
  // the host process. The listener is prepended so it settles a covered request
  // before `api-remotes` forwards it to the desktop UI / phone. Rules live in
  // ~/.dsh/remote/auto-approve.json and are edited by the phone through the
  // relay-owned `/__relay/auto-approve` API (see proxy.mjs).
  const autoApproveFile = cfg.autoApprove && cfg.autoApprove.file
    ? path.resolve(String(cfg.autoApprove.file))
    : undefined
  const autoApprove = createAutoApproveService({
    ctx,
    file: autoApproveFile,
    log,
    tokens: () => [remoteToken, launchToken],
    onChange: (summary) => {
      health.autoApprove = summary
    }
  })
  health.autoApprove = autoApprove.summary()
  if (health.autoApprove.enabled) {
    log(`[remote-relay] auto-approve enabled: all=${health.autoApprove.all} workspaces=${health.autoApprove.workspaces} sessions=${health.autoApprove.sessions} (${autoApprove.file})`)
  } else {
    log(`[remote-relay] auto-approve disabled (${autoApprove.file})`)
  }

  // Own the listener/watcher lifetime: HMR reload, profile recompose and
  // process shutdown all dispose them before rebinding.
  ctx.effect(() => autoApprove.install(), 'remote-relay: desktop auto-approval')

  log(`[remote-relay] v${VERSION} starting; DSH runtime ${dsh.version}${dsh.source ? ` (${dsh.source})` : ' (source unknown)'}`)
  log(`[remote-relay] upstream ${upstreamProtocol}//127.0.0.1:${upstreamPort} -> https://${host}:${port} (loopback mirror on 127.0.0.1:${port})`)
  log(`[remote-relay] TLS cert ${certFile} spki-sha256=${localFp}`)
  log(`[remote-relay] config ${configFile} (${gatewaySource}${gatewayCfg.enabled ? `, gateway ${gatewayCfg.url}` : ', gateway disabled'})`)

  // ---- gateway stack lifecycle (start / stop / live re-apply) ---------------
  let gatewayApplyQueue = Promise.resolve()

  function onGatewayState (state) {
    const wasOnline = gatewayState.online
    gatewayState.online = state.connected
    gatewayState.usable = Boolean(gatewayCfg.enabled && gatewayState.fp)
    if (!state.connected && state.lastError) gatewayState.error = state.lastError
    if (state.connected) gatewayState.error = null
    health.gateway = gatewayHealth()
    if (wasOnline !== state.connected) {
      log(`[remote-relay] gateway tunnel ${state.connected ? 'online' : 'offline'}${state.lastError ? ` (${state.lastError})` : ''}`)
      publish('gateway-state')
    }
  }

  async function startGatewayStack () {
    if (!gatewayCfg.enabled || !relayHandle) return
    ensureGatewayIdentity()
    if (!identity) return
    log(`[remote-relay] gateway enabled: ${gatewayCfg.url} as "${name}" remote-token=${maskToken(remoteToken)} identity=${identityFile}`)

    if (!internalProxy) {
      // Loopback-only plain proxy the gateway connector tunnels into.
      try {
        internalProxy = await startPlainProxy({ upstreamPort, upstreamProtocol, health, aclApi, sessionSummaryApi, autoApproveApi: autoApprove.api, log })
        log(`[remote-relay] gateway: internal plain proxy on 127.0.0.1:${internalProxy.port}`)
      } catch (error) {
        gatewayState.error = `internal proxy failed: ${error && error.message ? error.message : String(error)}`
        log(`[remote-relay] ERROR gateway: ${gatewayState.error}`)
        health.gateway = gatewayHealth()
        return
      }
    }

    // Identity probe (TOFU or pinned fingerprint).
    let probe
    try {
      probe = await probeGatewayIdentity({
        gatewayUrl: gatewayCfg.url,
        expectedFp: gatewayCfg.fingerprint,
        identityPath: gatewayIdentityPath(gatewayCfg.path)
      })
    } catch (error) {
      probe = { ok: false, error: error.message }
    }
    if (probe.ok) {
      gatewayState.fp = probe.fp ?? null
      gatewayState.usable = Boolean(probe.fp)
      gatewayState.error = gatewayState.usable ? null : 'gateway certificate fingerprint unavailable'
      const identityInfo = probe.identity ?? {}
      log(`[remote-relay] gateway identity ok: fp=${gatewayState.fp} version=${identityInfo.version ?? '?'} name=${identityInfo.name ?? '?'} tunnels=${identityInfo.tunnels ?? '?'}`)
      gatewayState.tunnels = identityInfo.tunnels ?? null
      if (gatewayCfg.fingerprint && probe.fp && probe.fp !== gatewayCfg.fingerprint) {
        gatewayState.error = `fingerprint mismatch (config ${gatewayCfg.fingerprint})`
        gatewayState.usable = false
      }
    } else {
      gatewayState.fp = probe.fp ?? null
      gatewayState.usable = false
      gatewayState.error = probe.error ?? 'gateway identity unavailable'
      log(`[remote-relay] ERROR gateway identity failed: ${gatewayState.error}`)
    }
    health.gateway = gatewayHealth()

    if (!gatewayCfg.enabled) return
    connector = createGatewayConnector({
      gatewayUrl: gatewayCfg.url,
      token: remoteToken,
      name,
      launchToken,
      secret: gatewayCfg.secret,
      internalPort: internalProxy.port,
      log,
      onState: onGatewayState
    })
    connector.start()
  }

  async function stopGatewayStack ({ closeProxy = false } = {}) {
    const previous = connector
    connector = null
    try { previous?.stop() } catch {}
    gatewayState.online = false
    gatewayState.usable = false
    gatewayState.fp = null
    if (closeProxy && internalProxy) {
      const proxy = internalProxy
      internalProxy = null
      try { await proxy.close() } catch {}
    }
    health.gateway = gatewayHealth()
  }

  /** Apply already-validated gateway settings live (connector restart). */
  async function applyGatewaySettings (settings, { reason = 'live', force = false, source = 'file' } = {}) {
    const next = {
      ...settings,
      url: settings.host ? gatewayUrlFor(settings) : '',
      identityFile
    }
    const unchanged = sameGatewaySettings(next, gatewayCfg)
    gatewayCfg = next
    gatewaySource = source
    gatewayState.url = next.url
    gatewayState.name = next.name
    if (unchanged && !force) {
      health.gateway = gatewayHealth()
      return configState()
    }
    await stopGatewayStack({ closeProxy: !next.enabled })
    gatewayState.error = next.enabled ? 'gateway restarting' : null
    health.gateway = gatewayHealth()
    log(`[remote-relay] config: applying ${next.enabled ? next.url : 'gateway disabled'} (${reason})`)
    if (next.enabled) await startGatewayStack()
    publish(`config-${reason}`)
    return configState()
  }

  /** Serialize config applies so two saves cannot interleave the connector. */
  function queueGatewayApply (settings, options) {
    const task = gatewayApplyQueue.then(() => applyGatewaySettings(settings, options))
    gatewayApplyQueue = task.then(() => {}, () => {})
    return task
  }

  // Re-read the persisted document (manual edit / external writer).
  async function reloadPersistedConfig (reason) {
    const current = readRelayConfig(configFile)
    if (current.exists && current.error) {
      configError = current.error
      health.gateway = gatewayHealth()
      log(`[remote-relay] WARN config: ${current.error}`)
      return
    }
    configError = null
    const resolved = resolveEffectiveGateway(current, legacy.settings)
    const next = { ...resolved.settings, url: resolved.settings.host ? gatewayUrlFor(resolved.settings) : '', identityFile }
    if (sameGatewaySettings(next, gatewayCfg)) {
      health.gateway = gatewayHealth()
      return
    }
    await queueGatewayApply(resolved.settings, { reason, force: false, source: resolved.source })
  }

  // ---- public projections used by the loopback config API/page -------------
  function configState () {
    const current = readRelayConfig(configFile)
    const resolved = resolveEffectiveGateway(current, legacy.settings)
    return {
      ok: true,
      file: configFile,
      exists: current.exists === true,
      error: current.error ?? configError ?? null,
      source: gatewaySource,
      updatedAt: current.updatedAt ?? null,
      initializedAt: current.initializedAt ?? null,
      initializedBy: current.initializedBy ?? null,
      desktopName: name,
      identityFile,
      gateway: {
        ...gatewaySummary(gatewayCfg),
        online: gatewayState.online,
        usable: gatewayState.usable,
        error: gatewayState.error,
        identityFingerprint: gatewayState.fp ?? null,
        internalPort: internalProxy?.port ?? null,
        source: gatewaySource,
        url: gatewayCfg.url,
        configFile
      },
      stored: current.gateway ? gatewaySummary(current.gateway) : null,
      legacy: {
        source: resolved.source,
        enabled: resolved.settings.enabled === true
      }
    }
  }

  const configProvider = () => configState()

  const configApplyProvider = async (patch) => {
    if (!relayHandle) {
      const error = new Error('relay is still starting; retry in a moment')
      error.statusCode = 503
      throw error
    }
    const body = patch && typeof patch === 'object' ? patch : {}
    const rawGateway = body.gateway && typeof body.gateway === 'object' ? body.gateway : body
    // Optional one-time initialization marker (installer seed / migrations).
    const requestedMarker = typeof body.initializedBy === 'string' && body.initializedBy.trim()
      ? body.initializedBy.trim().slice(0, 80)
      : undefined
    const current = readRelayConfig(configFile)
    const base = resolveEffectiveGateway(current, legacy.settings).settings
    const pick = (key, fallback) => (rawGateway[key] === undefined ? fallback : rawGateway[key])
    const settings = normalizeGatewaySettings({
      enabled: pick('enabled', base.enabled),
      host: pick('host', base.host),
      port: pick('port', base.port),
      path: pick('path', base.path),
      secure: pick('secure', base.secure),
      fingerprint: pick('fingerprint', base.fingerprint),
      name: pick('name', base.name),
      secret: pick('secret', base.secret)
    })
    const written = writeRelayConfigFile(configFile, {
      gateway: settings,
      // Mark only the transition from "no document" to "document"; later saves
      // keep the original marker.
      initializedAt: current.exists ? current.initializedAt : (requestedMarker ? new Date().toISOString() : undefined),
      initializedBy: requestedMarker ?? current.initializedBy
    })
    configError = null
    log(`[remote-relay] config: saved ${written.file} (${settings.enabled ? `${settings.host}:${settings.port}${settings.path}` : 'gateway disabled'})`)
    const applied = await queueGatewayApply(settings, { reason: 'save', force: true })
    return applied
  }

  const rotateProvider = async () => {
    if (!gatewayCfg.enabled) {
      const error = new Error('token rotation is not available (gateway disabled)')
      error.statusCode = 501
      throw error
    }
    const rotated = rotateIdentity({ file: identityFile, name })
    identity = rotated.identity
    remoteToken = rotated.identity.token
    health.identity = {
      file: identityFile,
      name: rotated.identity.name,
      tokenMasked: maskToken(remoteToken),
      createdAt: rotated.identity.createdAt,
      rotatedAt: rotated.identity.rotatedAt ?? null
    }
    health.gateway = gatewayHealth()
    if (connector) connector.rotate(remoteToken)
    gatewayState.online = false
    gatewayState.error = 'token rotated; reconnecting'
    health.gateway = gatewayHealth()
    publish('token-rotate')
    log(`[remote-relay] remote token rotated (${maskToken(remoteToken)})`)
    return { tokenMasked: maskToken(remoteToken), rotatedAt: rotated.identity.rotatedAt ?? null }
  }

  // ---- pairing document -----------------------------------------------------
  const buildCurrentPairing = () => {
    const gatewayUsable = gatewayCfg.enabled && gatewayState.usable && Boolean(gatewayState.fp)
    const gatewayField = gatewayCfg.enabled
      ? {
          url: gatewayState.url,
          name: gatewayState.name,
          online: gatewayState.online,
          fp: gatewayState.fp,
          usable: gatewayUsable,
          error: gatewayState.error
        }
      : undefined
    const remoteHost = gatewayUsable ? gatewayCfg.host : publicHost
    const remotePort = gatewayUsable ? gatewayCfg.port : publicPort
    const remoteFp = gatewayUsable ? gatewayState.fp : localFp
    return buildPairing({
      host: remoteHost,
      port: remotePort,
      token: gatewayUsable ? remoteToken : launchToken,
      fp: remoteFp,
      name,
      localHost: relayHandle?.host ?? host,
      localPort: relayHandle?.port ?? port,
      localToken: launchToken,
      localFp,
      gateway: gatewayField,
      loopbackPort: relayHandle?.loopbackPort ?? port,
      upstreamPort,
      relayVersion: VERSION,
      dshVersion: dsh.version,
      active: protocolActive,
      capabilities: protocolProbe?.capabilities,
      probe: protocolProbe,
      download: downloadPairingField(),
      error: protocolActive
        ? undefined
        : (protocolProbe
            ? {
                code: 'protocol-self-check-failed',
                message: `required Remote capabilities unavailable: ${(protocolProbe.missing ?? []).length > 0 ? protocolProbe.missing.join(', ') : 'see probe.errors'}`,
                missing: protocolProbe.missing,
                errors: protocolProbe.errors
              }
            : { code: 'self-check-pending', message: 'relay startup self-check in progress' })
    })
  }

  let publishPairing = () => {}
  const publish = (reason) => {
    if (!relayHandle) return
    try {
      const doc = buildCurrentPairing()
      writePairing(doc)
      health.pairing = {
        schemaVersion: doc.schemaVersion,
        mode: doc.mode ?? 'local',
        active: doc.active,
        updatedAt: doc.updatedAt
      }
      const gw = doc.gateway ? (doc.gateway.online ? 'online' : 'offline') : 'off'
      log(`[remote-relay] pairing v${doc.schemaVersion} written (${reason}; active=${doc.active}; gateway=${gw}; host=${doc.host}:${doc.port})`)
    } catch (error) {
      log(`[remote-relay] WARN cannot write pairing file ${pairingFile}: ${error && error.message ? error.message : String(error)}`)
    }
  }
  publishPairing = publish

  // ---- protocol self-check ---------------------------------------------------
  //
  // DSH registers the Remote stream mux (`/api/remote.mux`) only after the
  // launcher commits `appReady`, i.e. after the whole plugin tree finished
  // booting. This plugin is mounted *during* that boot, so a probe issued right
  // away can hit a host webserver with no upgrade route for the path: the
  // socket is destroyed (`socket hang up`) and $events/session/follow were then
  // latched as unavailable for the lifetime of the process (which also hid the
  // pairing QR code). Wait for appReady, then retry transient mux failures.
  //
  // The same routine backs the loopback-only `POST /__relay/probe` re-check:
  // one shared in-flight run, so a page button cannot stack probes.
  const runSelfCheck = createSingleFlight(async (reason) => {
    const appReadyObserved = await waitForAppReady(ctx, { timeoutMs: APP_READY_TIMEOUT_MS, log })
    if (relayClosed) return null
    if (appReadyObserved) log('[remote-relay] appReady observed; running the protocol self-check')
    const { result, attempts } = await probeWithRetry({
      probe: () => probeProtocol({ port: upstreamPort, token: launchToken, protocol: upstreamProtocol, log }),
      attempts: SELF_CHECK_ATTEMPTS,
      continueWhen: () => !relayClosed,
      log
    })
    if (relayClosed) return null
    protocolProbe = result
    protocolActive = protocolProbe?.ok === true
    health.protocolProbe = {
      status: protocolActive ? 'ok' : 'failed',
      required: REQUIRED_CAPABILITIES,
      ...protocolProbe,
      attempts,
      appReady: appReadyObserved
    }
    publish(reason)
    const summary = Object.entries(protocolProbe?.capabilities ?? {}).map(([key, value]) => `${key}=${String(value)}`).join(' ')
    if (protocolActive) {
      const suffix = attempts > 1 ? ` after ${attempts} attempts` : ''
      log(`[remote-relay] self-check OK${suffix} (${summary})`)
    } else {
      log(`[remote-relay] ERROR self-check FAILED: missing=${JSON.stringify(protocolProbe?.missing ?? [])} (${summary})`)
      for (const line of protocolProbe?.errors ?? []) log(`[remote-relay] ERROR probe: ${line}`)
      log(`[remote-relay] ERROR pairing marked active=false; fix compatibility before pairing a phone (${pairingFile})`)
    }
    // The manual re-check endpoint reports this wrapper (status/attempts/appReady).
    return health.protocolProbe
  })

  // ---- Windows workspace ACL admission guard -------------------------------
  // Guarantees that a new session's workspace can be provisioned by the DSH
  // sandbox without the agent first asking the user to approve a one-shot
  // `icacls` repair. The guard runs inside the host process (the user's own
  // token, not the restricted sandbox token), so it never needs an approval.
  //
  // Scope: only the workspace of a session that is actually starting, and only
  // once per workspace (results are cached under ~/.dsh/remote). Workspaces no
  // new session uses are never touched.
  const aclGuard = createAclGuard({
    log,
    options: cfg.aclGuard && typeof cfg.aclGuard === 'object' && !Array.isArray(cfg.aclGuard)
      ? cfg.aclGuard
      : {}
  })
  if (aclGuard.disabled) {
    log('[remote-relay] acl-guard disabled (non-Windows, aclGuard.enabled=false, or waitMs<=0)')
  } else {
    log('[remote-relay] acl-guard enabled: new sessions get a workspace admission check before their first tool call')
  }

  /**
   * Roots a phone-side health check may touch: every workspace the guard has
   * already seen plus the workspaces of sessions the host currently knows
   * about. Arbitrary paths are deliberately not accepted.
   * @returns {string[]}
   */
  const aclCandidateRoots = () => {
    const roots = aclGuard.knownRoots().map((row) => row.root)
    try {
      const list = ctx.sessions && typeof ctx.sessions.list === 'function' ? ctx.sessions.list() : []
      for (const session of list) {
        const cwd = session && session.header ? session.header.cwd : undefined
        if (typeof cwd === 'string' && cwd) roots.push(cwd)
      }
    } catch {}
    const unique = []
    for (const root of roots) {
      if (!unique.some((existing) => existing.toLowerCase() === String(root).toLowerCase())) unique.push(String(root))
    }
    return unique
  }

  // Relay-owned ACL API consumed by the phone settings card. It is served by
  // both listeners and authenticates with either pairing credential (gateway
  // pairing token or LAN launch token), because the phone holds exactly one of
  // them depending on how it was paired.
  const aclApi = {
    tokens: () => [remoteToken, launchToken].filter((value) => typeof value === 'string' && value.length > 0),
    status: () => ({
      guard: {
        ...aclGuard.status(),
        roots: aclGuard.knownRoots()
      },
      candidates: aclCandidateRoots()
    }),
    check: async (request) => {
      const repair = !(request && request.repair === false)
      const known = aclCandidateRoots()
      const requested = request && typeof request.root === 'string' && request.root.trim()
        ? [request.root.trim()]
        : known
      const roots = []
      for (const candidate of requested) {
        const match = known.find((root) => root.toLowerCase() === candidate.toLowerCase())
        if (!match) {
          roots.push({ root: candidate, ready: false, repaired: false, reason: 'unknown-root' })
          continue
        }
        const inspected = await aclGuard.inspect(match)
        if (!repair || inspected.skipped || inspected.ready) {
          roots.push({ ...inspected, repaired: false })
          continue
        }
        const startedAt = Date.now()
        const outcome = await aclGuard.ensureReady(match, { force: true })
        roots.push({
          root: match,
          ready: outcome.ready === true,
          repaired: outcome.ready === true,
          durationMs: Date.now() - startedAt,
          reason: outcome.reason
        })
      }
      return { repair, roots }
    }
  }

  // Compact session list for phones (v0.6.0+): trims the upstream
  // `session/list` payload to the fields the list UI renders, cutting ~92% of
  // the bytes. Both listeners share this instance; every upstream call reuses
  // the caller's own dsh-auth cookie (the relay never mints a credential here).
  const sessionSummaryApi = createSessionSummaryApi({ upstreamPort, upstreamProtocol })

  ctx.effect(() => {
    if (aclGuard.disabled) return () => {}
    let open = true
    const disposers = []
    const sessionCwd = (session) => {
      try {
        const cwd = session && session.header ? session.header.cwd : undefined
        return typeof cwd === 'string' && cwd ? cwd : undefined
      } catch {
        return undefined
      }
    }
    // `{ prepend: true }` is supported by the pinned cordis; fall back to a plain
    // listener if a host build rejects the options form. Prepending only decides
    // ordering among pre-execute gates — the guard still runs before the tool
    // body either way, which is what the admission guarantee needs.
    const listen = (event, handler) => {
      try {
        return ctx.on(event, handler, { prepend: true })
      } catch {
        return ctx.on(event, handler)
      }
    }
    try {
      disposers.push(ctx.on('session/created', (session) => {
        if (!open) return
        const cwd = sessionCwd(session)
        if (cwd) aclGuard.warm(cwd)
      }))
      disposers.push(listen('tools/pre-execute', async (exec, next) => {
        if (!open) return next()
        try {
          const agent = exec ? exec.agent : undefined
          const cwd = agent && agent.session ? sessionCwd(agent.session) : undefined
          if (cwd) await aclGuard.ensureReady(cwd)
        } catch {}
        return next()
      }))
      disposers.push(listen('tools/post-execute', async (exec, result, next) => {
        if (!open) return next()
        try {
          const agent = exec ? exec.agent : undefined
          const cwd = agent && agent.session ? sessionCwd(agent.session) : undefined
          if (cwd) aclGuard.noteInstrumentFailure(cwd, collectResultText(result))
        } catch {}
        return next()
      }))
    } catch (error) {
      log(`[remote-relay] WARN acl-guard hook registration failed: ${error && error.message ? error.message : String(error)}`)
    }
    return () => {
      open = false
      for (const off of disposers) {
        try { off() } catch {}
      }
      aclGuard.dispose()
    }
  }, 'remote-relay: Windows workspace ACL admission guard')

  // ctx.effect owns the listener lifetime: HMR reload, profile recompose and
  // process shutdown all dispose the previous listeners before rebinding.
  ctx.effect(() => {
    let relay
    let closed = false
    let unwatchConfig = () => {}
    const started = startRelay({
      host,
      port,
      certFile,
      keyFile,
      upstreamPort,
      upstreamProtocol,
      health,
      pairingProvider: () => currentPairing,
      assetsDir: path.join(PKG_ROOT, 'assets'),
      downloadDir,
      downloadProvider: downloadSummary,
      rotateProvider,
      configProvider,
      configApplyProvider,
      configFilePath: configFile,
      aclApi,
      sessionSummaryApi,
      autoApproveApi: autoApprove.api,
      probeProvider: () => runSelfCheck('manual-self-check'),
      log
    })

    const run = async () => {
      const handle = await started
      if (closed) {
        await handle.close()
        return
      }
      relay = handle
      relayHandle = handle
      relayClosed = false

      // Provisional, explicitly inactive pairing while checks run.
      publish('provisional')

      if (gatewayCfg.enabled) {
        try {
          await startGatewayStack()
        } catch (error) {
          gatewayState.error = `gateway startup failed: ${error && error.message ? error.message : String(error)}`
          health.gateway = gatewayHealth()
          log(`[remote-relay] ERROR gateway: ${gatewayState.error}`)
        }
      }

      // DSH protocol self-check (loopback upstream). runSelfCheck waits for the
      // launcher's appReady signal and retries transient mux failures, so a
      // cold boot cannot latch a startup-race failure into the pairing state.
      await runSelfCheck('startup-self-check')
      if (closed) return

      // Manual edits to relay-config.json are picked up without a plugin reload.
      unwatchConfig = watchConfigFile(configFile, reloadPersistedConfig)

      const pairingMode = gatewayState.usable ? `v2 gateway ${gatewayCfg.url}` : 'v1 LAN'
      log(`[remote-relay] pair (${pairingMode}): ${currentPairing?.pairingUri ?? 'n/a'}`)
      log(`[remote-relay] config page (loopback): https://127.0.0.1:${handle.loopbackPort}/__relay/pair`)
      log(`[remote-relay] ready (health: https://${handle.host}:${handle.port}/__relay/health)`)
    }

    run().catch((error) => {
      log(`[remote-relay] ERROR cannot start relay: ${error && error.stack ? error.stack : String(error)}`)
    })

    return async () => {
      closed = true
      relayClosed = true
      try { unwatchConfig() } catch {}
      try { await stopGatewayStack({ closeProxy: true }) } catch {}
      try {
        if (relay) await relay.close()
        else {
          const handle = await started.catch(() => null)
          if (handle) await handle.close()
        }
      } catch (error) {
        log(`[remote-relay] WARN dispose failed: ${error && error.message ? error.message : String(error)}`)
      }
      log('[remote-relay] stopped')
    }
  }, 'remote-relay: TLS reverse proxy + gateway connector')
}

/**
 * Watch the config directory and reload on relay-config.json changes. Directory
 * watching is used because the file is replaced atomically (tmp + rename).
 * @returns {() => void} disposer
 */
function watchConfigFile (target, reload) {
  const dir = path.dirname(target)
  const base = path.basename(target)
  let timer = null
  try {
    const watcher = fs.watch(dir, { persistent: false }, (event, filename) => {
      if (filename && String(filename) !== base) return
      if (timer) clearTimeout(timer)
      timer = setTimeout(() => {
        timer = null
        Promise.resolve(reload('file-change')).catch((error) => {
          log(`[remote-relay] WARN config reload failed: ${error && error.message ? error.message : String(error)}`)
        })
      }, 300)
      timer.unref?.()
    })
    watcher.on?.('error', () => {})
    return () => {
      try { watcher.close() } catch {}
      if (timer) clearTimeout(timer)
    }
  } catch (error) {
    log(`[remote-relay] WARN cannot watch ${dir} for config changes: ${error && error.message ? error.message : String(error)}`)
    return () => {}
  }
}

// Cordis accepts either a default object or named exports; provide both so the
// loader works regardless of how it unwraps an ESM namespace.
export default { name, inject, apply }
