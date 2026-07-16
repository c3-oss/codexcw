// Smoke tests for @c3-oss/codexcw, driven by a fake `codex` so they run
// without a real Codex install. Unix-only (the fixture is a shell script).

import assert from 'node:assert/strict'
import { mkdtempSync, chmodSync, readFileSync, writeFileSync } from 'node:fs'
import { tmpdir } from 'node:os'
import { join, dirname } from 'node:path'
import { fileURLToPath } from 'node:url'
import { test } from 'node:test'

import native from '../binding.js'
import { Runner, CodexcwError, getAccountUsage } from '../index.js'

const here = dirname(fileURLToPath(import.meta.url))
const fakeCodex = join(here, 'fixtures', 'fake-codex.sh')
chmodSync(fakeCodex, 0o755)

function runnerWithCapture() {
  const dir = mkdtempSync(join(tmpdir(), 'codexcw-'))
  const argsFile = join(dir, 'args.txt')
  const stdinFile = join(dir, 'stdin.txt')
  const runner = new Runner({
    executable: fakeCodex,
    env: { CODEXCW_ARGS_FILE: argsFile, CODEXCW_STDIN_FILE: stdinFile },
  })
  return { runner, argsFile, stdinFile }
}

test('run decodes events and uses safe defaults', async () => {
  const { runner, argsFile, stdinFile } = runnerWithCapture()

  const result = await runner.run({ prompt: 'diga oi' })

  assert.equal(result.threadId, 'thread-1')
  assert.equal(result.finalMessage, 'Oi.')
  assert.equal(result.usage.inputTokens, 10)
  assert.equal(result.events.length, 4)

  assert.equal(readFileSync(stdinFile, 'utf8'), 'diga oi')

  const args = readFileSync(argsFile, 'utf8').trim().split('\n')
  assert.ok(args.includes('--json'))
  assert.ok(args.includes('--sandbox'))
  assert.ok(args.includes('read-only'))
  assert.ok(args.includes('approval_policy="never"'))
  assert.equal(args.at(-1), '-')
})

test('streaming yields events in order', async () => {
  const { runner } = runnerWithCapture()

  const session = await runner.start({ prompt: 'stream' })
  const types = []
  for await (const event of session.events()) {
    types.push(event.type)
  }
  const result = await session.wait()

  assert.deepEqual(types, [
    'thread.started',
    'turn.started',
    'item.completed',
    'turn.completed',
  ])
  assert.equal(result.finalMessage, 'Oi.')
})

test('onEvent callback runs per event', async () => {
  const { runner } = runnerWithCapture()
  const seen = []

  const result = await runner.run({ prompt: 'cb' }, (event) => {
    seen.push(event.type)
  })

  assert.equal(seen.length, 4)
  assert.equal(result.finalMessage, 'Oi.')
})

test('onEvent throw cancels the run', async () => {
  const { runner } = runnerWithCapture()

  await assert.rejects(
    runner.run({ prompt: 'boom' }, (event) => {
      if (event.type === 'turn.started') throw new Error('stop')
    }),
    /stop/,
  )
})

test('missing prompt surfaces a typed error', async () => {
  const { runner } = runnerWithCapture()

  await assert.rejects(runner.run({}), (err) => {
    assert.ok(err instanceof CodexcwError)
    assert.equal(err.kind, 'promptRequired')
    return true
  })
})

test('runMany collects results', async () => {
  const { runner } = runnerWithCapture()

  const group = await runner.runMany(
    [{ prompt: 'a' }, { prompt: 'b' }, { prompt: 'c' }],
    { maxConcurrent: 2 },
  )

  let eventCount = 0
  for await (const _runEvent of group.events()) {
    eventCount += 1
  }

  const results = await group.wait()
  assert.equal(results.length, 3)
  assert.ok(eventCount > 0)
  for (const r of results) {
    assert.equal(r.error, null)
    assert.equal(r.result.finalMessage, 'Oi.')
  }
})

