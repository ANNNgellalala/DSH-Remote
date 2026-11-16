// Gateway identity: the per-desktop remote token + desktop name persisted at
// ~/.dsh/remote/identity.json.
//
// The token is created once (32 lowercase hex = 16 random bytes), survives
// relay reloads and desktop restarts, and is the only credential the public
// gateway needs to route a phone to this desktop. Rotation rewrites the file
// atomically; the connector reconnects with the new token immediately.
import crypto from 'node:crypto'
import fs from 'node:fs'
import os from 'node:os'
import path from 'node:path'

export const IDENTITY_VERSION = 1
export const TOKEN_PATTERN = /^[0-9a-f]{32}$/

export function identityFilePath () {
  return path.join(os.homedir(), '.dsh', 'remote', 'identity.json')
}

export function generateToken () {
  return crypto.randomBytes(16).toString('hex')
}

export function maskToken (token) {
  const text = String(token ?? '')
  if (text.length <= 8) return '****'
  return `${text.slice(0, 4)}…${text.slice(-4)}`
}

function readFile (file) {
  try {
    const parsed = JSON.parse(fs.readFileSync(file, 'utf8'))
    if (parsed && typeof parsed === 'object') return parsed
  } catch {
    // missing/corrupt identity is recreated below
  }
  return undefined
}

function writeFile (file, identity) {
  const dir = path.dirname(file)
  fs.mkdirSync(dir, { recursive: true })
  const tmp = path.join(dir, `.identity.json.tmp-${process.pid}-${crypto.randomBytes(4).toString('hex')}`)
  fs.writeFileSync(tmp, `${JSON.stringify(identity, null, 2)}\n`, { encoding: 'utf8', mode: 0o600 })
  fs.renameSync(tmp, file)
  return file
}

function normalize (raw, name) {
  const token = typeof raw?.token === 'string' && TOKEN_PATTERN.test(raw.token) ? raw.token : generateToken()
  const now = new Date().toISOString()
  return {
    version: IDENTITY_VERSION,
    token,
    name: typeof raw?.name === 'string' && raw.name.length > 0 ? raw.name : (name ?? os.hostname()),
    createdAt: typeof raw?.createdAt === 'string' ? raw.createdAt : now,
    updatedAt: typeof raw?.updatedAt === 'string' ? raw.updatedAt : now,
    ...(typeof raw?.rotatedAt === 'string' ? { rotatedAt: raw.rotatedAt } : {})
  }
}

/**
 * Load the identity, creating it (and its token) when missing. A configured
 * token overrides the stored one and is persisted so restarts stay stable.
 * @returns {{ identity: object, file: string, created: boolean, tokenFromConfig: boolean }}
 */
export function ensureIdentity ({ file, token, name } = {}) {
  const target = file ?? identityFilePath()
  const existing = readFile(target)
  const configToken = typeof token === 'string' && token.length > 0 ? token.trim().toLowerCase() : undefined
  if (configToken !== undefined && !TOKEN_PATTERN.test(configToken)) {
    throw new Error('dsh-remote-relay: gateway.token must be 32 lowercase hex characters')
  }
  if (!existing && !configToken) {
    const created = normalize(undefined, name)
    writeFile(target, created)
    return { identity: created, file: target, created: true, tokenFromConfig: false }
  }
  const normalized = normalize(existing, name)
  if (configToken && normalized.token !== configToken) {
    normalized.token = configToken
    normalized.updatedAt = new Date().toISOString()
    writeFile(target, normalized)
    return { identity: normalized, file: target, created: false, tokenFromConfig: true }
  }
  if (!existing) writeFile(target, normalized)
  return { identity: normalized, file: target, created: !existing, tokenFromConfig: false }
}

/** Rotate the remote token, persist it and return the new identity. */
export function rotateIdentity ({ file, name } = {}) {
  const target = file ?? identityFilePath()
  const existing = normalize(readFile(target), name)
  const now = new Date().toISOString()
  const rotated = {
    ...existing,
    token: generateToken(),
    updatedAt: now,
    rotatedAt: now
  }
  writeFile(target, rotated)
  return { identity: rotated, file: target }
}

