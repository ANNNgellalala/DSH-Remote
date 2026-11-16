// Desktop-side auto-approval for the remote relay.
//
// The phone used to auto-answer approvals inside its own process, which stopped
// working as soon as the app was killed. This module moves the decision into the
// DSH host process: a prepended `approval/request` waterfall listener answers
// `allowed-once` for exactly the workspaces/sessions the user picked on the
// phone, and delegates everything else to the normal chain (desktop UI and
// phone). Rules live next to the other relay state:
//
//   ~/.dsh/remote/auto-approve.json
//
// The phone edits them through the relay-owned API
// (`GET/PUT /__relay/auto-approve`), which authenticates with the pairing token
// on both the LAN TLS listener and the gateway's loopback hop.
//
// Safety properties:
//   - disabled by default; no ranges selected means nothing is auto-approved;
//   - only `allowed-once` is ever returned (never `rejected`);
//   - user questions (`user-questions/request`) are never touched;
//   - a broken rules file or a matcher error falls back to manual approval;
//   - every automatic decision is logged and counted.
import fs from 'node:fs'
import os from 'node:os'
import path from 'node:path'

export const AUTO_APPROVE_SCHEMA = 2
export const AUTO_APPROVE_PATH = '/__relay/auto-approve'
export const AUTO_APPROVE_MAX_WORKSPACES = 200
export const AUTO_APPROVE_MAX_SESSIONS = 500
const MAX_TITLE = 200
const MAX_RECENT = 50

/** Error raised for user-supplied rule documents (HTTP 400). */
export class AutoApproveValidationError extends Error {
  constructor (message) {
    super(message)
    this.name = 'AutoApproveValidationError'
    this.code = 'invalid-auto-approve'
  }
}

export function autoApproveFilePath (home = os.homedir()) {
  return path.join(home, '.dsh', 'remote', 'auto-approve.json')
}

function asObject (value) {
  return value && typeof value === 'object' && !Array.isArray(value) ? value : {}
}

function asString (value) {
  return typeof value === 'string' && value.trim() !== '' ? value.trim() : null
}

function shortText (value) {
  const text = asString(value)
  return text === null ? null : text.slice(0, MAX_TITLE)
}

/**
 * Canonical comparison key for a workspace directory. Windows paths are
 * case-insensitive and accept both separators; trailing separators never
 * matter. Kept as a pure helper so the matcher and its tests share one rule.
 * @param {unknown} value
 * @returns {string}
 */
