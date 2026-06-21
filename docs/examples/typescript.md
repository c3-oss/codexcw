# codexcw — TypeScript / Node.js examples

The npm package is `@c3-oss/codexcw` (in `bindings/node`), a native addon backed
by the Rust core.

```bash
npm install @c3-oss/codexcw
```

The `codex` executable must be on `PATH`, authenticated, and new enough to
support `codex exec --json`. Defaults are automation-friendly: read-only sandbox,
approval `never`, ephemeral sessions, color off, git-check skipped.

> **The API is async-only.** Every call returns a `Promise`, and event streams
> are `AsyncIterable`s consumed with `for await`. There is no synchronous API —
> Node is single-threaded and a blocking native call would freeze the event loop.

## Three ways to run

```ts
import { Runner } from '@c3-oss/codexcw'

const runner = new Runner()

// 1. One-shot: await the final result.
const result = await runner.run({ prompt: 'diga oi' })
console.log(result.finalMessage)
```

```ts
// 2. Streaming: iterate events live, then collect the result.
const session = await runner.start({ prompt: 'resuma este repo' })
for await (const event of session.events()) {
  if (event.type === 'item.completed' && event.item?.type === 'agent_message') {
    console.log(event.item.text)
  }
}
const result = await session.wait()
console.log('tokens:', result.usage.totalTokens)
```

```ts
// 3. Callback: a per-event handler. Throwing cancels the run.
await runner.run({ prompt: 'trabalhe' }, (event) => {
  if (event.type === 'item.completed' && event.item?.type === 'command_execution') {
    console.log('$', event.item.command)
  }
})
```

```ts
// A callback that aborts on the first command execution.
import { CodexcwError } from '@c3-oss/codexcw'

try {
  await runner.run({ prompt: '...' }, (event) => {
    if (event.type === 'item.started' && event.item?.type === 'command_execution') {
      throw new Error('stop')
    }
  })
} catch (err) {
  console.log('cancelled:', (err as Error).message)
}
```

## Resume a session

```ts
const first = await runner.run({ prompt: 'crie um arquivo TODO.md' })
const threadId = first.threadId

const second = await runner.run({
  prompt: 'agora adicione 3 itens ao TODO.md',
  resumeId: threadId,
})
console.log(second.finalMessage)
```

```ts
// Resume the most recent thread:
await runner.run({ prompt: 'continue', resumeLast: true })

// resumeAll disables Codex's cwd filtering while resuming:
await runner.run({ prompt: 'continue', resumeId: threadId, resumeAll: true })
```

> Resume runs reject `dir`, `addDirs`, and `profile` — the rejection surfaces as
> a `CodexcwError` with `kind === 'invalidRequest'`.

## Sandbox modes

```ts
// Read-only is the default. Let Codex write inside the workspace:
await runner.run({ prompt: 'refatore o pacote foo', sandbox: 'workspace-write' })

// Remove sandbox filesystem restrictions:
await runner.run({ prompt: '...', sandbox: 'danger-full-access' })
```

## Approval policies

```ts
// Defaults to 'never' (no prompts). The safer interactive middle ground:
await runner.run({ prompt: '...', sandbox: 'workspace-write', approval: 'on-request' })
```

## ⚠️ Bypass sandbox and approvals

> **Danger.** `dangerouslyBypassSandbox` runs Codex with
> `--dangerously-bypass-approvals-and-sandbox`: no sandbox, no approval prompts.
> Only use this in a disposable, fully-trusted environment.

```ts
await runner.run({ prompt: '...', dangerouslyBypassSandbox: true })

// Run enabled hooks without persisted trust:
await runner.run({ prompt: '...', dangerouslyBypassHooks: true })
```

## Run many with bounded concurrency

```ts
const group = await runner.runMany(
  [
    { prompt: 'review package A' },
    { prompt: 'review package B' },
    { prompt: 'review package C' },
  ],
  { maxConcurrent: 2 },
)

for await (const { index, event } of group.events()) {
  console.log(`[${index}] ${event.type}`)
}

const results = await group.wait()
for (const r of results) {
  if (r.error) {
    console.log(`[${r.index}] failed:`, r.error.kind, r.error.message)
  } else {
    console.log(`[${r.index}] ${r.result?.finalMessage}`)
  }
}
```

## Config overrides

```ts
await runner.run({
  prompt: '...',
  config: [
    { key: 'model_reasoning_effort', value: '"high"' },
    { key: 'tools.web_search', value: 'true' },
  ],
})
```

## Structured output

```ts
const schema = JSON.stringify({
  type: 'object',
  properties: { summary: { type: 'string' } },
  required: ['summary'],
})

const result = await runner.run({
  prompt: 'resuma o repo como JSON',
  outputSchema: schema,
  outputLastMessagePath: 'out.json',
})
console.log(JSON.parse(result.finalMessage))
```

## Working directory and extra dirs

```ts
await runner.run({
  prompt: '...',
  dir: '/work/project',
  addDirs: ['/work/shared', '/work/vendor'],
})
```

## Model and profile

```ts
await runner.run({ prompt: '...', model: 'o3', profile: 'work' })
```

## Stdin input

```ts
// Prompt via stdin only:
await runner.run({ stdin: 'diga oi' })

// Prompt plus extra stdin context (wrapped in <stdin> markers):
await runner.run({ prompt: 'resuma o diff abaixo', stdin: largeDiff })
```

## Custom executable and environment

```ts
const runner = new Runner({
  executable: '/opt/codex/bin/codex',
  env: { CODEX_HOME: '/tmp/codex-home' },
})
```

## Error handling

Failures throw a typed `CodexcwError` whose `kind` discriminates the cause.

```ts
import { CodexcwError } from '@c3-oss/codexcw'

try {
  const result = await runner.run({ prompt: '...' })
  console.log(result.finalMessage)
} catch (err) {
  if (err instanceof CodexcwError) {
    switch (err.kind) {
      case 'exit':
        console.log(`codex exited ${err.code}: ${err.stderr}`)
        break
      case 'codex':
        console.log('codex reported:', err.message)
        break
      case 'decode':
        console.log(`bad JSONL on line ${err.line}`)
        break
      case 'promptRequired':
        console.log('prompt or stdin is required')
        break
      default:
        console.log(err.kind, err.message)
    }
  } else {
    throw err
  }
}
```

## Cancellation

```ts
const session = await runner.start({ prompt: '...' })
setTimeout(() => session.cancel(), 5000)
for await (const _event of session.events()) {
  // ...
}
await session.wait()
```

---

See the [README](../../README.md) for the cross-language overview and
[AGENTS.md](../../AGENTS.md) for the project guide.
