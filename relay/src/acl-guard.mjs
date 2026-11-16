// Windows workspace ACL admission guard (dsh-remote-relay submodule).
//
// Goal: guarantee that a *new* DSH session can write inside its workspace under
// the `workspace-write` sandbox, without waiting for the agent to hit a denial
// and asking the user to approve a one-shot `icacls` repair.
//
// Why a guard is enough: DSH's sandbox provisioning (`grantWrite`) needs the
// workspace root to grant the current user WRITE_OWNER (the Low integrity label
// lives in the SACL, and Windows only grants an owner implicit READ_CONTROL and
// WRITE_DAC). When that right is missing, PROVISIONING itself fails with
// `SetNamedSecurityInfoW failed (Win32 5)` and the whole session can never
// write. The guard therefore only ensures the root carries an inheritable
// FullControl ACE for the current user; DSH keeps owning the capability ACE and
// the Low label.
//
// Mode A (default): applying an inheritable ACE makes Windows propagate it to
// the workspace's existing children, which repairs the previously damaged
// objects of *that* workspace as a side effect. Workspaces that no new session
// uses are never touched.
//
// The guard runs inside the DSH host process (the user's own token, not the
// restricted sandbox token), so it needs no approval prompt.
//
// This module is deliberately dependency-free (node: builtins only) because it
// ships as part of the relay plugin.

import { createHash } from 'node:crypto'
import { execFile } from 'node:child_process'
import fs from 'node:fs'
import os from 'node:os'
import path from 'node:path'

/** Bumping this invalidates every persisted admission cache entry. */
export const GUARD_VERSION = 1

/** SDDL right string for `FILE_ALL_ACCESS`. */
const FILE_ALL_ACCESS = 0x1f01ff

/** Default configuration; every value can be overridden by the plugin config. */
export const DEFAULT_OPTIONS = {
  /** false turns the guard into a no-op (host may be non-Windows or opted out). */
  enabled: true,
  /** How long `tools/pre-execute` may wait for a first-time preparation. */
  waitMs: 30_000,
  /** Timeout of one PowerShell probe. */
  probeTimeoutMs: 8_000,
  /** Timeout of one `icacls` repair (propagation on a large tree can be slow). */
  repairTimeoutMs: 120_000,
  /** Persisted cache validity; after this the next new session re-probes. */
  cacheTtlHours: 24,
  /** Mode A: let Windows propagate the inherited ACE to the workspace's children. */
  propagate: true,
  /** Roots never touched (case-insensitive prefix match). */
  excludedPrefixes: []
}

const SYSTEM_PREFIXES = [
  'c:\\windows',
  'c:\\program files',
  'c:\\program files (x86)',
  'c:\\programdata'
]

/** One-line logger shape shared with the relay's `log`. */
function noop () {}

/**
 * Quote one PowerShell single-quoted literal.
 * @param {string} value
 * @returns {string}
 */
export function psQuote (value) {
  return `'${String(value).replace(/'/g, "''")}'`
}

/**
 * Canonical admission key for a workspace root. Uses realpath so junctions and
 * symlinks resolve to the object whose ACL is actually written.
 * @param {string} root - absolute workspace root (session header cwd).
 * @returns {string}
 */
export function normalizeRootKey (root) {
  const absolute = path.resolve(String(root))
  try {
    return fs.realpathSync.native(absolute)
  } catch {
    return absolute
  }
}

/**
 * Decide whether a root must be skipped because the guard cannot (or must not)
 * touch it: UNC/network paths and protected system directories.
 * @param {string} root - canonical root.
 * @param {{ excludedPrefixes?: string[] }} [options]
 * @returns {{ skip: boolean, reason?: string }}
 */