test('runMany preserves request conversion errors in mixed batches', async () => {
  const { runner } = runnerWithCapture()

  const group = await runner.runMany([
    { prompt: 'bad sandbox', sandbox: 'bogus' },
    { prompt: 'valid' },
    { prompt: 'bad approval', approval: 'bogus' },
  ])

  const eventIndices = []
  for await (const runEvent of group.events()) {
    eventIndices.push(runEvent.index)
  }
  const results = await group.wait()

  assert.deepEqual(results.map((result) => result.index), [0, 1, 2])
  assert.ok(eventIndices.length > 0)
  assert.ok(eventIndices.every((index) => index === 1))

  assert.ok(results[0].error instanceof CodexcwError)
  assert.equal(results[0].error.kind, 'invalidRequest')
  assert.match(results[0].error.message, /unknown sandbox mode: bogus/)
  assert.equal(results[0].result, null)

  assert.equal(results[1].error, null)
  assert.equal(results[1].result.finalMessage, 'Oi.')

  assert.ok(results[2].error instanceof CodexcwError)
  assert.equal(results[2].error.kind, 'invalidRequest')
  assert.match(results[2].error.message, /unknown approval policy: bogus/)
  assert.equal(results[2].result, null)
})

test('getAccountUsage reads account limits', async () => {
  const dir = mkdtempSync(join(tmpdir(), 'codexcw-usage-'))
  const fake = join(dir, 'codex')
  const argsFile = join(dir, 'args.txt')
  const envFile = join(dir, 'env.txt')
  const codexHome = join(dir, 'codex-home')
  writeFileSync(
    fake,
    `#!/bin/sh
set -eu
: >"$CODEXCW_ARGS_FILE"
for arg in "$@"; do
  printf '%s\\n' "$arg" >>"$CODEXCW_ARGS_FILE"
done
printf '%s\\n' "$CODEX_HOME" >"$CODEXCW_ENV_FILE"
while IFS= read -r line; do
  case "$line" in
    *'"method":"initialized"'*) ;;
    *'"method":"initialize"'*) printf '%s\\n' '{"id":1,"result":{}}' ;;
    *'"method":"account/rateLimits/read"'*)
      printf '%s\\n' '{"id":2,"result":{"rateLimits":{"limitId":null,"limitName":null,"planType":"pro","rateLimitReachedType":null,"primary":{"usedPercent":12.5,"windowDurationMins":300,"resetsAt":1766948068},"credits":{"hasCredits":true,"unlimited":false,"balance":"7"},"individualLimit":{"limit":"100","used":25,"remainingPercent":"75","resetsAt":"1768000000"}},"rateLimitsByLimitId":{"spark":{"limitName":"Codex Spark","primary":{"usedPercent":8,"windowDurationMins":300,"resetsAt":1767000000}}}}}'
      ;;
    *'"method":"account/usage/read"'*)
      printf '%s\\n' '{"id":3,"result":{"summary":{"lifetimeTokens":"12345678901234567890","peakDailyTokens":456,"longestRunningTurnSec":"789","currentStreakDays":3,"longestStreakDays":"9"},"dailyUsageBuckets":[{"startDate":"2026-07-07","tokens":"42"}]}}'
      ;;
    *'"method":"account/read"'*)
      printf '%s\\n' '{"id":4,"result":{"account":{"type":"chatgpt","email":"stub@example.com","planType":"pro"},"requiresOpenaiAuth":false}}'
      ;;
  esac
done
`,
  )
  chmodSync(fake, 0o755)

  const usage = await getAccountUsage({
    executable: fake,
    env: {
      CODEXCW_ARGS_FILE: argsFile,
      CODEXCW_ENV_FILE: envFile,
      CODEX_HOME: codexHome,
    },
    timeoutMs: 5000,
  })

  assert.equal(usage.account.email, 'stub@example.com')
  assert.equal(usage.rateLimits.planType, 'pro')
  assert.equal(usage.rateLimits.primary.usedPercent, 12.5)
  assert.equal(usage.rateLimits.credits.balance, '7')
  assert.equal(usage.rateLimits.individualLimit.remainingPercent, 75)
  assert.equal(usage.rateLimitsByLimitId.spark.limitName, 'Codex Spark')
  assert.equal(usage.tokenUsage.summary.lifetimeTokens, '12345678901234567890')
  assert.equal(usage.tokenUsage.summary.peakDailyTokens, '456')
  assert.equal(usage.tokenUsage.dailyUsageBuckets[0].tokens, '42')
  assert.match(usage.rawRateLimits, /rateLimits/)
  assert.match(usage.rawTokenUsage, /lifetimeTokens/)

  const args = readFileSync(argsFile, 'utf8').trim().split('\n')
  assert.deepEqual(args, [
    '-s',
    'read-only',
    '-a',
    'untrusted',
    'app-server',
    '--stdio',
  ])
  assert.equal(readFileSync(envFile, 'utf8'), `${codexHome}\n`)
})