export function normalizeWorkspacePath (value) {
  const raw = asString(value)
  if (raw === null) return ''
  let normalized = raw.replace(/[\\/]+$/, '')
  if (process.platform === 'win32') {
    normalized = normalized.replace(/\//g, '\\').toLowerCase()
  }
  return normalized
}

/** A fresh, disabled rules document. */
export function emptyAutoApproveDocument () {
  return {
    schemaVersion: AUTO_APPROVE_SCHEMA,
    enabled: false,
    all: false,
    workspaces: [],
    sessions: [],
    revision: 0,
    updatedAt: null,
    updatedBy: null
  }
}

function normalizeWorkspaces (value) {
  if (value === undefined || value === null) return []
  if (!Array.isArray(value)) throw new AutoApproveValidationError('workspaces must be an array')
  if (value.length > AUTO_APPROVE_MAX_WORKSPACES) {
    throw new AutoApproveValidationError(`at most ${AUTO_APPROVE_MAX_WORKSPACES} workspaces are allowed`)
  }
  const rows = []
  const seen = new Set()
  for (const item of value) {
    const row = asObject(item)
    const id = asString(row.id)
    const dir = asString(row.path)
    if (id === null && dir === null) continue
    const key = (id ?? '') + '\u0000' + (dir ?? '')
    if (seen.has(key)) continue
    seen.add(key)
    rows.push({ id, path: dir, title: shortText(row.title) })
  }
  return rows
}

function normalizeSessions (value) {
  if (value === undefined || value === null) return []
  if (!Array.isArray(value)) throw new AutoApproveValidationError('sessions must be an array')
  if (value.length > AUTO_APPROVE_MAX_SESSIONS) {
    throw new AutoApproveValidationError(`at most ${AUTO_APPROVE_MAX_SESSIONS} sessions are allowed`)
  }
  const rows = []
  const seen = new Set()
  for (const item of value) {
    const row = asObject(item)
    const id = asString(row.id)
    if (id === null || seen.has(id)) continue
    seen.add(id)
    rows.push({
      id,
      title: shortText(row.title),
      cwd: asString(row.cwd),
      parentSessionId: asString(row.parentSessionId)
    })
  }
  return rows
}

/**
 * Validate and normalize one rule document (phone request or file content).
 *
 * @param {unknown} value
 * @param {{ revision?: number, updatedAt?: string|null, updatedBy?: string|null }} [meta]
 * @returns {ReturnType<typeof emptyAutoApproveDocument>}
 */
export function normalizeAutoApproveDocument (value, meta = {}) {
  if (Array.isArray(value)) throw new AutoApproveValidationError('document must be an object')
  const raw = asObject(value)
  const sourceVersion = raw.schemaVersion === undefined ? 1 : Number(raw.schemaVersion)
  if (!Number.isSafeInteger(sourceVersion) || sourceVersion < 1 || sourceVersion > AUTO_APPROVE_SCHEMA) {
    throw new AutoApproveValidationError(`unsupported schemaVersion ${JSON.stringify(raw.schemaVersion)}`)
  }
  // v1 carried a mode; migrate it into the tree shape (all + selected workspaces/sessions).
  // No compatibility promise beyond reading an existing file once.
  const legacyAll = sourceVersion < 2 && raw.mode === 'all'
  const all = raw.all === true || legacyAll
  const revision = Number.isSafeInteger(Number(meta.revision)) && Number(meta.revision) >= 0
    ? Number(meta.revision)
    : 0
  return {
    schemaVersion: AUTO_APPROVE_SCHEMA,
    enabled: raw.enabled === true,
    all,
    workspaces: all ? [] : normalizeWorkspaces(raw.workspaces),
    sessions: all ? [] : normalizeSessions(raw.sessions),
    revision,
    updatedAt: asString(meta.updatedAt) ?? null,
    updatedBy: asString(meta.updatedBy) ?? null
  }
}

function atomicWrite (file, document) {
  const dir = path.dirname(file)
  fs.mkdirSync(dir, { recursive: true })
  const temp = path.join(dir, `.${path.basename(file)}.${process.pid}.${Date.now()}.tmp`)
  try {
    fs.writeFileSync(temp, JSON.stringify(document, null, 2) + '\n', { mode: 0o600 })
    fs.renameSync(temp, file)
    try { fs.chmodSync(file, 0o600) } catch {}
  } catch (error) {
    try { fs.unlinkSync(temp) } catch {}
    throw error
  }
}

/**
 * Durable rule store with a directory watcher (manual edits and atomic rename
 * from another writer both reload). A malformed file disables auto-approval
 * until it is fixed: continuing with the previous document after a corrupt
 * write would silently keep permissions open.
 *
 * @param {{ file?: string, log?: (line: string) => void }} [options]
 */
export function createAutoApproveStore (options = {}) {
  const file = options.file ? path.resolve(String(options.file)) : autoApproveFilePath()
  const log = typeof options.log === 'function' ? options.log : () => {}
  let document = emptyAutoApproveDocument()
  let error = null
  let watcher = null
  let listeners = new Set()

  function readFromDisk () {
    if (!fs.existsSync(file)) {
      return { document: emptyAutoApproveDocument(), error: null }
    }
    try {
      const raw = JSON.parse(fs.readFileSync(file, 'utf8'))
      const normalized = normalizeAutoApproveDocument(raw, {
        revision: raw && raw.revision,
        updatedAt: raw && raw.updatedAt,
        updatedBy: raw && raw.updatedBy
      })
      return { document: normalized, error: null }
    } catch (loadError) {
      return {
        document: emptyAutoApproveDocument(),
        error: `invalid ${file}: ${loadError && loadError.message ? loadError.message : String(loadError)}`
      }
    }
  }

  function reload (reason) {
    const next = readFromDisk()
    document = next.document
    error = next.error
    if (error) log(`[remote-relay] WARN auto-approve: ${error}`)
    else if (reason) log(`[remote-relay] auto-approve rules reloaded (${reason})`)
    for (const listener of listeners) {
      try { listener(snapshot()) } catch {}
    }
    return snapshot()
  }

  function snapshot () {
    return {
      ...document,
      workspaces: document.workspaces.map((row) => ({ ...row })),
      sessions: document.sessions.map((row) => ({ ...row })),
      error
    }
  }

  function save (input) {
    const nextRevision = (Number.isSafeInteger(document.revision) ? document.revision : 0) + 1
    const normalized = normalizeAutoApproveDocument(input, {
      revision: nextRevision,
      updatedAt: new Date().toISOString(),
      updatedBy: 'phone'
    })
    atomicWrite(file, normalized)
    document = normalized
    error = null
    log(`[remote-relay] auto-approve rules saved: enabled=${normalized.enabled} all=${normalized.all} workspaces=${normalized.workspaces.length} sessions=${normalized.sessions.length}`)
    for (const listener of listeners) {
      try { listener(snapshot()) } catch {}
    }
    return snapshot()
  }

  function watch () {
    if (watcher) return () => {}
    const dir = path.dirname(file)
    const base = path.basename(file)
    let timer = null
    try {
      watcher = fs.watch(dir, { persistent: false }, (_event, filename) => {
        if (filename && String(filename) !== base) return
        if (timer) clearTimeout(timer)
        timer = setTimeout(() => {
          timer = null
          reload('file-change')
        }, 300)
        timer.unref?.()
      })
      watcher.on?.('error', () => {})
    } catch (watchError) {
      log(`[remote-relay] WARN cannot watch ${dir} for auto-approve changes: ${watchError && watchError.message ? watchError.message : String(watchError)}`)
      return () => {}
    }
    return () => {
      if (timer) clearTimeout(timer)
      try { watcher?.close() } catch {}
      watcher = null
    }
  }

  // Load once at construction so the service starts with the persisted state.
  const initial = readFromDisk()
  document = initial.document
  error = initial.error
  if (error) log(`[remote-relay] WARN auto-approve: ${error}`)

  return {
    file,
    get: snapshot,
    reload: (reason) => reload(reason),
    save,
    watch,
    onChange (listener) {
      if (typeof listener !== 'function') return () => {}
      listeners.add(listener)
      return () => listeners.delete(listener)
    }
  }
}

/**
 * Decide whether one approval request is covered by the current rules.
 *
 * @param {object} request approval/request payload ({ agent, toolName, callId?, signal? })
 * @param {ReturnType<typeof normalizeAutoApproveDocument>} rules
 * @param {{
 *   resolveWorkspaceId?: (cwd: string) => Promise<string|null>,
 *   loadSessionHeaders?: () => Map<string, object>,
 *   maxParentDepth?: number
 * }} [deps]
 * @returns {Promise<{ rule: string } | null>}
 */
export async function matchAutoApproveRequest (request, rules, deps = {}) {
  if (!rules || rules.enabled !== true) return null
  const session = request && request.agent ? request.agent.session : undefined
  if (!session) return null
  if (rules.all === true) return { rule: 'all' }

  const sessionId = asString(session.id)
  if (sessionId === null) return null

  // Selected sessions: the exact session and every subagent descendant. This
  // check runs before the workspace check so a partially selected workspace
  // (workspace node unchecked, some sessions checked) is handled by ids.
  const selectedSessions = new Set(rules.sessions.map((row) => row.id).filter(Boolean))
  if (selectedSessions.size > 0) {
    const loadHeaders = typeof deps.loadSessionHeaders === 'function' ? deps.loadSessionHeaders : () => new Map()
    const headers = loadHeaders()
    const maxDepth = Number.isSafeInteger(deps.maxParentDepth) && deps.maxParentDepth > 0
      ? deps.maxParentDepth
      : 8
    let current = sessionId
    let header = session.header && typeof session.header === 'object' ? session.header : headers.get(current)
    for (let depth = 0; depth < maxDepth && current; depth += 1) {
      if (selectedSessions.has(current)) return { rule: `session:${current}` }
      const parent = header && typeof header.parentSession === 'string' ? header.parentSession : null
      if (!parent) break
      current = parent
      header = headers.get(current)
    }
  }

  // Fully selected workspaces: every session (including future ones) whose cwd
  // resolves to that workspace. Prefer the durable registry (canonical cwd);
  // fall back to comparing the session cwd with the stored workspace paths.
  if (rules.workspaces.length === 0) return null
  const header = session.header && typeof session.header === 'object' ? session.header : {}
  const cwd = asString(header.cwd)
  if (cwd === null) return null

  const selectedIds = new Set(rules.workspaces.map((row) => row.id).filter(Boolean))
  if (selectedIds.size > 0 && typeof deps.resolveWorkspaceId === 'function') {
    try {
      const workspaceId = await deps.resolveWorkspaceId(cwd)
      if (workspaceId && selectedIds.has(workspaceId)) {
        return { rule: `workspace:${workspaceId}` }
      }
    } catch {
      // Registry unavailable: fall through to the path comparison.
    }
  }

  const cwdKey = normalizeWorkspacePath(cwd)
  if (cwdKey) {
    for (const row of rules.workspaces) {
      const key = normalizeWorkspacePath(row.path)
      if (key && key === cwdKey) return { rule: `workspace-path:${row.id ?? key}` }
    }
  }
  return null
}

/**
 * Host-side service: owns the store, the prepended waterfall listener, the
 * audit trail and the phone-facing JSON API.
 *
 * @param {{
 *   ctx: any,
 *   file?: string,
 *   log?: (line: string) => void,
 *   tokens?: () => (string|null|undefined)[],
 *   onChange?: (summary: object) => void,
 *   now?: () => string
 * }} options
 */
export function createAutoApproveService (options = {}) {
  const ctx = options.ctx
  const log = typeof options.log === 'function' ? options.log : () => {}
  const now = typeof options.now === 'function' ? options.now : () => new Date().toISOString()
  const tokens = typeof options.tokens === 'function' ? options.tokens : () => []
  const store = createAutoApproveStore({ file: options.file, log })
  const stats = { allowed: 0, errors: 0, lastAllowedAt: null }
  const recent = []

  const loadSessionHeaders = () => {
    const headers = new Map()
    try {
      const sessions = ctx && ctx.sessions && typeof ctx.sessions.list === 'function' ? ctx.sessions.list() : []
      for (const session of sessions) {
        const header = session && session.header
        if (header && typeof header.id === 'string' && header.id) headers.set(header.id, header)
      }
    } catch {}
    return headers
  }

  const resolveWorkspaceId = async (cwd) => {
    try {
      const registry = ctx && ctx.workspaceRegistry
      if (!registry || typeof registry.resolveByPath !== 'function') return null
      const workspace = await registry.resolveByPath(cwd)
      return workspace && typeof workspace.id === 'string' ? workspace.id : null
    } catch {
      return null
    }
  }

  const notify = () => {
    if (typeof options.onChange !== 'function') return
    try { options.onChange(summary()) } catch {}
  }

  function summary () {
    const rules = store.get()
    return {
      schemaVersion: AUTO_APPROVE_SCHEMA,
      enabled: rules.enabled === true,
      all: rules.all === true,
      workspaces: rules.workspaces.length,
      sessions: rules.sessions.length,
      revision: rules.revision,
      updatedAt: rules.updatedAt,
      updatedBy: rules.updatedBy,
      stats: { ...stats },
      error: rules.error ?? null,
      file: store.file
    }
  }

  function status () {
    const rules = store.get()
    return {
      schemaVersion: AUTO_APPROVE_SCHEMA,
      enabled: rules.enabled === true,
      all: rules.all === true,
      workspaces: rules.workspaces,
      sessions: rules.sessions,
      revision: rules.revision,
      updatedAt: rules.updatedAt,
      updatedBy: rules.updatedBy,
      stats: { ...stats },
      recent: recent.map((row) => ({ ...row })),
      error: rules.error ?? null,
      file: store.file
    }
  }

  async function onApproval (request, next) {
    try {
      if (request && request.signal && request.signal.aborted) return next()
      const rules = store.get()
      if (rules.enabled !== true) return next()
      const hit = await matchAutoApproveRequest(request, rules, { resolveWorkspaceId, loadSessionHeaders })
      if (!hit) return next()
      const session = request.agent && request.agent.session ? request.agent.session : {}
      const sessionId = typeof session.id === 'string' ? session.id : ''
      const toolName = typeof request.toolName === 'string' ? request.toolName : ''
      const at = now()
      stats.allowed += 1
      stats.lastAllowedAt = at
      recent.unshift({
        at,
        sessionId,
        toolName,
        callId: typeof request.callId === 'string' ? request.callId : null,
        rule: hit.rule
      })
      if (recent.length > MAX_RECENT) recent.length = MAX_RECENT
      log(`[remote-relay] auto-approve allowed session=${sessionId || '?'} tool=${toolName || '?'} rule=${hit.rule}`)
      notify()
      return 'allowed-once'
    } catch (error) {
      stats.errors += 1
      log(`[remote-relay] WARN auto-approve failed (${error && error.message ? error.message : String(error)}); delegating to manual approval`)
      return next()
    }
  }

  function install () {
    let off = () => {}
    try {
      off = ctx.on('approval/request', onApproval, { prepend: true })
    } catch {
      off = ctx.on('approval/request', onApproval)
    }
    const unwatch = store.watch()
    const unsubscribe = store.onChange(() => notify())
    notify()
    return () => {
      try { off() } catch {}
      try { unwatch() } catch {}
      try { unsubscribe() } catch {}
    }
  }

  const api = {
    tokens: () => tokens().filter((value) => typeof value === 'string' && value.length > 0),
    status,
    save (body) {
      const saved = store.save(body)
      notify()
      return {
        ...status(),
        enabled: saved.enabled,
        all: saved.all === true,
        workspaces: saved.workspaces,
        sessions: saved.sessions,
        revision: saved.revision,
        updatedAt: saved.updatedAt,
        updatedBy: saved.updatedBy
      }
    }
  }

  return {
    file: store.file,
    summary,
    status,
    api,
    install,
    store
  }
}
