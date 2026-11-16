// Unit tests for the startup readiness + retry helpers used by the relay
// protocol self-check (relay v0.6.1+).
//
// These helpers exist because DSH registers `/api/remote.mux` only after the
// launcher commits `appReady`; a probe issued during boot sees the host
// webserver destroy the unmatched upgrade socket ("socket hang up") and must be
// retried instead of latched as a permanent incompatibility.
//
// Run: node --test relay/test/readiness.test.mjs   (or: cd relay && npm test)
import assert from 'node:assert/strict'
import test from 'node:test'

import {
  APP_READY_TIMEOUT_MS,
  createSingleFlight,
  isTransientProbeFailure,
  probeWithRetry,
  waitForAppReady
} from '../src/readiness.mjs'

/** Build a probe result fixture. */
function result (overrides = {}) {
  return {
    ok: true,
    checkedAt: '2026-11-13T00:00:00.000Z',
    capabilities: { $connect: true, 'session/list': true, 'session/events': true, $events: true, 'session/follow': true },
    missing: [],
    errors: [],
    ...overrides
  }
}

/** A fake `appReady` service. */
function fakeAppReady () {
  const listeners = new Set()
  let ready = false
  return {
    service: {
      onReady (listener) {
        if (ready) {
          listener()
          return () => {}
        }
        listeners.add(listener)
        return () => listeners.delete(listener)
      }
    },
    commit () {
      ready = true
      for (const listener of [...listeners]) listener()
      listeners.clear()
    },
    get listenerCount () {
      return listeners.size
    }
  }
}

function fakeCtx (appReady) {
  return {
    get (name) {
      return name === 'appReady' ? appReady : undefined
    }
  }
}

test('waitForAppReady resolves true immediately when appReady already committed', async () => {
  const ready = fakeAppReady()
  ready.commit()
  assert.equal(await waitForAppReady(fakeCtx(ready.service), { timeoutMs: 1000 }), true)
})

test('waitForAppReady resolves true when the launcher commits later', async () => {
  const ready = fakeAppReady()
  const pending = waitForAppReady(fakeCtx(ready.service), { timeoutMs: 1000 })
  setTimeout(() => ready.commit(), 15)
  assert.equal(await pending, true)
  assert.equal(ready.listenerCount, 0)
})

test('waitForAppReady returns false on a host without the appReady service', async () => {
  const lines = []
  assert.equal(await waitForAppReady({}, { log: (line) => lines.push(line) }), false)
  assert.equal(await waitForAppReady(undefined, {}), false)
  assert.deepEqual(lines, [])
})

test('waitForAppReady unsubscribes and resolves false when the timeout expires', async () => {
  const ready = fakeAppReady()
  const lines = []
  const startedAt = Date.now()
  const observed = await waitForAppReady(fakeCtx(ready.service), { timeoutMs: 25, log: (line) => lines.push(line) })
  assert.equal(observed, false)
  assert.ok(Date.now() - startedAt >= 20)
  assert.equal(ready.listenerCount, 0, 'the appReady listener must not leak')
  assert.match(lines.join('\n'), /appReady not signalled within 25ms/)
})

test('waitForAppReady tolerates a throwing onReady hook', async () => {
  const lines = []
  const ctx = {
    get: () => ({
      onReady () {
        throw new Error('boom')
      }
    })
  }
  assert.equal(await waitForAppReady(ctx, { timeoutMs: 1000, log: (line) => lines.push(line) }), false)
  assert.match(lines.join('\n'), /appReady hook failed.*boom/)
})

test('waitForAppReady treats a non-positive timeout as "do not wait"', async () => {
  const ready = fakeAppReady()
  const lines = []
  assert.equal(await waitForAppReady(fakeCtx(ready.service), { timeoutMs: 0, log: (line) => lines.push(line) }), false)
  assert.equal(ready.listenerCount, 0, 'no listener may be registered when waiting is disabled')
  assert.match(lines.join('\n'), /appReady wait disabled/)
})

test('APP_READY_TIMEOUT_MS is a positive bound', () => {
  assert.ok(Number.isFinite(APP_READY_TIMEOUT_MS) && APP_READY_TIMEOUT_MS > 0)
})

test('isTransientProbeFailure: mux startup-race signatures are retryable', () => {
  assert.equal(isTransientProbeFailure(result({ ok: false, errors: ['mux upgrade: socket hang up'], missing: ['$events', 'session/follow'], capabilities: {} })), true)
  assert.equal(isTransientProbeFailure(result({ ok: false, errors: ['$connect: ECONNREFUSED 127.0.0.1:58570'], missing: ['$connect'] })), true)
  assert.equal(isTransientProbeFailure(result({ ok: false, errors: ['session/follow: gateway/stream-failed'], missing: ['session/follow'] })), true)
  // Missing mux-dependent capabilities alone still point at the race.
  assert.equal(isTransientProbeFailure(result({ ok: false, errors: [], missing: ['$events'] })), true)
})

test('isTransientProbeFailure: hard compatibility failures are not retried', () => {
  assert.equal(isTransientProbeFailure(result()), false)
  assert.equal(isTransientProbeFailure(result({ ok: false, errors: ['$connect: expected 302 + Set-Cookie, got HTTP 401'], missing: ['$connect', 'session/list'], capabilities: {} })), false)
  assert.equal(isTransientProbeFailure(null), false)
  assert.equal(isTransientProbeFailure(undefined), false)
})

