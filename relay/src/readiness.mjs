// Startup readiness + retry helpers for the relay protocol self-check.
//
// DSH registers the Remote stream mux (`/api/remote.mux`) only after the
// launcher commits `appReady` — the API Gateway creates that WebSocket upgrade
// route from `appReady.onReady` (see @deepseek-ai/dsh-api-gateway). Until then
// the host webserver destroys every unmatched upgrade socket, which an HTTP
// client observes as `socket hang up` (ECONNRESET).
//
// The relay plugin is mounted *while* that boot is still in progress, so a
// self-check issued from `apply()` races the route registration. Before v0.6.1
// the single startup probe could therefore latch `$events`/`session/follow` as
// unavailable for the whole process lifetime, leaving the pairing page stuck at
// `active=false` even though the endpoint became healthy a few seconds later.
//
// This module keeps that check honest:
//   - `waitForAppReady` blocks the probe until the launcher signals readiness
//     (or a bounded timeout expires, so headless/no-launcher hosts still probe);
//   - `probeWithRetry` retries only failures that can plausibly be transient
//     (mux upgrade hiccups, refused/reset sockets, gateway/* stream errors),
//     so a genuinely incompatible DSH is not hammered.
//
// Both helpers take their dependencies as options, which keeps them unit
// testable without a DSH host or a listening socket.

/** Default ceiling for waiting on the launcher's appReady signal. */
export const APP_READY_TIMEOUT_MS = 60000

/**
 * Failure shapes that a later attempt can plausibly fix: the mux upgrade was
 * reset before the route existed, the upstream socket was not accepting
 * connections yet, or the stream endpoint reported a gateway-level refusal.
 */
const TRANSIENT_ERROR = /mux upgrade|socket hang up|ECONNRESET|ECONNREFUSED|EPIPE|ETIMEDOUT|timeout|gateway\/|upgrade rejected with HTTP (?:409|425|429|5\d\d)/i

/** Capabilities that can only pass once the mux WebSocket is up. */
const MUX_DEPENDENT = new Set(['$events', 'session/follow'])

/**
 * Decide whether a failed probe result is worth another attempt.
 * @param {Record<string, unknown>|null|undefined} result - probeProtocol result
 * @returns {boolean}
 */
export function isTransientProbeFailure (result) {
  if (!result || result.ok === true) return false
  const errors = Array.isArray(result.errors) ? result.errors : []
  if (errors.some((entry) => TRANSIENT_ERROR.test(String(entry ?? '')))) return true
  // Missing mux-dependent capabilities without an explicit error still points
  // at a mux that was not serving yet (the startup race signature).
  const missing = Array.isArray(result.missing) ? result.missing : []
  return missing.some((name) => MUX_DEPENDENT.has(String(name)))
}

/** Join a failed probe into one bounded log line. */
function summarizeFailure (result) {
  if (!result) return 'no result'
  const errors = Array.isArray(result.errors) ? result.errors.map(String) : []
  if (errors.length > 0) return errors.join('; ')
  const missing = Array.isArray(result.missing) ? result.missing.map(String) : []
  if (missing.length > 0) return `missing ${missing.join(', ')}`
  return 'probe not ok'
}

function defaultSleep (ms) {
  return new Promise((resolve) => {
    const timer = setTimeout(resolve, ms)
    timer.unref?.()
  })
}

/**
 * Resolve once the launcher commits `appReady`.
 *
 * Cordis hosts that are not launcher-driven do not provide the service: this
 * resolves `false` immediately so the caller probes as before. A host that
 * provides the service but never commits it is bounded by `timeoutMs`; the
 * relay then probes anyway and reports the real capability state.
 *
 * @param {{ get?: (name: string) => unknown }} ctx - plugin context
 * @param {{ timeoutMs?: number, log?: (line: string) => void }} [options]
 * @returns {Promise<boolean>} true when appReady was observed
 */