export function shouldSkipRoot (root, options = {}) {
  const value = String(root)
  if (value.startsWith('\\\\')) return { skip: true, reason: 'unc-path' }
  const lower = value.toLowerCase()
  const excluded = [...SYSTEM_PREFIXES, ...(options.excludedPrefixes ?? []).map((p) => String(p).toLowerCase())]
  for (const prefix of excluded) {
    if (!prefix) continue
    if (lower === prefix || lower.startsWith(prefix.endsWith('\\') ? prefix : `${prefix}\\`)) {
      return { skip: true, reason: `excluded:${prefix}` }
    }
  }
  return { skip: false }
}

/**
 * Parse the rights field of one SDDL ACE into a numeric access mask.
 * @param {string} rights - e.g. `FA`, `0x110156`, `FA,` or generic forms.
 * @returns {number | null}
 */
function parseRights (rights) {
  if (!rights) return null
  if (rights === 'FA') return FILE_ALL_ACCESS
  if (rights === 'GA') return 0x10000000
  if (rights.startsWith('0x') || rights.startsWith('0X')) {
    const parsed = Number.parseInt(rights, 16)
    return Number.isFinite(parsed) ? parsed : null
  }
  return null
}

/**
 * Split an SDDL string into its ACE field arrays. Handles the DACL section only
 * (`D:...`) and tolerates the `AI`/`AR`/`P` control flags that prefix it.
 * @param {string} sddl
 * @returns {string[][]} ACE fields (`type;flags;rights;object;inherit;sid`)
 */
export function splitAces (sddl) {
  const aces = []
  const text = String(sddl ?? '')
  for (const match of text.matchAll(/\(([^()]*)\)/g)) {
    const fields = match[1].split(';')
    if (fields.length >= 6) aces.push(fields)
  }
  return aces
}

/**
 * Inspect one workspace root's SDDL and report the facts the guard needs.
 *
 * `hasUserFullInheritable` is the readiness condition: the current user owns an
 * allow ACE carrying FILE_ALL_ACCESS with container/object inheritance. That is
 * exactly what lets DSH write the Low label (WRITE_OWNER) and keep provisioning.
 *
 * `hasCapability`/`hasLowLabel`/`hasPackageAce` are informational: the guard
 * never writes them (DSH does), but they make the audit trail and the phone
 * status card actionable.
 *
 * @param {string} sddl - DACL SDDL from `(Get-Acl).Sddl`.
 * @param {string} userSid - current user SID.
 * @returns {{
 *   hasUserFullInheritable: boolean,
 *   hasUserFull: boolean,
 *   hasCapability: boolean,
 *   capabilitySids: string[],
 *   hasPackageAce: boolean,
 *   packageSids: string[],
 *   aceCount: number
 * }}
 */
export function parseSddl (sddl, userSid) {
  const aces = splitAces(sddl)
  const result = {
    hasUserFullInheritable: false,
    hasUserFull: false,
    hasCapability: false,
    capabilitySids: [],
    hasPackageAce: false,
    packageSids: [],
    aceCount: aces.length
  }
  for (const ace of aces) {
    const [type, flags = '', rights = '', , , sid = ''] = ace
    if (type === 'A' && sid === userSid) {
      const mask = parseRights(rights)
      const full = mask !== null && (mask & FILE_ALL_ACCESS) === FILE_ALL_ACCESS
      if (full) {
        result.hasUserFull = true
        const inheritedOnly = flags.includes('IO')
        const inheritable = (flags.includes('OI') || flags.includes('CI')) && !inheritedOnly
        if (inheritable) result.hasUserFullInheritable = true
      }
    }
    if (/^S-1-4-\d+-\d+/.test(sid) && type === 'A') {
      result.hasCapability = true
      if (!result.capabilitySids.includes(sid)) result.capabilitySids.push(sid)
    }
    if (type === 'A' && /^S-1-15-2-/.test(sid)) {
      result.hasPackageAce = true
      if (!result.packageSids.includes(sid)) result.packageSids.push(sid)
    }
  }
  return result
}

/**
 * Run one PowerShell command and return stdout.
 * @param {string} script
 * @param {number} timeoutMs
 * @returns {Promise<string>}
 */
