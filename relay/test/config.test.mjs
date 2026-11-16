// Unit tests for the persistent relay configuration (node:test, no deps).
//
// Run: node --test relay/test/   (or: cd relay && npm test)
import assert from 'node:assert/strict'
import fs from 'node:fs'
import os from 'node:os'
import path from 'node:path'
import test from 'node:test'
import {
  CONFIG_VERSION,
  DEFAULT_GATEWAY_PATH,
  emptyRelayConfig,
  gatewayIdentityPath,
  gatewaySummary,
  gatewayUrlFor,
  normalizeFingerprint,
  normalizeGatewayPath,
  normalizeGatewaySettings,
  normalizeHost,
  normalizePort,
  readRelayConfig,
  relayConfigFilePath,
  writeRelayConfigFile
} from '../src/config.mjs'

const FP = '0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef'

function tempDir () {
  return fs.mkdtempSync(path.join(os.tmpdir(), 'dsh-relay-config-test-'))
}

test('normalizeHost accepts IPs, hostnames and pasted URLs', () => {
  assert.equal(normalizeHost('203.0.113.10'), '203.0.113.10')
  assert.equal(normalizeHost('  203.0.113.10 '), '203.0.113.10')
  assert.equal(normalizeHost('Example.COM.'), 'example.com')
  assert.equal(normalizeHost('dsh.example.com'), 'dsh.example.com')
  assert.equal(normalizeHost('https://relay.example.com:9443/__gateway/tunnel'), 'relay.example.com')
  assert.equal(normalizeHost('relay.example.com:9443'), 'relay.example.com')
  assert.equal(normalizeHost('[2001:db8::1]'), '2001:db8::1')
  assert.equal(normalizeHost('2001:DB8::1'), '2001:db8::1')
  assert.equal(normalizeHost('::1'), '::1')
})

test('normalizeHost rejects empty and malformed values', () => {
  assert.throws(() => normalizeHost(''), /required/)
  assert.throws(() => normalizeHost(null), /required/)
  assert.throws(() => normalizeHost('not a host'), /invalid/)
  assert.throws(() => normalizeHost('-bad.example.com'), /invalid/)
  assert.throws(() => normalizeHost('a'.repeat(200)), /invalid/)
})

test('normalizePort defaults to 9443 and validates the range', () => {
  assert.equal(normalizePort(undefined), 9443)
  assert.equal(normalizePort(''), 9443)
  assert.equal(normalizePort('9443'), 9443)
  assert.equal(normalizePort(1), 1)
  assert.equal(normalizePort(65535), 65535)
  assert.throws(() => normalizePort(0), /invalid gateway.port/)
  assert.throws(() => normalizePort(65536), /invalid gateway.port/)
  assert.throws(() => normalizePort('abc'), /invalid gateway.port/)
  assert.throws(() => normalizePort(1.5), /invalid gateway.port/)
})

test('normalizeGatewayPath keeps an optional route endpoint', () => {
  assert.equal(normalizeGatewayPath(undefined), DEFAULT_GATEWAY_PATH)
  assert.equal(normalizeGatewayPath(''), DEFAULT_GATEWAY_PATH)
  assert.equal(normalizeGatewayPath('/__gateway/tunnel'), '/__gateway/tunnel')
  assert.equal(normalizeGatewayPath('__gateway/tunnel'), '/__gateway/tunnel')
  assert.equal(normalizeGatewayPath('/__gateway/tunnel/'), '/__gateway/tunnel')
  assert.equal(normalizeGatewayPath('/dsh//__gateway/tunnel'), '/dsh/__gateway/tunnel')
  assert.equal(normalizeGatewayPath('/dsh/relay'), '/dsh/relay')
  assert.throws(() => normalizeGatewayPath('/__gateway/tunnel?x=1'), /invalid gateway.path/)
  assert.throws(() => normalizeGatewayPath('/a b'), /invalid gateway.path/)
  assert.throws(() => normalizeGatewayPath('/a#b'), /invalid gateway.path/)
})

test('normalizeFingerprint accepts colon separated hex and lowercases', () => {
  const spaced = '01:23:45:67:89:AB:CD:EF:01:23:45:67:89:AB:CD:EF:01:23:45:67:89:AB:CD:EF:01:23:45:67:89:AB:CD:EF'
  assert.equal(normalizeFingerprint(spaced.toLowerCase()), FP)
  assert.equal(normalizeFingerprint(FP.toUpperCase()), FP)
  assert.equal(normalizeFingerprint(''), null)
  assert.equal(normalizeFingerprint(null), null)
  assert.throws(() => normalizeFingerprint('abc'), /invalid gateway.fingerprint/)
})

