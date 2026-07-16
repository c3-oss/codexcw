# `@c3-oss/codexcw`

Run Codex or Claude Code non-interactively from Node.js. The Codex agent wraps
`codex exec --json`; the Claude agent wraps
`claude -p --output-format stream-json`. Both expose results, live event
streams, callbacks, typed usage, and typed errors through the same API. A
native addon backed by a Rust core (napi-rs) provides prebuilt binaries for
macOS, Linux (gnu + musl), and Windows.

## Install

```bash
npm install @c3-oss/codexcw
```

The selected agent executable must be on `PATH` and authenticated. Codex must
support `codex exec --json`; Claude must support `--output-format stream-json`.

Runners can alternatively wrap Claude Code: `new Runner({ agent: 'claude' })`
spawns `claude -p --output-format stream-json` and normalizes its events into
the same event model, with model selection via the `haiku`/`sonnet`/`opus`
aliases (`ClaudeModel`).

## Usage

The API is async-only: every call returns a `Promise`, and event streams are
`AsyncIterable`s consumed with `for await`.

```ts
import { Runner } from '@c3-oss/codexcw'

const runner = new Runner()

const result = await runner.run({ prompt: 'say hi' })
console.log(result.finalMessage)
console.log(result.usage.totalTokens)
console.log(result.usage.totalCostUsd)
```

```ts
import {
  getAccountUsage,
  getClaudeAccountUsage,
} from '@c3-oss/codexcw'

const usage = await getAccountUsage({
  env: { CODEX_HOME: '/tmp/codex-home' },
})
console.log(usage.rateLimits.primary?.usedPercent)
console.log(usage.tokenUsage?.summary.lifetimeTokens)

const claudeUsage = await getClaudeAccountUsage()
console.log(claudeUsage.windows)
```

Full recipes — streaming, resume, sandbox/approval, batches, structured output,
account usage, and error handling — are in
[`docs/examples/typescript.md`](../../docs/examples/typescript.md).

## License

CC0-1.0