function runPowerShell (script, timeoutMs) {
  const attempt = (program) => new Promise((resolve, reject) => {
    execFile(
      program,
      ['-NoProfile', '-NonInteractive', '-Command', script],
      { windowsHide: true, timeout: timeoutMs, maxBuffer: 4 * 1024 * 1024 },
      (error, stdout, stderr) => {
        if (error) {
          reject(new Error(`${program}: ${error.message}${stderr ? ` :: ${String(stderr).trim()}` : ''}`))
          return
        }
        resolve(String(stdout ?? ''))
      }
    )
  })
  return attempt('pwsh').catch(() => attempt('powershell.exe'))
}

/**
 * Real backend: probe through PowerShell, repair through `icacls`.
 * @param {{ probeTimeoutMs: number, repairTimeoutMs: number, log?: (line: string) => void }} options
 * @returns {import('./acl-guard.mjs').AclBackend}
 */
export function createWindowsBackend (options) {
  const timeout = options.probeTimeoutMs
  const log = options.log ?? noop

  return {
    /**
     * Read the root's SDDL, owner SID and reparse state.
     * @param {string} root
     */
    async probe (root) {
      const script = [
        "$ErrorActionPreference = 'Stop'",
        `$p = ${psQuote(root)}`,
        '$item = Get-Item -LiteralPath $p -Force -ErrorAction Stop',
        '$acl = Get-Acl -LiteralPath $p',
        '$sid = [System.Security.Principal.WindowsIdentity]::GetCurrent().User.Value',
        '[pscustomobject]@{ sid = $sid; sddl = $acl.Sddl; ' +
          'reparse = [bool]($item.Attributes -band [System.IO.FileAttributes]::ReparsePoint); ' +
          'fileSystem = $(try { $item.PSDrive.FileSystem } catch { $null }) } | ConvertTo-Json -Compress'
      ].join('\n')
      const stdout = await runPowerShell(script, timeout)
      const parsed = JSON.parse(stdout)
      if (!parsed || typeof parsed.sddl !== 'string' || typeof parsed.sid !== 'string') {
        throw new Error('probe returned an unexpected payload')
      }
      return {
        ok: true,
        root,
        userSid: parsed.sid,
        sddl: parsed.sddl,
        reparse: parsed.reparse === true,
        fileSystem: parsed.fileSystem ?? null
      }
    },

    /**
     * Add an inheritable FullControl ACE for the current user.
     * Mode A (`propagate`) is what `icacls` does by default: the ACE is also
     * written into the existing children.
     * @param {string} root
     * @param {{ userSid: string }} facts
     */
    async repair (root, facts) {
      const grant = `*${facts.userSid}:(OI)(CI)F`
      const stdout = await new Promise((resolve, reject) => {
        execFile(
          'icacls',
          [root, '/grant', grant],
          { windowsHide: true, timeout: options.repairTimeoutMs, maxBuffer: 4 * 1024 * 1024 },
          (error, out, stderr) => {
            if (error) {
              reject(new Error(`icacls: ${error.message}${stderr ? ` :: ${String(stderr).trim()}` : ''}`))
              return
            }
            resolve(String(out ?? ''))
          }
        )
      })
      // icacls reports per-object failures in stdout while still exiting 0 in a
      // few propagation cases; keep the text for the audit trail.
      if (/failed processing/i.test(stdout) || /Access is denied/i.test(stdout)) {
        log(`[remote-relay] acl-guard: icacls reported partial failures for ${root}`)
      }
      return { stdout: stdout.trim() }
    }
  }
}

/**
 * Resolve the default guard state files under `~/.dsh/remote`.
 * @param {string} [home]
 */
export function defaultGuardPaths (home = os.homedir()) {
  const dir = path.join(home, '.dsh', 'remote')
  return {
    dir,
    cacheFile: path.join(dir, 'acl-admission.json'),
    auditFile: path.join(dir, 'acl-audit.jsonl')
  }
}

/**
 * Read the persisted admission cache.
 * @param {string} cacheFile
 * @returns {Record<string, { preparedAt: string, fingerprint: string, version: number }>}
 */