test('gatewayUrlFor builds wss/ws URLs including IPv6', () => {
  assert.equal(
    gatewayUrlFor({ host: '203.0.113.10', port: 9443, path: '/__gateway/tunnel' }),
    'wss://203.0.113.10:9443/__gateway/tunnel'
  )
  assert.equal(
    gatewayUrlFor({ host: 'relay.example.com', port: 443, secure: false }),
    'ws://relay.example.com:443/__gateway/tunnel'
  )
  assert.equal(
    gatewayUrlFor({ host: '2001:db8::1', port: 9443, path: '/dsh/relay' }),
    'wss://[2001:db8::1]:9443/dsh/relay'
  )
})

test('gatewayIdentityPath mirrors the tunnel endpoint', () => {
  assert.equal(gatewayIdentityPath(undefined), '/__gateway/identity')
  assert.equal(gatewayIdentityPath('/__gateway/tunnel'), '/__gateway/identity')
  assert.equal(gatewayIdentityPath('/dsh/__gateway/tunnel'), '/dsh/__gateway/identity')
  assert.equal(gatewayIdentityPath('/dsh/relay'), '/dsh/relay/identity')
})

test('normalizeGatewaySettings requires a host only when enabled', () => {
  const disabled = normalizeGatewaySettings({ enabled: false })
  assert.equal(disabled.enabled, false)
  assert.equal(disabled.host, '')
  assert.equal(disabled.port, 9443)
  assert.equal(disabled.path, DEFAULT_GATEWAY_PATH)
  assert.equal(disabled.secure, true)
  assert.equal(disabled.fingerprint, null)

  const enabled = normalizeGatewaySettings({
    enabled: true,
    host: '203.0.113.10',
    port: '9443',
    path: '/__gateway/tunnel',
    fingerprint: FP
  })
  assert.equal(enabled.enabled, true)
  assert.equal(enabled.host, '203.0.113.10')
  assert.equal(enabled.port, 9443)
  assert.equal(enabled.path, '/__gateway/tunnel')
  assert.equal(enabled.fingerprint, FP)

  assert.throws(() => normalizeGatewaySettings({ enabled: true }), /requires gateway.host/)
  assert.equal(normalizeGatewaySettings({ host: 'example.com' }, { fallbackEnabled: true }).enabled, true)
})

test('gatewaySummary hides the shared secret and exposes the URL', () => {
  const summary = gatewaySummary({
    enabled: true,
    host: '203.0.113.10',
    port: 9443,
    path: '/__gateway/tunnel',
    secure: true,
    fingerprint: FP,
    name: 'Gateway',
    secret: 'shh'
  })
  assert.equal(summary.url, 'wss://203.0.113.10:9443/__gateway/tunnel')
  assert.equal(summary.secret, undefined)
  assert.equal(summary.secretConfigured, true)
  assert.equal(gatewaySummary(null).enabled, false)
})

test('readRelayConfig reports a missing file without throwing', () => {
  const dir = tempDir()
  try {
    const result = readRelayConfig(path.join(dir, 'relay-config.json'))
    assert.equal(result.exists, false)
    assert.equal(result.error, undefined)
  } finally {
    fs.rmSync(dir, { recursive: true, force: true })
  }
})

