// Smoke tests for @c3-oss/codexcw, driven by a fake `codex` so they run
// without a real Codex install. Unix-only (the fixture is a shell script).

import assert from 'node:assert/strict'
import { mkdtempSync, chmodSync, readFileSync, writeFileSync } from 'node:fs'
import { createRequire } from 'node:module'
import { tmpdir } from 'node:os'
import { join, dirname } from 'node:path'
import { fileURLToPath } from 'node:url'
import { test } from 'node:test'

import {
  Runner,
  CodexcwError,
  getAccountUsage,
  getClaudeAccountUsage,
  ClaudeModel,
  PermissionMode,
} from '../index.js'

const here = dirname(fileURLToPath(import.meta.url))
const require = createRequire(import.meta.url)
const native = require('../binding.js')
const fakeCodex = join(here, 'fixtures', 'fake-codex.sh')
const fakeClaude = join(here, 'fixtures', 'fake-claude.sh')
chmodSync(fakeCodex, 0o755)
chmodSync(fakeClaude, 0o755)

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

test('invalid agent is rejected instead of falling back to codex', () => {
  assert.throws(
    () => new Runner({ agent: 'unknown-agent' }),
    (err) => {
      assert.ok(err instanceof CodexcwError)
      assert.equal(err.kind, 'invalidRequest')
      assert.match(err.message, /unknown agent/)
      return true
    },
  )
  assert.throws(
    () => new native.Runner({ agent: 'unknown-agent' }),
    /unknown agent/,
  )
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

test('claude agent normalizes stream-json events', async () => {
  const dir = mkdtempSync(join(tmpdir(), 'codexcw-claude-'))
  const argsFile = join(dir, 'args.txt')
  const stdinFile = join(dir, 'stdin.txt')
  const runner = new Runner({
    agent: 'claude',
    executable: fakeClaude,
    env: { CODEXCW_ARGS_FILE: argsFile, CODEXCW_STDIN_FILE: stdinFile },
  })

  const result = await runner.run({
    prompt: 'create hello.txt',
    model: ClaudeModel.Haiku,
    permissionMode: PermissionMode.AcceptEdits,
  })

  assert.equal(result.threadId, 'sess-1')
  assert.equal(result.finalMessage, 'Done.')
  assert.equal(result.usage.inputTokens, 18)
  assert.equal(result.usage.cachedInputTokens, 45921)
  assert.equal(result.usage.cacheCreationInputTokens, 3944)
  assert.equal(result.usage.outputTokens, 380)
  assert.equal(result.usage.totalTokens, 50263)
  assert.equal(result.usage.totalCostUsd, 0.013562)
  assert.deepEqual(
    result.usage.modelUsage['claude-haiku-4-5-20251001'],
    {
      inputTokens: 18,
      outputTokens: 380,
      cacheReadInputTokens: 45921,
      cacheCreationInputTokens: 3944,
      webSearchRequests: 0,
      costUsd: 0.013562,
      contextWindow: 200000,
      maxOutputTokens: 32000,
    },
  )
  assert.deepEqual(
    result.events.map((e) => e.type),
    [
      'thread.started',
      'turn.started',
      'item.completed',
      'item.started',
      'item.completed',
      'item.completed',
      'item.completed',
      'turn.completed',
    ],
  )
  const fileChange = result.events[4].item
  assert.equal(fileChange.type, 'file_change')
  assert.equal(fileChange.changes[0].path, '/work/hello.txt')
  assert.equal(fileChange.changes[0].kind, 'add')
  assert.equal(fileChange.aggregatedOutput, 'File created successfully')
  assert.notEqual(result.events[5].item.id, result.events[6].item.id)
  assert.equal(PermissionMode.Auto, 'auto')
  assert.equal(PermissionMode.Manual, 'manual')

  assert.equal(readFileSync(stdinFile, 'utf8'), 'create hello.txt')
  const args = readFileSync(argsFile, 'utf8').trim().split('\n')
  assert.equal(args[0], '-p')
  assert.ok(args.includes('stream-json'))
  assert.ok(args.includes('--verbose'))
  assert.ok(args.includes('--model'))
  assert.ok(args.includes('haiku'))
  assert.ok(args.includes('--permission-mode'))
  assert.ok(args.includes('acceptEdits'))
  assert.ok(args.includes('--no-session-persistence'))
})

test('claude agent rejects codex-only fields', async () => {
  const runner = new Runner({ agent: 'claude', executable: fakeClaude })

  await assert.rejects(
    runner.run({ prompt: 'x', sandbox: 'read-only' }),
    (err) => {
      assert.ok(err instanceof CodexcwError)
      assert.equal(err.kind, 'invalidRequest')
      return true
    },
  )
})

test('claude failures use the claude error kind', async () => {
  const runner = new Runner({
    agent: 'claude',
    executable: fakeClaude,
    env: { CODEXCW_CLAUDE_ERROR: '1' },
  })

  const session = await runner.start({ prompt: 'fail' })
  const events = []
  for await (const event of session.events()) events.push(event)
  await assert.rejects(session.wait(), (err) => {
    assert.ok(err instanceof CodexcwError)
    assert.equal(err.kind, 'claude')
    assert.match(err.message, /Claude fixture failure/)
    return true
  })
  const failed = events.find((event) => event.type === 'turn.failed')
  assert.equal(failed.usage.totalTokens, 10)
})

test('getClaudeAccountUsage reads the Claude usage report', async () => {
  const dir = mkdtempSync(join(tmpdir(), 'codexcw-claude-usage-'))
  const argsFile = join(dir, 'args.txt')
  const stdinFile = join(dir, 'stdin.txt')
  const usage = await getClaudeAccountUsage({
    executable: fakeClaude,
    env: {
      CODEXCW_ARGS_FILE: argsFile,
      CODEXCW_STDIN_FILE: stdinFile,
    },
    timeoutMs: 5000,
  })

  assert.match(usage.report, /currently using your subscription/)
  assert.deepEqual(usage.windows, [
    {
      label: 'Current session',
      usedPercent: 13,
      resetsAt: 'Jul 16 at 3:50pm (America/Sao_Paulo)',
    },
    {
      label: 'Current week (all models)',
      usedPercent: 5,
      resetsAt: 'Jul 18 at 9am (America/Sao_Paulo)',
    },
  ])
  assert.match(usage.raw, /"type":"result"/)
  assert.equal(readFileSync(stdinFile, 'utf8'), '/usage')
  const args = readFileSync(argsFile, 'utf8').trim().split('\n')
  assert.ok(args.includes('-p'))
  assert.ok(args.includes('--output-format'))
  assert.ok(args.includes('json'))
  assert.ok(args.includes('--no-session-persistence'))
})

test('getClaudeAccountUsage rejects invalid timeout values', async () => {
  for (const timeoutMs of [-1, Number.NaN, Number.POSITIVE_INFINITY]) {
    await assert.rejects(
      getClaudeAccountUsage({ timeoutMs }),
      (error) =>
        error instanceof CodexcwError &&
        error.kind === 'invalidRequest' &&
        error.message.includes('finite non-negative'),
    )
  }
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

test('ESM entrypoint re-exports account usage helpers', async () => {
  const esm = await import('../index.mjs')
  assert.equal(typeof esm.getAccountUsage, 'function')
  assert.equal(typeof esm.getClaudeAccountUsage, 'function')
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