function readCache (cacheFile) {
  try {
    const raw = JSON.parse(fs.readFileSync(cacheFile, 'utf8'))
    if (raw && raw.version === GUARD_VERSION && raw.entries && typeof raw.entries === 'object') {
      return raw.entries
    }
  } catch {}
  return {}
}

/**
 * Persist the admission cache atomically.
 * @param {string} cacheFile
 * @param {Record<string, object>} entries
 */
function writeCache (cacheFile, entries) {
  try {
    fs.mkdirSync(path.dirname(cacheFile), { recursive: true })
    const payload = JSON.stringify({ version: GUARD_VERSION, updatedAt: new Date().toISOString(), entries }, null, 2)
    const tmp = `${cacheFile}.${process.pid}.tmp`
    fs.writeFileSync(tmp, payload, 'utf8')
    try {
      fs.renameSync(tmp, cacheFile)
    } catch {
      fs.rmSync(cacheFile, { force: true })
      fs.renameSync(tmp, cacheFile)
    }
  } catch {}
}

/**
 * Append one audit record (JSON Lines). Never throws.
 * @param {string} auditFile
 * @param {object} record
 */
function appendAudit (auditFile, record) {
  try {
    fs.mkdirSync(path.dirname(auditFile), { recursive: true })
    fs.appendFileSync(auditFile, `${JSON.stringify(record)}\n`, 'utf8')
  } catch {}
}

/**
 * @typedef {object} AclBackend
 * @property {(root: string) => Promise<{ ok: true, root: string, userSid: string, sddl: string, reparse: boolean, fileSystem?: string | null }>} probe
 * @property {(root: string, facts: { userSid: string }) => Promise<{ stdout?: string }>} repair
 */

/**
 * Create the workspace admission guard.
 *
 * The guard is fail-safe by construction: every public method resolves, never
 * rejects, so a hook can `await` it without risking the host's tool pipeline.
 *
 * @param {{
 *   log?: (line: string) => void,
 *   options?: Partial<typeof DEFAULT_OPTIONS>,
 *   backend?: AclBackend,
 *   paths?: { cacheFile: string, auditFile: string },
 *   platform?: string
 * }} [config]
 */
