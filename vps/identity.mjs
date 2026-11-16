// Gateway identity: the public, secret-free description a phone (or an
// operator script) can use to pin the gateway certificate and discover the
// number of online desktops.
import crypto from 'node:crypto'
import fs from 'node:fs'
import path from 'node:path'

/** Read `version` from the package.json next to gateway.mjs. */
export function readVersion (pkgRoot) {
  try {
    const raw = fs.readFileSync(path.join(pkgRoot, 'package.json'), 'utf8')
    const parsed = JSON.parse(raw)
    return typeof parsed.version === 'string' ? parsed.version : '0.0.0'
  } catch {
    return '0.0.0'
  }
}

/**
 * SPKI SHA-256 fingerprint (lowercase hex) from a PEM certificate: sha256 over
 * the DER-encoded SubjectPublicKeyInfo, exactly what the phone app pins.
 */
export function spkiFingerprintFromPem (pem) {
  const certificate = new crypto.X509Certificate(pem)
  const spki = certificate.publicKey.export({ type: 'spki', format: 'der' })
  return crypto.createHash('sha256').update(spki).digest('hex')
}

/** SPKI fingerprint of a PEM cert file on disk; throws with a readable message. */
export function fingerprintFromFile (certFile) {
  let pem
  try {
    pem = fs.readFileSync(certFile)
  } catch (error) {
    throw new Error(
      `dsh-remote-gateway: cannot read TLS certificate ${certFile}: ` +
      `${error && error.code ? error.code : ''} ${error && error.message ? error.message : String(error)}`
    )
  }
  try {
    return spkiFingerprintFromPem(pem)
  } catch (error) {
    throw new Error(`dsh-remote-gateway: invalid PEM certificate ${certFile}: ${error && error.message ? error.message : String(error)}`)
  }
}

/**
 * Public identity document for `GET /__gateway/identity`.
 *
 * Contract (M6): `{version, name:'DSH Remote Gateway', fp, tunnels, url}`.
 * Extra, equally non-secret fields (node/uptimeSec/download/resources) are
 * additive and safe for ops tooling; no token, cookie, desktop launch token or
 * absolute directory path leaks.
 */
export function buildIdentity ({ version, name, fp, tunnels, url, node, uptimeSec, download, resources }) {
  const identity = { version, name, fp, tunnels, url, node, uptimeSec }
  if (download) identity.download = download
  if (resources) identity.resources = resources
  return identity
}
