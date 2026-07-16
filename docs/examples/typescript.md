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

## Fast mode (`/fast`)

Codex Fast mode uses the `priority` service tier.

```ts
await runner.run({
  prompt: '...',
  config: [{ key: 'service_tier', value: '"priority"' }],
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

## Claude Code agent

The runner also wraps Claude Code's non-interactive mode
(`claude -p --output-format stream-json`). Select it with the `agent` runner
option; the `claude` executable must be on `PATH` and authenticated. Events
are normalized into the same event model — `thread.started` carries the
Claude session id, tool calls become `item.started`/`item.completed` pairs,
and the final `result` maps to `turn.completed` — with `raw` always keeping
the original Claude JSON line.

```ts
import { Runner, ClaudeModel, PermissionMode } from '@c3-oss/codexcw'

const runner = new Runner({ agent: 'claude' })

const result = await runner.run({
  prompt: 'crie um arquivo TODO.md',
  model: ClaudeModel.Haiku, // 'haiku', 'sonnet', or 'opus'
  permissionMode: PermissionMode.AcceptEdits,
})

console.log('tokens:', result.usage.totalTokens)
console.log('cache writes:', result.usage.cacheCreationInputTokens)
console.log('cost (USD):', result.usage.totalCostUsd)
console.log('per-model usage:', result.usage.modelUsage)
```

```ts
// Tool filters and resume work per request:
await runner.run({
  prompt: 'rode os testes',
  model: ClaudeModel.Sonnet,
  allowedTools: ['Bash(npm test *)', 'Read'],
  disallowedTools: ['WebSearch'],
})

const first = await runner.run({ prompt: 'lembre disto', persistent: true })
await runner.run({
  prompt: 'continue',
  resumeId: first.threadId, // or resumeLast: true
  persistent: true,
})
```

Claude runs support `dir` (applied as the process working directory),
`addDirs`, `outputSchema`/`outputSchemaPath` (passed as `--json-schema`), and
`dangerouslyBypassSandbox` (passed as `--dangerously-skip-permissions`).
`permissionMode`, `allowedTools`, and `disallowedTools` are claude-only;
codex-only fields (`sandbox`, `approval`, `profile`, `config`, `images`,
feature flags) reject with an `invalidRequest` error on a claude runner.
`PermissionMode` includes all Claude modes: `AcceptEdits`, `Auto`,
`BypassPermissions`, `Manual`, `DontAsk`, and `Plan`.

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

## Account usage and limits

`getAccountUsage` reads account limits and credits through `codex app-server`.
It accepts an executable override and child-process environment. `CODEX_HOME`
defaults to `~/.codex` when it is not set. `timeoutMs` bounds each JSON-RPC
request and defaults to 10 seconds.

```ts
import { getAccountUsage } from '@c3-oss/codexcw'

const usage = await getAccountUsage({
  env: { CODEX_HOME: '/tmp/codex-home' },
  timeoutMs: 5000,
})

if (usage.account) {
  console.log('account:', usage.account.email)
}
if (usage.rateLimits.primary) {
  console.log('primary used:', usage.rateLimits.primary.usedPercent)
}
if (usage.tokenUsage) {
  console.log('lifetime tokens:', usage.tokenUsage.summary.lifetimeTokens)
}
```

`account` and `tokenUsage` are undefined when codex answers those reads with a
JSON-RPC error; transport errors and timeouts reject the whole call.

Claude account usage is available through the Claude Code `/usage` command:

```ts
import { getClaudeAccountUsage } from '@c3-oss/codexcw'

const usage = await getClaudeAccountUsage({ timeoutMs: 5000 })
console.log(usage.report)
for (const window of usage.windows) {
  console.log(window.label, window.usedPercent, window.resetsAt)
}
```

`raw` keeps the original Claude JSON output, while `windows` contains the
percentage-based limits parsed from the human-readable report.

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
        console.log(`agent exited ${err.code}: ${err.stderr}`)
        break
      case 'codex':
        console.log('codex reported:', err.message)
        break
      case 'claude':
        console.log('claude reported:', err.message)
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