test('getAccountUsage timeoutMs bounds slow JSON-RPC reads', async () => {
  const dir = mkdtempSync(join(tmpdir(), 'codexcw-usage-timeout-'))
  const fake = join(dir, 'codex')
  writeFileSync(
    fake,
    `#!/bin/sh
set -eu
while IFS= read -r line; do
  case "$line" in
    *'"method":"initialized"'*) ;;
    *'"method":"initialize"'*) printf '%s\\n' '{"id":1,"result":{}}' ;;
    *'"method":"account/rateLimits/read"'*)
      printf '%s\\n' '{"id":2,"result":{"rateLimits":{"planType":"pro"}}}'
      ;;
    *'"method":"account/usage/read"'*) sleep 5 >/dev/null 2>&1 ;;
  esac
done
`,
  )
  chmodSync(fake, 0o755)

  await assert.rejects(
    getAccountUsage({ executable: fake, timeoutMs: 200 }),
    (err) => {
      assert.ok(err instanceof CodexcwError)
      assert.equal(err.kind, 'process')
      assert.match(err.message, /timeout waiting for account\/usage\/read/)
      return true
    },
  )
})

const invalidAccountUsageTimeouts = [
  -1,
  -Number.MIN_VALUE,
  Number.NaN,
  Number.POSITIVE_INFINITY,
  Number.NEGATIVE_INFINITY,
  2 ** 64 * 1000,
  Number.MAX_VALUE,
]

test('getAccountUsage rejects invalid timeoutMs values', async () => {
  for (const timeoutMs of invalidAccountUsageTimeouts) {
    await assert.rejects(getAccountUsage({ timeoutMs }), (err) => {
      assert.ok(err instanceof CodexcwError)
      assert.equal(err.kind, 'invalidRequest')
      assert.match(
        err.message,
        /account usage timeoutMs must be finite, non-negative/,
      )
      return true
    })
  }
})

test('getAccountUsageRaw preserves invalid timeoutMs errors', async () => {
  for (const timeoutMs of invalidAccountUsageTimeouts) {
    const outcome = await native.getAccountUsageRaw({ timeoutMs })

    assert.equal(outcome.result, undefined)
    assert.equal(outcome.error.kind, 'invalidRequest')
    assert.match(
      outcome.error.message,
      /account usage timeoutMs must be finite, non-negative/,
    )
  }
})

test('ESM entrypoint re-exports getAccountUsage', async () => {
  const esm = await import('../index.mjs')
  assert.equal(typeof esm.getAccountUsage, 'function')
})

test(
  'live getAccountUsage and fast-mode config use real codex',
  { skip: process.env.CODEXCW_LIVE_CODEX !== '1' },
  async () => {
    const usage = await getAccountUsage()
    assert.ok(usage.rawRateLimits.length > 0)

    const dir = mkdtempSync(join(tmpdir(), 'codexcw-live-'))
    const runner = new Runner()
    const result = await runner.run({
      prompt: 'Responda exatamente: OK',
      dir,
      ignoreRules: true,
      config: [{ key: 'service_tier', value: '"priority"' }],
    })

    assert.match(result.finalMessage.toUpperCase(), /OK/)
  },
)
