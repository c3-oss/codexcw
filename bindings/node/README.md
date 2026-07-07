# `@c3-oss/codexcw`

Run the [Codex CLI](https://developers.openai.com/codex/cli) non-interactively
from Node.js — spawn `codex exec --json`, decode its JSONL event stream, and
consume runs as results, live event streams, or per-event callbacks. A native
addon backed by a Rust core (napi-rs), with prebuilt binaries for macOS, Linux
(gnu + musl), and Windows.

## Install

```bash
npm install @c3-oss/codexcw
```

The `codex` executable must be on `PATH`, authenticated, and new enough to
support `codex exec --json`.

## Usage

The API is async-only: every call returns a `Promise`, and event streams are
`AsyncIterable`s consumed with `for await`.

```ts
import { Runner } from '@c3-oss/codexcw'

const runner = new Runner()

const result = await runner.run({ prompt: 'say hi' })
console.log(result.finalMessage)
```

```ts
import { getAccountUsage } from '@c3-oss/codexcw'

const usage = await getAccountUsage({
  env: { CODEX_HOME: '/tmp/codex-home' },
})
console.log(usage.rateLimits.primary?.usedPercent)
console.log(usage.tokenUsage?.summary.lifetimeTokens)
```

Full recipes — streaming, resume, sandbox/approval, batches, structured output,
account usage, and error handling — are in
[`docs/examples/typescript.md`](../../docs/examples/typescript.md).

## License

CC0-1.0