test('probeWithRetry returns the first success without sleeping', async () => {
  let calls = 0
  const sleeps = []
  const outcome = await probeWithRetry({
    probe: async () => {
      calls++
      return result()
    },
    attempts: 5,
    sleep: async (ms) => sleeps.push(ms)
  })
  assert.equal(outcome.ok, true)
  assert.equal(outcome.attempts, 1)
  assert.equal(calls, 1)
  assert.deepEqual(sleeps, [])
})

test('probeWithRetry retries a transient failure and then succeeds', async () => {
  const delays = []
  const lines = []
  let calls = 0
  const outcome = await probeWithRetry({
    probe: async () => {
      calls++
      if (calls < 3) {
        return result({ ok: false, errors: ['mux upgrade: socket hang up'], missing: ['$events', 'session/follow'], capabilities: {} })
      }
      return result()
    },
    attempts: 4,
    delaysMs: [10, 20, 30],
    sleep: async (ms) => delays.push(ms),
    log: (line) => lines.push(line)
  })
  assert.equal(outcome.ok, true)
  assert.equal(outcome.attempts, 3)
  assert.equal(calls, 3)
  assert.deepEqual(delays, [10, 20])
  assert.match(lines.join('\n'), /self-check attempt 1\/4 failed: mux upgrade: socket hang up/)
})

test('probeWithRetry stops early on a non-transient failure', async () => {
  let calls = 0
  const outcome = await probeWithRetry({
    probe: async () => {
      calls++
      return result({ ok: false, errors: ['$connect: expected 302 + Set-Cookie, got HTTP 401'], missing: ['$connect'], capabilities: {} })
    },
    attempts: 5,
    sleep: async () => {}
  })
  assert.equal(outcome.ok, false)
  assert.equal(calls, 1, 'auth failures must not be hammered')
  assert.equal(outcome.attempts, 1)
})

test('probeWithRetry defaults to the documented five-attempt budget', async () => {
  let calls = 0
  const delays = []
  const outcome = await probeWithRetry({
    probe: async () => {
      calls++
      return result({ ok: false, errors: ['mux upgrade: socket hang up'], missing: ['$events', 'session/follow'], capabilities: {} })
    },
    delaysMs: [1, 2, 3, 4],
    sleep: async (ms) => delays.push(ms)
  })
  assert.equal(outcome.ok, false)
  assert.equal(outcome.attempts, 5, 'omitting attempts must not disable the retry budget')
  assert.equal(calls, 5)
  assert.deepEqual(delays, [1, 2, 3, 4])
})

test('probeWithRetry stops retrying when continueWhen turns false (dispose)', async () => {
  let calls = 0
  let continueWhen = true
  const delays = []
  const outcome = await probeWithRetry({
    probe: async () => {
      calls++
      continueWhen = false
      return result({ ok: false, errors: ['mux upgrade: socket hang up'], missing: ['$events'], capabilities: {} })
    },
    attempts: 5,
    delaysMs: [1],
    continueWhen: () => continueWhen,
    sleep: async (ms) => delays.push(ms)
  })
  assert.equal(outcome.ok, false)
  assert.equal(outcome.attempts, 1)
  assert.equal(calls, 1)
  assert.deepEqual(delays, [], 'no backoff sleep after the relay was disposed')
})

test('probeWithRetry returns the last transient failure after exhausting attempts', async () => {
  let calls = 0
  const outcome = await probeWithRetry({
    probe: async () => {
      calls++
      return result({ ok: false, errors: ['mux upgrade: socket hang up'], missing: ['$events', 'session/follow'], capabilities: {} })
    },
    attempts: 3,
    delaysMs: [1, 2],
    sleep: async () => {}
  })
  assert.equal(outcome.ok, false)
  assert.equal(outcome.attempts, 3)
  assert.equal(calls, 3)
  assert.match(outcome.result.errors[0], /socket hang up/)
})

test('probeWithRetry converts a throwing probe into a failure result', async () => {
  // A crashed probe is a programming/serialization error rather than a
  // startup-race symptom, so it is reported once and not retried.
  let calls = 0
  const outcome = await probeWithRetry({
    probe: async () => {
      calls++
      throw new Error('kaboom')
    },
    attempts: 2,
    delaysMs: [1],
    sleep: async () => {}
  })
  assert.equal(outcome.ok, false)
  assert.equal(outcome.attempts, 1)
  assert.equal(calls, 1)
  assert.match(outcome.result.errors[0], /probe crashed: kaboom/)
})

test('probeWithRetry rejects a missing probe function', async () => {
  await assert.rejects(() => probeWithRetry({}), /probe must be a function/)
})

test('createSingleFlight shares one in-flight run between concurrent callers', async () => {
  let calls = 0
  let release
  const gate = new Promise((resolve) => { release = resolve })
  const run = createSingleFlight(async (value) => {
    calls++
    await gate
    return `done:${value}`
  })
  const a = run('a')
  const b = run('b')
  assert.equal(a, b, 'both callers must receive the same promise')
  release()
  assert.equal(await a, 'done:a')
  assert.equal(calls, 1)
})

test('createSingleFlight clears its slot after settling (success and failure)', async () => {
  let calls = 0
  const run = createSingleFlight(async () => {
    calls++
    if (calls === 1) throw new Error('first')
    return calls
  })
  await assert.rejects(() => run(), /first/)
  assert.equal(await run(), 2, 'a failed run must not wedge the single-flight slot')
  assert.equal(await run(), 3)
  assert.equal(calls, 3)
})

test('createSingleFlight runs the task on a microtask, not synchronously', async () => {
  let started = false
  const run = createSingleFlight(async () => {
    started = true
    return true
  })
  const pending = run()
  assert.equal(started, false)
  assert.equal(await pending, true)
  assert.equal(started, true)
})