test('writeRelayConfigFile persists atomically and round-trips', () => {
  const dir = tempDir()
  const file = path.join(dir, 'nested', 'relay-config.json')
  try {
    const seededAt = '2026-11-10T00:00:00.000Z'
    const written = writeRelayConfigFile(file, {
      gateway: { enabled: true, host: '203.0.113.10', port: '9443', path: '/__gateway/tunnel', fingerprint: FP },
      initializedAt: seededAt,
      initializedBy: 'one-time-migration:profile-patch'
    })
    assert.equal(written.file, file)
    const text = fs.readFileSync(file, 'utf8')
    assert.match(text, new RegExp(`"version": ${CONFIG_VERSION}`))
    assert.match(text, /"enabled": true/)
    assert.equal(text.endsWith('\n'), true)
    const listed = fs.readdirSync(path.dirname(file))
    assert.deepEqual(listed, ['relay-config.json'], 'no tmp files left behind')

    const read = readRelayConfig(file)
    assert.equal(read.exists, true)
    assert.equal(read.error, undefined)
    assert.equal(read.gateway.enabled, true)
    assert.equal(read.gateway.host, '203.0.113.10')
    assert.equal(read.gateway.port, 9443)
    assert.equal(read.gateway.fingerprint, FP)
    assert.equal(read.initializedAt, seededAt)
    assert.equal(read.initializedBy, 'one-time-migration:profile-patch')
    assert.equal(read.updatedAt, written.document.updatedAt)

    // A second write keeps the one-time markers and flips the enable flag.
    writeRelayConfigFile(file, { gateway: { enabled: false, host: '203.0.113.10', port: 9443 } })
    const reread = readRelayConfig(file)
    assert.equal(reread.gateway.enabled, false)
    assert.equal(reread.initializedAt, seededAt)
    assert.equal(reread.initializedBy, 'one-time-migration:profile-patch')
  } finally {
    fs.rmSync(dir, { recursive: true, force: true })
  }
})

test('writeRelayConfigFile refuses invalid input (and leaves no file)', () => {
  const dir = tempDir()
  const file = path.join(dir, 'relay-config.json')
  try {
    assert.throws(
      () => writeRelayConfigFile(file, { gateway: { enabled: true, host: '' } }),
      /requires gateway.host/
    )
    assert.throws(
      () => writeRelayConfigFile(file, { gateway: { enabled: true, host: 'x', port: 0 } }),
      /invalid gateway.port/
    )
    assert.equal(fs.existsSync(file), false)
  } finally {
    fs.rmSync(dir, { recursive: true, force: true })
  }
})

test('readRelayConfig surfaces corrupt or invalid documents as errors', () => {
  const dir = tempDir()
  const corrupt = path.join(dir, 'corrupt.json')
  const invalid = path.join(dir, 'invalid.json')
  try {
    fs.writeFileSync(corrupt, '{ not json', 'utf8')
    assert.match(readRelayConfig(corrupt).error, /invalid JSON/)

    fs.writeFileSync(invalid, JSON.stringify({ version: 1, gateway: { enabled: true } }), 'utf8')
    assert.match(readRelayConfig(invalid).error, /invalid config.gateway/)

    fs.writeFileSync(invalid, JSON.stringify({ version: 1, gateway: { enabled: true, host: 'h', port: 99999 } }), 'utf8')
    assert.match(readRelayConfig(invalid).error, /invalid gateway.port/)
  } finally {
    fs.rmSync(dir, { recursive: true, force: true })
  }
})

test('emptyRelayConfig is a valid disabled document', () => {
  const doc = emptyRelayConfig()
  assert.equal(doc.version, CONFIG_VERSION)
  assert.equal(doc.gateway.enabled, false)
  assert.equal(doc.gateway.port, 9443)
  // It must be accepted by the normalizer (what the page/API would save).
  assert.equal(normalizeGatewaySettings(doc.gateway).enabled, false)
})

test('relayConfigFilePath honours overrides and DSH_HOME', () => {
  const originalHome = process.env.DSH_HOME
  const originalOverride = process.env.DSH_REMOTE_CONFIG
  try {
    const explicit = path.join(os.tmpdir(), 'explicit-relay-config.json')
    assert.equal(relayConfigFilePath(explicit), path.resolve(explicit))

    delete process.env.DSH_HOME
    delete process.env.DSH_REMOTE_CONFIG
    assert.ok(relayConfigFilePath().endsWith(path.join('.dsh', 'remote', 'relay-config.json')))

    process.env.DSH_HOME = path.join(os.tmpdir(), 'dsh-home-test')
    assert.equal(
      relayConfigFilePath(),
      path.join(path.resolve(process.env.DSH_HOME), 'remote', 'relay-config.json')
    )

    process.env.DSH_REMOTE_CONFIG = explicit
    assert.equal(relayConfigFilePath(), path.resolve(explicit))
  } finally {
    if (originalHome === undefined) delete process.env.DSH_HOME
    else process.env.DSH_HOME = originalHome
    if (originalOverride === undefined) delete process.env.DSH_REMOTE_CONFIG
    else process.env.DSH_REMOTE_CONFIG = originalOverride
  }
})
