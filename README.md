# codexcw

[![CI](https://github.com/c3-oss/codexcw/actions/workflows/ci.yml/badge.svg)](https://github.com/c3-oss/codexcw/actions/workflows/ci.yml)
[![License: CC0 1.0](https://img.shields.io/badge/license-CC0%201.0-lightgrey.svg)](LICENSE)

Run the Codex CLI non-interactively through `codex exec --json`: spawn Codex
processes, decode the JSONL event stream, and expose each run as streams,
callbacks, results, and typed errors.

`codexcw` ships as **four independent, idiomatic implementations** of the same
contract — there is no FFI between them; each is native to its ecosystem:

| Language   | Package                        | Install |
| ---------- | ------------------------------ | ------- |
| Go         | `github.com/c3-oss/codexcw`    | `go get github.com/c3-oss/codexcw` |
| Rust       | `codexcw` (crates.io)          | `cargo add codexcw` |
| TypeScript | `@c3-oss/codexcw` (npm)         | `npm install @c3-oss/codexcw` |
| Python     | `codexcw` (PyPI)               | `pip install codexcw` |

The Go library lives at the repo root; the Rust core is in `crates/codexcw`, and
the npm + PyPI bindings (backed by that Rust core) are in `bindings/`.

The `codex` executable must be on `PATH`, authenticated, and new enough to
support `codex exec --json`. Defaults are automation-friendly: JSONL streaming,
ephemeral sessions, read-only sandbox, approval policy `never`, color disabled,
and the Git repository check skipped.

## Go

```go
package main

import (
	"context"
	"fmt"
	"log"

	"github.com/c3-oss/codexcw"
)

func main() {
	runner := codexcw.New()
	result, err := runner.Run(context.Background(), codexcw.Request{Prompt: "diga oi"})
	if err != nil {
		log.Fatal(err)
	}
	fmt.Println(result.FinalMessage)
}
```

## Rust

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

```ts
import { Runner } from '@c3-oss/codexcw'

const runner = new Runner()
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

```python
from codexcw import Runner, Request

runner = Runner()
result = runner.run(Request(prompt="diga oi"))
print(result.final_message)
```

An async API mirrors the sync one under `codexcw.aio`.

## Development

```bash
devbox shell       # enter the pinned toolchain (Go + Rust + Node + Python), wire hooks
just ci            # local mirror of the PR pipeline (all four languages)
```

Recipes are language-namespaced:

| Target | Purpose |
| --- | --- |
| `just go-build` / `go-test` / `go-lint` | Go library |
| `just rust-build` / `rust-test` / `rust-lint` | Rust core |
| `just node-build` / `node-test` | npm package |
| `just py-build` / `py-test` | PyPI package |
| `just quality` | markdown lint, link check, secret scan |
| `just ci` | the full local lane |

See [`AGENTS.md`](AGENTS.md) for the canonical project guide.

## License

[CC0 1.0 Universal](LICENSE).
