// Compact session-list projection for phones (relay v0.6.0+).
//
// The phone paints its home screen from `session/list`, but the upstream payload
// is dominated by per-session projections the list never renders (inbox,
// permissions, tokenUsage, todos, agentTeam snapshots, ...). On a real desktop
// the raw list is ~1.06 MB while the eleven fields below are all the client
// needs — measured on a 241-session fixture the trimmed payload is ~8% of the
// original.
//
// Division of labour:
//   - `UpstreamProxy` (proxy.mjs) owns routing/auth/HTTP response shape and only
//     calls `list(cookieHeader, options)` / `detail(sessionId, cookieHeader)`.
//   - this module owns the upstream call and the trimming.
//
// Credential rule: the upstream call is made with the *phone's own* `dsh-auth`
// cookie, forwarded verbatim; the relay never mints, caches or logs cookies or
// tokens here. Host/Origin rewriting reuses the proxy's trust-fence helper.
//
// Schema v1 is deliberately additive-friendly: clients ignore unknown fields, so
// a later revision may add fields (and bump `schema`) without breaking phones.
import http from 'node:http'
import https from 'node:https'
import { randomUUID } from 'node:crypto'

import { rewriteRequestHeaders } from './proxy.mjs'

/** Version of the compact list schema returned to clients. */
export const SUMMARY_SCHEMA = 1

/**
 * Fields of one `items[]` row in schema v1, in wire order. Clients may ignore
 * unknown fields; new fields are appended here and `schema` is bumped only for
 * incompatible changes.
 */
export const SUMMARY_FIELDS = Object.freeze([
  'sessionId',
  'updatedAt',
  'running',
  'blank',
  'agentAvailable',
  'title',
  'cwd',
  'model',
  'lastPromptAt',
  'parentSessionId',
  'origin'
])

/** Official unary RPC endpoint used to read the session list. */
export const SESSION_LIST_ENDPOINT = '/api/session/list'

/** Upstream call timeout: the desktop DSH answers in milliseconds when healthy. */
export const SUMMARY_TIMEOUT_MS = 8000

/** Hard cap on the upstream body we are willing to buffer (defensive). */
const MAX_RESPONSE_BYTES = 32 * 1024 * 1024

function asObject (value) {
  return value && typeof value === 'object' && !Array.isArray(value) ? value : {}
}

function asString (value) {
  return typeof value === 'string' && value !== '' ? value : null
}

function asFiniteNumber (value) {
  return typeof value === 'number' && Number.isFinite(value) ? value : null
}

/** A session is a subagent session when the upstream marks a parent. */
export function isSubagentSession (item) {
  return asString(asObject(item).parentSessionId) !== null
}

/**
 * Trim one upstream `SessionSummary` to the schema-v1 row.
 *
 * Sources (mirrors the Core client mapping documented in docs/protocol-v1.md):
 *   title        <- projections.values.title
 *   model        <- projections.values.modelSelection.lastUsed.model
 *                   (fallback projections.values.modelSelection.next.model)
 *   cwd          <- item.cwd (fallback projections.values.workingDirectory)
 *   lastPromptAt <- projections.values.sessionListMetadata.lastPromptAt
 *   blank        <- item.blank (fallback projections.values.sessionListMetadata.blank)
 * Everything else (projections, inbox, agentTeam, ...) is dropped.
 *
 * @param {unknown} item
 * @returns {Record<string, unknown>|null} null when the input is not an object.
 */