export function createAclGuard (config = {}) {
  const log = config.log ?? noop
  const options = { ...DEFAULT_OPTIONS, ...(config.options ?? {}) }
  const platform = config.platform ?? process.platform
  const paths = config.paths ?? defaultGuardPaths()
  const disabled = !options.enabled || platform !== 'win32' || options.waitMs <= 0
  const backend = config.backend ?? (disabled ? null : createWindowsBackend({ ...options, log }))
  if (!disabled && options.propagate === false) {
    // Mode B (root-only, no propagation) needs a two-step low-level DACL write;
    // today the guard implements mode A only. Say so instead of silently
    // behaving differently from the configured mode.
    log('[remote-relay] acl-guard: propagate=false is not implemented yet; using mode A (inheritance propagates)')
  }

  /** @type {Map<string, { ready: boolean, reason?: string }>} */
  const memory = new Map()
  /** @type {Map<string, Promise<{ ready: boolean, reason?: string }>>} */
  const pending = new Map()
  /** @type {Map<string, number>} last forced re-check timestamp per root */
  const forcedAt = new Map()
  /** @type {Map<string, string>} raw cwd -> canonical key (avoids a realpath per tool call) */
  const keyCache = new Map()
  /** @type {Map<string, number>} every root the host has asked about, with its last-seen time */
  const seen = new Map()
  let entries = disabled ? {} : readCache(paths.cacheFile)
  const stats = { prepared: 0, repaired: 0, failed: 0, skipped: 0, probeErrors: 0, timeouts: 0 }

  const isFresh = (entry) => {
    if (!entry || entry.version !== GUARD_VERSION) return false
    const at = Date.parse(entry.preparedAt)
    if (!Number.isFinite(at)) return false
    return Date.now() - at < options.cacheTtlHours * 3600_000
  }

  const markReady = (key, fingerprint) => {
    memory.set(key, { ready: true })
    entries = { ...entries, [key]: { preparedAt: new Date().toISOString(), fingerprint, version: GUARD_VERSION } }
    writeCache(paths.cacheFile, entries)
  }

  const audit = (record) => appendAudit(paths.auditFile, { at: new Date().toISOString(), guardVersion: GUARD_VERSION, ...record })

  /**
   * Canonical root key, memoized per raw cwd (the hook runs on every tool call).
   * @param {string} cwd
   * @returns {string}
   */
  const resolveKey = (cwd) => {
    const cached = keyCache.get(cwd)
    if (cached) return cached
    const key = normalizeRootKey(cwd)
    keyCache.set(cwd, key)
    return key
  }

  /**
   * Record that a root is in active use (drives the phone status list and
   * bounds which roots the status API will touch).
   * @param {string} key
   */
  const remember = (key) => {
    seen.set(key, Date.now())
  }

  /**
   * One preparation attempt: probe → classify → (repair → verify).
   * @param {string} key
   * @returns {Promise<{ ready: boolean, reason?: string }>}
   */
  async function prepare (key) {
    const startedAt = Date.now()
    if (!backend) return { ready: true, reason: 'disabled' }
    let facts
    try {
      facts = await backend.probe(key)
    } catch (error) {
      stats.probeErrors += 1
      const reason = `probe-failed:${error?.message ?? error}`
      memory.set(key, { ready: false, reason })
      audit({ event: 'probe-failed', root: key, reason })
      return { ready: false, reason }
    }
    if (facts.reparse) {
      stats.skipped += 1
      memory.set(key, { ready: false, reason: 'reparse-root' })
      audit({ event: 'skip', root: key, reason: 'reparse-root' })
      return { ready: false, reason: 'reparse-root' }
    }
    const classified = parseSddl(facts.sddl, facts.userSid)
    if (classified.hasUserFullInheritable) {
      markReady(key, fingerprintOf(facts))
      stats.prepared += 1
      audit({
        event: 'ready',
        root: key,
        action: 'none',
        durationMs: Date.now() - startedAt,
        capability: classified.hasCapability,
        packageAce: classified.hasPackageAce
      })
      return { ready: true }
    }
    try {
      await backend.repair(key, facts)
      stats.repaired += 1
    } catch (error) {
      stats.failed += 1
      const reason = `repair-failed:${error?.message ?? error}`
      memory.set(key, { ready: false, reason })
      audit({ event: 'repair-failed', root: key, reason, durationMs: Date.now() - startedAt })
      return { ready: false, reason }
    }
    // Verify, never trust icacls' exit code alone.
    let after
    try {
      after = await backend.probe(key)
    } catch (error) {
      stats.probeErrors += 1
      const reason = `verify-probe-failed:${error?.message ?? error}`
      memory.set(key, { ready: false, reason })
      audit({ event: 'verify-failed', root: key, reason })
      return { ready: false, reason }
    }
    const verified = parseSddl(after.sddl, after.userSid)
    const ready = verified.hasUserFullInheritable
    if (ready) {
      markReady(key, fingerprintOf(after))
    } else {
      stats.failed += 1
      memory.set(key, { ready: false, reason: 'repair-unverified' })
    }
    audit({
      event: ready ? 'repaired' : 'repair-unverified',
      root: key,
      durationMs: Date.now() - startedAt,
      capability: verified.hasCapability,
      packageAce: verified.hasPackageAce
    })
    return ready ? { ready: true } : { ready: false, reason: 'repair-unverified' }
  }

  /**
   * Ensure one workspace root is ready for DSH provisioning.
   *
   * Never rejects. Returns as soon as the preparation settles or `waitMs`
   * elapses; a timed-out preparation keeps running in the background so the
   * next tool call finds it completed.
   *
   * @param {string} cwd - the session's workspace root.
   * @param {{ waitMs?: number, force?: boolean }} [overrides]
   * @returns {Promise<{ ready: boolean, reason?: string, pending?: boolean, skipped?: boolean, cached?: boolean }>}
   */
  async function ensureReady (cwd, overrides = {}) {
    if (disabled) return { ready: true, reason: 'disabled', skipped: true }
    if (typeof cwd !== 'string' || !cwd) return { ready: true, reason: 'no-cwd', skipped: true }
    let key
    try {
      key = resolveKey(cwd)
    } catch {
      return { ready: true, reason: 'uncanonical', skipped: true }
    }
    const exclusion = shouldSkipRoot(key, options)
    if (exclusion.skip) {
      stats.skipped += 1
      audit({ event: 'skip', root: key, reason: exclusion.reason })
      return { ready: true, reason: exclusion.reason, skipped: true }
    }
    remember(key)

    const remembered = memory.get(key)
    if (remembered?.ready && !overrides.force) return { ready: true, cached: true }
    if (overrides.force && remembered) memory.delete(key)
    if (remembered && !remembered.ready && !overrides.force) {
      // A previous attempt failed; do not hammer it, let DSH surface the error.
      return { ready: false, reason: remembered.reason }
    }

    let task = pending.get(key)
    if (!task) {
      const cacheEntry = entries[key]
      if (!overrides.force && isFresh(cacheEntry)) {
        memory.set(key, { ready: true })
        return { ready: true, cached: true }
      }
      task = prepare(key).catch((error) => ({ ready: false, reason: `unexpected:${error?.message ?? error}` }))
      pending.set(key, task)
      task.finally(() => {
        if (pending.get(key) === task) pending.delete(key)
      })
    }

    const waitMs = overrides.waitMs ?? options.waitMs
    let timer
    const timeout = new Promise((resolve) => {
      timer = setTimeout(() => resolve({ ready: false, pending: true, reason: 'timeout' }), waitMs)
      timer.unref?.()
    })
    try {
      const settled = await Promise.race([task, timeout])
      if (settled && settled.pending) {
        stats.timeouts += 1
        audit({ event: 'timeout', root: key, waitMs })
        log(`[remote-relay] acl-guard: ${key} admission still running after ${waitMs} ms; continuing in background`)
      }
      return settled
    } finally {
      if (timer) clearTimeout(timer)
    }
  }

  /**
   * Fire-and-forget warm-up used from `session/created`. A stale persisted
   * entry is re-probed here so an externally changed ACL cannot hide behind the
   * process-lifetime memory cache.
   * @param {string} cwd
   */
  function warm (cwd) {
    if (disabled) return
    let key
    try {
      key = resolveKey(cwd)
    } catch {
      return
    }
    if (shouldSkipRoot(key, options).skip) return
    remember(key)
    const entry = entries[key]
    const force = !isFresh(entry)
    ensureReady(key, { waitMs: 0, force }).catch(() => {})
  }

  /**
   * Feed one tool result back to the guard. Known ACL failure signatures force a
   * single re-check (rate limited) so the next attempt sees the repaired root.
   * @param {string} cwd
   * @param {string} text - rendered tool result text.
   */
  function noteInstrumentFailure (cwd, text) {
    if (disabled || typeof cwd !== 'string' || !cwd || typeof text !== 'string' || !text) return
    if (!/grantWrite|SANDBOX_UNAVAILABLE|SetNamedSecurityInfo|windows-acl-run|sandbox: file access denied|sandbox: the sandbox runner itself failed|Access is denied/i.test(text)) return
    let key
    try {
      key = resolveKey(cwd)
    } catch {
      return
    }
    const last = forcedAt.get(key) ?? 0
    if (Date.now() - last < 5 * 60_000) return
    forcedAt.set(key, Date.now())
    log(`[remote-relay] acl-guard: ACL failure observed under ${key}; re-checking workspace admission`)
    ensureReady(key, { waitMs: 0, force: true }).catch(() => {})
  }

  /**
   * Probe-only health check of one root: never writes ACLs. Backs the phone's
   * "一键体检" (report mode) and the per-root detail row.
   * @param {string} cwd
   * @returns {Promise<{ root: string, ready: boolean, needsRepair?: boolean, skipped?: boolean, reason?: string, hasCapability?: boolean, packageAce?: boolean }>}
   */
  async function inspect (cwd) {
    if (disabled) return { root: String(cwd ?? ''), ready: true, skipped: true, reason: 'disabled' }
    if (typeof cwd !== 'string' || !cwd) return { root: '', ready: true, skipped: true, reason: 'no-cwd' }
    let key
    try {
      key = resolveKey(cwd)
    } catch {
      return { root: cwd, ready: false, skipped: true, reason: 'uncanonical' }
    }
    const exclusion = shouldSkipRoot(key, options)
    if (exclusion.skip) return { root: key, ready: true, skipped: true, reason: exclusion.reason }
    remember(key)
    if (!backend) return { root: key, ready: true, skipped: true, reason: 'no-backend' }
    try {
      const facts = await backend.probe(key)
      if (facts.reparse) return { root: key, ready: false, skipped: true, reason: 'reparse-root' }
      const classified = parseSddl(facts.sddl, facts.userSid)
      return {
        root: key,
        ready: classified.hasUserFullInheritable,
        needsRepair: !classified.hasUserFullInheritable,
        hasCapability: classified.hasCapability,
        packageAce: classified.hasPackageAce
      }
    } catch (error) {
      stats.probeErrors += 1
      return { root: key, ready: false, reason: `probe-failed:${error?.message ?? error}` }
    }
  }

  /**
   * Every root the host has asked about plus every persisted entry, with its
   * last known admission state. The status API only ever touches these roots.
   * @returns {Array<{ root: string, state: string, preparedAt: string | null, lastSeenAt: string | null }>}
   */
  function knownRoots () {
    const keys = new Set([...seen.keys(), ...Object.keys(entries)])
    return [...keys].map((root) => {
      const entry = entries[root]
      const memo = memory.get(root)
      const state = memo?.ready === false
        ? 'failed'
        : memo?.ready
          ? 'ready'
          : isFresh(entry)
            ? 'ready'
            : entry
              ? 'stale'
              : 'unknown'
      return {
        root,
        state,
        preparedAt: entry?.preparedAt ?? null,
        lastSeenAt: seen.has(root) ? new Date(seen.get(root)).toISOString() : null
      }
    })
  }

  /**
   * Snapshot for the phone status card (wired in a later round).
   * @returns {object}
   */
  function status () {
    return {
      version: GUARD_VERSION,
      enabled: !disabled,
      options: { waitMs: options.waitMs, cacheTtlHours: options.cacheTtlHours, propagate: options.propagate },
      stats: { ...stats },
      roots: Object.fromEntries(
        Object.entries(entries).map(([key, entry]) => [key, {
          preparedAt: entry.preparedAt,
          fingerprint: entry.fingerprint,
          state: memory.get(key)?.ready === false ? 'failed' : isFresh(entry) ? 'ready' : 'stale'
        }])
      )
    }
  }

  /** Flush in-memory state and drop pending references. */
  function dispose () {
    for (const task of pending.values()) task.catch?.(() => {})
    pending.clear()
    memory.clear()
    writeCache(paths.cacheFile, entries)
  }

  return {
    disabled,
    options,
    ensureReady,
    inspect,
    warm,
    knownRoots,
    noteInstrumentFailure,
    status,
    dispose,
    /** Test seam: the persistence path in use. */
    paths
  }
}

/**
 * Stable fingerprint of one probed root (used to invalidate the persisted cache
 * when the root ACL changes).
 * @param {{ sddl: string, userSid: string }} facts
 * @returns {string}
 */
export function fingerprintOf (facts) {
  return createHash('sha256')
    .update(`${GUARD_VERSION}\u0000${facts.userSid}\u0000${facts.sddl}`)
    .digest('hex')
    .slice(0, 32)
}
