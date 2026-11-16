// Pairing facts for the phone client: TLS certificate fingerprint, access token,
// LAN endpoint, and the single copy-paste pairing URI.
//
// Everything here is pure node: built-ins (no third-party deps) because the
// plugin runs inside the packaged desktop host process.
import crypto from 'node:crypto'
import fs from 'node:fs'
import os from 'node:os'
import path from 'node:path'

/** Fixed runtime location shared with the Core/Smoke tooling. */
export function pairingFilePath () {
  return path.join(os.homedir(), '.dsh', 'remote', 'pairing.json')
}

/**
 * SHA-256 of the certificate's SubjectPublicKeyInfo (DER), lowercase hex.
 * This is the value a client pins; it survives certificate renewal as long as
 * the key pair stays the same (allows pinning without a CA).
 */
export function spkiFingerprint (certFileOrPem) {
  const pem = certFileOrPem.includes('-----BEGIN CERTIFICATE-----')
    ? certFileOrPem
    : fs.readFileSync(certFileOrPem, 'utf8')
  const cert = new crypto.X509Certificate(pem)
  const spki = cert.publicKey.export({ type: 'spki', format: 'der' })
  return crypto.createHash('sha256').update(spki).digest('hex')
}

/** `dshremote://pair?v=1|2&host=..&port=..&token=..&fp=..&name=..[&mode=gateway]`. */
export function buildPairingUri ({ host, port, token, fp, name, version = 1, mode }) {
  const q = new URLSearchParams({
    v: String(version),
    host,
    port: String(port),
    token,
    fp,
    name
  })
  if (mode) q.set('mode', mode)
  return `dshremote://pair?${q.toString()}`
}

/**
 * Assemble the pairing document written to disk.
 *
 * v1 (LAN only): host/port/token/fp describe the relay TLS listener and the
 * DSH launchToken; the phone connects directly on the LAN.
 * v2 (gateway): host/port/token/fp describe the public gateway and the remote
 * token; `localHost/localPort/localPairingUri` keep the LAN alternative and
 * `gateway` carries `{url, online, fp}`. schemaVersion is 2 for gateway mode.
 */
export function buildPairing ({
  host,
  port,
  token,
  fp,
  name,
  localHost,
  localPort,
  localToken,
  localFp,
  gateway,
  loopbackPort,
  upstreamPort,
  relayVersion,
  dshVersion,
  active = true,
  capabilities,
  probe,
  download,
  error
}) {
  const gatewayMode = Boolean(gateway && gateway.url && gateway.usable !== false)
  const version = gatewayMode ? 2 : 1
  const pairingUri = buildPairingUri({ host, port, token, fp, name, version, mode: gatewayMode ? 'gateway' : undefined })
  const doc = {
    schemaVersion: version,
    active: active === true,
    host,
    port: Number(port),
    token,
    fp,
    pairingUri,
    name,
    updatedAt: new Date().toISOString(),
    protocol: 'https'
  }
  if (gateway) {
    doc.gateway = {
      url: String(gateway.url ?? ''),
      online: gateway.online === true,
      fp: gateway.fp ?? null,
      usable: gateway.usable !== false,
      ...(gateway.name ? { name: String(gateway.name) } : {}),
      ...(gateway.error ? { error: String(gateway.error) } : {})
    }
  }
  if (gatewayMode) {
    doc.mode = 'gateway'
  }
  if (localHost && Number(localPort) > 0) {
    doc.localHost = localHost
    doc.localPort = Number(localPort)
    doc.localPairingUri = buildPairingUri({ host: localHost, port: localPort, token: localToken ?? token, fp: localFp ?? fp, name })
    doc.localFp = String(localFp ?? fp)
  }
  if (relayVersion) doc.relayVersion = String(relayVersion)
  if (dshVersion) doc.dshVersion = String(dshVersion)
  if (capabilities && typeof capabilities === 'object') doc.capabilities = capabilities
  if (download && typeof download === 'object') doc.download = download
  if (probe && typeof probe === 'object') {
    doc.probe = {
      ok: probe.ok === true,
      checkedAt: probe.checkedAt ?? new Date().toISOString(),
      missing: Array.isArray(probe.missing) ? probe.missing : [],
      errors: Array.isArray(probe.errors) ? probe.errors : [],
      sessionSample: probe.sessionSample ?? null
    }
  }
  if (error) doc.error = error
  if (Number.isFinite(Number(loopbackPort))) doc.loopbackPort = Number(loopbackPort)
  if (Number.isFinite(Number(upstreamPort))) doc.upstreamPort = Number(upstreamPort)
  return doc
}

/**
 * Atomic write: temp file in the same directory, then rename over the target.
 * A reader never observes a half-written pairing document.
 */
export function writePairingFile (doc, file = pairingFilePath()) {
  const dir = path.dirname(file)
  fs.mkdirSync(dir, { recursive: true })
  const tmp = path.join(dir, `.pairing.json.tmp-${process.pid}-${crypto.randomBytes(4).toString('hex')}`)
  const body = `${JSON.stringify(doc, null, 2)}\n`
  fs.writeFileSync(tmp, body, { encoding: 'utf8', mode: 0o600 })
  fs.renameSync(tmp, file)
  return file
}