export function trimSessionSummary (item) {
  if (!item || typeof item !== 'object' || Array.isArray(item)) return null
  const row = item
  const projections = asObject(row.projections)
  const values = asObject(projections.values)
  const selection = asObject(values.modelSelection)
  const metadata = asObject(values.sessionListMetadata)
  const model = asString(asObject(selection.lastUsed).model) ?? asString(asObject(selection.next).model)
  const cwd = asString(row.cwd) ?? asString(values.workingDirectory)
  const blank = row.blank === true ? true : (row.blank === undefined ? metadata.blank === true : false)
  return {
    sessionId: asString(row.sessionId),
    updatedAt: asFiniteNumber(row.updatedAt),
    running: row.running === true,
    blank,
    agentAvailable: row.agentAvailable === true,
    title: asString(values.title),
    cwd,
    model,
    lastPromptAt: asFiniteNumber(metadata.lastPromptAt),
    parentSessionId: asString(row.parentSessionId),
    origin: asString(row.origin)
  }
}

/**
 * Build the schema-v1 list payload from the upstream `session/list` value.
 * Subagent sessions (non-empty `parentSessionId`) are hidden unless requested;
 * `counts.total` still counts every upstream row so a client can show "N of M".
 *
 * @param {unknown} upstreamValue result.value of `session/list` (`{items, cursor?}`)
 * @param {{ includeSubagents?: boolean, generatedAt?: string }} [options]
 */
export function buildSummaryPayload (upstreamValue, options = {}) {
  const value = asObject(upstreamValue)
  const rawItems = Array.isArray(value.items) ? value.items : []
  const includeSubagents = options.includeSubagents === true
  const items = []
  let hiddenSubagents = 0
  for (const raw of rawItems) {
    if (!includeSubagents && isSubagentSession(raw)) {
      hiddenSubagents += 1
      continue
    }
    const trimmed = trimSessionSummary(raw)
    if (trimmed) items.push(trimmed)
  }
  return {
    ok: true,
    schema: SUMMARY_SCHEMA,
    fields: [...SUMMARY_FIELDS],
    generatedAt: options.generatedAt ?? new Date().toISOString(),
    cursor: value.cursor ?? null,
    counts: {
      total: rawItems.length,
      hiddenSubagents,
      returned: items.length
    },
    items
  }
}

/**
 * Find one upstream session item by exact `sessionId` (detail mode).
 * @param {unknown} upstreamValue
 * @param {unknown} sessionId
 * @returns {Record<string, unknown>|null} the upstream item, untrimmed.
 */
export function findSessionItem (upstreamValue, sessionId) {
  const wanted = asString(sessionId)
  if (!wanted) return null
  const value = asObject(upstreamValue)
  const rawItems = Array.isArray(value.items) ? value.items : []
  for (const item of rawItems) {
    if (asString(asObject(item).sessionId) === wanted) return item
  }
  return null
}

function upstreamFailure (message) {
  const error = new Error(message)
  error.code = 'upstream-failed'
  return error
}

function sessionListBody () {
  return JSON.stringify({
    type: 'client-request',
    rpcId: `relay-summary-${randomUUID()}`,
    method: 'session/list',
    payload: { args: { _request: {} } }
  })
}

/**
 * POST the official unary `session/list` envelope to the loopback DSH API with
 * the caller's cookie. Resolves with `result.value` (the `{items,...}` object).
 *
 * @param {{ upstreamPort: number, upstreamProtocol?: string, cookieHeader?: string, timeoutMs?: number }} options
 * @returns {Promise<Record<string, unknown>>}
 */
