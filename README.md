# codexcw2

[![CI](https://github.com/c3-oss/codexcw2/actions/workflows/ci.yml/badge.svg)](https://github.com/c3-oss/codexcw2/actions/workflows/ci.yml)
[![License: CC0 1.0](https://img.shields.io/badge/license-CC0%201.0-lightgrey.svg)](LICENSE)

A Rust port of [`codexcw`](https://github.com/c3-oss/codexcw) for running the
Codex CLI non-interactively through `codex exec --json`. A single Rust core
spawns Codex processes, decodes the JSONL event stream, and exposes each run as
async streams, callbacks, results, and typed errors. The core ships as a Rust
crate and as idiomatic TypeScript and Python packages.

| Language   | Package                                                        | Registry  |
| ---------- | ------------------------------------------------------------- | --------- |
| Rust       | [`codexcw`](crates/codexcw)                                   | crates.io |
| TypeScript | [`@c3-oss/codexcw`](bindings/node)                            | npm       |
| Python     | [`codexcw`](bindings/python)                                  | PyPI      |

The `codex` executable must be available on `PATH`, authenticated, and new
enough to support `codex exec --json`. Defaults are automation-friendly: JSONL
streaming, ephemeral sessions, read-only sandbox, approval policy `never`, color
disabled, and the Git repository check skipped.

## Rust

```bash
cargo add codexcw
```

```rust
use codexcw::{Request, Runner};

#[tokio::main]
async fn main() -> Result<(), codexcw::Error> {
    let runner = Runner::new();
    let result = runner.run(Request::new("diga oi")).await?;
    println!("{}", result.final_message);
    Ok(())
}
```

## TypeScript / Node.js

```bash
npm install @c3-oss/codexcw
```

```ts
import { Runner } from '@c3-oss/codexcw'

const runner = new Runner()

// One-shot
const result = await runner.run({ prompt: 'diga oi' })
console.log(result.finalMessage)

// Streaming
const session = await runner.start({ prompt: 'resuma este repo' })
for await (const event of session.events()) {
  if (event.type === 'item.completed' && event.item?.type === 'agent_message') {
    console.log(event.item.text)
  }
}
await session.wait()
```

## Python

```bash
pip install codexcw
```

```python
from codexcw import Runner, Request

runner = Runner()
result = runner.run(Request(prompt="diga oi"))
print(result.final_message)

# Streaming
session = runner.start(Request(prompt="resuma este repo"))
for event in session.events():
    if event.type == "item.completed" and event.item.type == "agent_message":
        print(event.item.text)
session.wait()
```

An async API mirrors the sync one under `codexcw.aio`.

## Running many Codex instances

Every binding exposes bounded-concurrency batches:

```ts
const group = await runner.runMany(
  [{ prompt: 'review package A' }, { prompt: 'review package B' }],
  { maxConcurrent: 2 },
)
for await (const { index, event } of group.events()) {
  console.log(index, event.type)
}
const results = await group.wait()
```

## Development

```bash
devbox shell       # enter the pinned toolchain, install deps, wire hooks
just ci            # local mirror of the PR pipeline
```

| Target            | Purpose                                            |
| ----------------- | -------------------------------------------------- |
| `just build`      | build the core crate                               |
| `just test`       | core unit + integration tests                      |
| `just lint`       | clippy across the workspace                         |
| `just build-node` / `just test-node` | build and test the npm package  |
| `just build-py` / `just test-py`     | build and test the PyPI package |
| `just quality`    | markdown lint, link check, secret scan             |
| `just ci`         | the full local CI lane                             |

See [`AGENTS.md`](AGENTS.md) for the canonical project guide.

## License

[CC0 1.0 Universal](LICENSE).
