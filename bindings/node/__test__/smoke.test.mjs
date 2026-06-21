// Smoke tests for @c3-oss/codexcw, driven by a fake `codex` so they run
// without a real Codex install. Unix-only (the fixture is a shell script).

import assert from 'node:assert/strict'
import { mkdtempSync, chmodSync, readFileSync } from 'node:fs'
import { tmpdir } from 'node:os'
import { join, dirname } from 'node:path'
import { fileURLToPath } from 'node:url'
import { test } from 'node:test'

import { Runner, CodexcwError } from '../index.js'

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