export function waitForAppReady (ctx, options = {}) {
  const timeoutMs = Number.isFinite(options.timeoutMs) ? options.timeoutMs : APP_READY_TIMEOUT_MS
  const log = typeof options.log === 'function' ? options.log : () => {}
  if (!(timeoutMs > 0)) {
    log(`[remote-relay] appReady wait disabled (timeoutMs=${String(timeoutMs)}); running the self-check without it`)
    return Promise.resolve(false)
  }

  let ready
  try {
    ready = ctx && typeof ctx.get === 'function' ? ctx.get('appReady') : undefined
  } catch {
    ready = undefined
  }
  if (!ready || typeof ready.onReady !== 'function') return Promise.resolve(false)

  return new Promise((resolve) => {
    let done = false
    let timer = null
    let cancelListener = null
    const finish = (observed, note) => {
      if (done) return
      done = true
      if (timer !== null) {
        clearTimeout(timer)
        timer = null
      }
      if (cancelListener) {
        try { cancelListener() } catch {}
        cancelListener = null
      }
      if (note) log(note)
      resolve(observed)
    }
    try {
      const unsubscribe = ready.onReady(() => finish(true))
      if (typeof unsubscribe === 'function') {
        if (done) {
          try { unsubscribe() } catch {}
        } else {
          cancelListener = unsubscribe
        }
      }
    } catch (error) {
      finish(false, `[remote-relay] appReady hook failed (${error && error.message ? error.message : String(error)}); running the self-check without it`)
      return
    }
    if (timeoutMs > 0) {
      timer = setTimeout(() => finish(false, `[remote-relay] appReady not signalled within ${timeoutMs}ms; running the self-check anyway`), timeoutMs)
      timer.unref?.()
    }
  })
}

/**
 * Wrap a task so concurrent callers share one in-flight run.
 *
 * The self-check is expensive (upstream probe, up to five attempts) and both
 * the startup path and the loopback `POST /__relay/probe` button can ask for
 * it; sharing keeps a double-click from stacking probes. The slot clears when
 * the run settles, regardless of success or failure.
 *
 * @template {unknown[]} Args
 * @param {(...args: Args) => Promise<unknown>} task
 * @returns {(...args: Args) => Promise<unknown>}
 */
export function createSingleFlight (task) {
  if (typeof task !== 'function') throw new TypeError('createSingleFlight: task must be a function')
  let inflight = null
  return (...args) => {
    if (inflight) return inflight
    const run = Promise.resolve().then(() => task(...args))
    inflight = run
    const clear = () => {
      if (inflight === run) inflight = null
    }
    run.then(clear, clear)
    return run
  }
}

/**
 * Run `probe()` once, retrying only transient failures with backoff.
 *
 * `attempts` defaults to the five-attempt budget the docs promise (one initial
 * try plus four backoff retries) so a caller cannot accidentally disable the
 * startup-race retry by omitting the option.
 *
 * @param {{
 *   probe: () => Promise<Record<string, unknown>>,
 *   attempts?: number,
 *   delaysMs?: number[],
 *   retryable?: (result: Record<string, unknown>) => boolean,
 *   continueWhen?: () => boolean,
 *   sleep?: (ms: number) => Promise<void>,
 *   log?: (line: string) => void
 * }} options
 * @returns {Promise<{ result: Record<string, unknown>|null, attempts: number, ok: boolean }>}
 */
export async function probeWithRetry (options = {}) {
  const probe = options.probe
  if (typeof probe !== 'function') throw new TypeError('probeWithRetry: probe must be a function')
  const total = Number.isInteger(options.attempts) && options.attempts > 0 ? options.attempts : 5
  const delays = Array.isArray(options.delaysMs) ? options.delaysMs : [750, 1500, 3000, 6000]
  const retryable = typeof options.retryable === 'function' ? options.retryable : isTransientProbeFailure
  const continueWhen = typeof options.continueWhen === 'function' ? options.continueWhen : () => true
  const sleep = typeof options.sleep === 'function' ? options.sleep : defaultSleep
  const log = typeof options.log === 'function' ? options.log : () => {}

  let result = null
  let attempted = 0
  for (let attempt = 1; attempt <= total; attempt++) {
    if (attempt > 1) {
      if (!continueWhen()) {
        log('[remote-relay] self-check stopped before retry (relay is shutting down)')
        break
      }
      const wait = delays[Math.min(attempt - 2, delays.length - 1)] ?? 0
      if (wait > 0) {
        log(`[remote-relay] self-check retry ${attempt - 1}/${total - 1} after ${wait}ms`)
        await sleep(wait)
      }
      if (!continueWhen()) {
        log('[remote-relay] self-check stopped before retry (relay is shutting down)')
        break
      }
    }
    attempted = attempt
    try {
      result = await probe()
    } catch (error) {
      result = {
        ok: false,
        checkedAt: new Date().toISOString(),
        capabilities: {},
        missing: [],
        errors: [`probe crashed: ${error && error.message ? error.message : String(error)}`]
      }
    }
    if (result && result.ok === true) return { result, attempts: attempt, ok: true }
    const triesLeft = attempt < total
    if (triesLeft && !retryable(result)) {
      log('[remote-relay] self-check failure is not transient; not retrying')
      return { result, attempts: attempt, ok: false }
    }
    if (triesLeft) log(`[remote-relay] self-check attempt ${attempt}/${total} failed: ${summarizeFailure(result)}`)
  }
  return { result, attempts: attempted, ok: Boolean(result && result.ok === true) }
}