function requestSessionList ({ upstreamPort, upstreamProtocol = 'http:', cookieHeader = '', timeoutMs = SUMMARY_TIMEOUT_MS }) {
  const body = Buffer.from(sessionListBody(), 'utf8')
  const transport = upstreamProtocol === 'https:' ? https : http
  const headers = rewriteRequestHeaders(
    cookieHeader ? { cookie: cookieHeader } : {},
    upstreamPort,
    upstreamProtocol
  )
  headers['content-type'] = 'application/json'
  headers.accept = 'application/json'
  headers['content-length'] = body.length

  return new Promise((resolve, reject) => {
    let settled = false
    const fail = (message) => {
      if (settled) return
      settled = true
      reject(upstreamFailure(message))
    }
    const upstreamReq = transport.request({
      host: '127.0.0.1',
      port: upstreamPort,
      method: 'POST',
      path: SESSION_LIST_ENDPOINT,
      headers,
      rejectUnauthorized: false,
      timeout: timeoutMs
    }, (upstreamRes) => {
      const status = upstreamRes.statusCode ?? 0
      const chunks = []
      let size = 0
      upstreamRes.on('data', (chunk) => {
        size += chunk.length
        if (size > MAX_RESPONSE_BYTES) {
          upstreamRes.destroy()
          fail(`session/list response exceeded ${MAX_RESPONSE_BYTES} bytes`)
          return
        }
        chunks.push(chunk)
      })
      upstreamRes.on('error', (error) => fail(error && error.message ? error.message : String(error)))
      upstreamRes.on('end', () => {
        if (settled) return
        if (status !== 200) {
          fail(`session/list returned HTTP ${status}`)
          return
        }
        let envelope
        try {
          envelope = JSON.parse(Buffer.concat(chunks).toString('utf8'))
        } catch {
          fail('session/list returned invalid JSON')
          return
        }
        const result = asObject(asObject(envelope).result)
        if (result.ok !== true || !('value' in result)) {
          const code = asString(asObject(result.error).code)
          fail(`session/list failed${code ? ` (${code})` : ''}`)
          return
        }
        settled = true
        resolve(asObject(result.value))
      })
    })
    upstreamReq.on('timeout', () => upstreamReq.destroy(new Error(`session/list timed out after ${timeoutMs}ms`)))
    upstreamReq.on('error', (error) => fail(error && error.message ? error.message : String(error)))
    upstreamReq.end(body)
  })
}

/**
 * Create the object `index.mjs` injects into both listeners as
 * `sessionSummaryApi`.
 *
 * The upstream call carries the phone's cookie, so both methods take it as an
 * argument (list first for symmetry with the contract, detail last because the
 * session id is the primary key). Errors carry `code: 'upstream-failed'`; the
 * proxy maps them to HTTP 502. Messages never contain cookies or tokens.
 *
 * @param {{ upstreamPort: number, upstreamProtocol?: string, timeoutMs?: number }} options
 * @returns {{
 *   schema: number,
 *   fields: string[],
 *   list: (cookieHeader?: string, options?: { includeSubagents?: boolean }) => Promise<Record<string, unknown>>,
 *   detail: (sessionId: string, cookieHeader?: string) => Promise<{ schema: number, generatedAt: string, item: Record<string, unknown> }|null>
 * }}
 */
export function createSessionSummaryApi ({ upstreamPort, upstreamProtocol = 'http:', timeoutMs = SUMMARY_TIMEOUT_MS } = {}) {
  const port = Number(upstreamPort)
  if (!Number.isInteger(port) || port < 1 || port > 65535) {
    throw new Error(`dsh-remote-summary: invalid upstream port ${JSON.stringify(upstreamPort)}`)
  }
  const protocol = upstreamProtocol === 'https:' ? 'https:' : 'http:'
  return {
    schema: SUMMARY_SCHEMA,
    fields: [...SUMMARY_FIELDS],
    async list (cookieHeader, options = {}) {
      const value = await requestSessionList({
        upstreamPort: port,
        upstreamProtocol: protocol,
        cookieHeader: typeof cookieHeader === 'string' ? cookieHeader : '',
        timeoutMs
      })
      return buildSummaryPayload(value, { includeSubagents: options.includeSubagents === true })
    },
    async detail (sessionId, cookieHeader) {
      const value = await requestSessionList({
        upstreamPort: port,
        upstreamProtocol: protocol,
        cookieHeader: typeof cookieHeader === 'string' ? cookieHeader : '',
        timeoutMs
      })
      const item = findSessionItem(value, sessionId)
      if (!item) return null
      return { schema: SUMMARY_SCHEMA, generatedAt: new Date().toISOString(), item }
    }
  }
}
