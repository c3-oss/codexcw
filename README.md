# codexcw

[![CI](https://github.com/c3-oss/codexcw/actions/workflows/ci.yml/badge.svg)](https://github.com/c3-oss/codexcw/actions/workflows/ci.yml)
[![License: CC0 1.0](https://img.shields.io/badge/license-CC0%201.0-lightgrey.svg)](LICENSE)

Run Codex or Claude Code non-interactively: spawn the selected agent, decode its
JSONL event stream, and expose each run as streams, callbacks, results, and
typed errors.

`codexcw` ships as **five independent, idiomatic implementations** of the same
contract — there is no FFI between them; each is native to its ecosystem:

| Language   | Package                        | Install |
| ---------- | ------------------------------ | ------- |
| Go         | `github.com/c3-oss/codexcw`    | `go get github.com/c3-oss/codexcw` |
| Rust       | `codexcw` (crates.io)          | `cargo add codexcw` |
| TypeScript | `@c3-oss/codexcw` (npm)         | `npm install @c3-oss/codexcw` |
| Python     | `codexcw` (PyPI)               | `pip install codexcw` |
| C# / .NET  | `C3OSS.Codexcw` (NuGet)        | `dotnet add package C3OSS.Codexcw` |

The Go library lives at the repo root; the Rust core is in `crates/codexcw`;
the npm + PyPI bindings (backed by that Rust core) are in `bindings/`; and the
.NET port is in `dotnet/`.

Two agents share the same event model:

- **Codex** (the default) spawns `codex exec --json`. Defaults are
  automation-friendly: JSONL streaming, ephemeral sessions, read-only sandbox,
  approval policy `never`, color disabled, and the Git repository check
  skipped.
- **Claude Code** — the `claude` agent — spawns
  `claude -p --output-format stream-json` and normalizes its events into the
  same event model, with model selection between the `haiku`, `sonnet`, and
  `opus` aliases and Claude permission modes on the request.

The selected agent's executable must be on `PATH` and authenticated: `codex`
new enough to support `codex exec --json`, `claude` new enough to support
`--output-format stream-json`. See the per-language examples in
[`docs/examples/`](docs/examples/).

Account usage is available through agent-specific helpers. The Codex helpers
(`GetAccountUsage`, `get_account_usage`, `getAccountUsage`) call
`codex app-server --stdio` and return limits, credits, and token usage. The
Claude helpers (`GetClaudeAccountUsage`, `get_claude_account_usage`,
`getClaudeAccountUsage`) call Claude Code's `/usage` command and return its
report, parsed percentage windows, and raw JSON. Both accept a custom
executable, environment, and timeout.

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

More examples (resume, sandbox/approval, bypass, batches, …): [`docs/examples/go.md`](docs/examples/go.md).

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

More examples (async + blocking): [`docs/examples/rust.md`](docs/examples/rust.md).

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

More examples: [`docs/examples/typescript.md`](docs/examples/typescript.md).

## Python

```python
from codexcw import Runner, Request

runner = Runner()
result = runner.run(Request(prompt="diga oi"))
print(result.final_message)
```

An async API mirrors the sync one under `codexcw.aio`. More examples (sync +
async): [`docs/examples/python.md`](docs/examples/python.md).

## C# / .NET

```csharp
using C3OSS.Codexcw;

var runner = new Runner();
var result = await runner.RunAsync(new Request { Prompt = "diga oi" });
Console.WriteLine(result.FinalMessage);

// Streaming
var session = runner.Start(new Request { Prompt = "resuma este repo" });
await foreach (var evt in session.Events())
{
    if (evt.ItemCompleted?.Item is { Kind: ItemKind.AgentMessage } item)
    {
        Console.WriteLine(item.Text);
    }
}
await session.WaitAsync();
```

More examples: [`docs/examples/csharp.md`](docs/examples/csharp.md).

## Development

```bash
devbox shell       # enter the pinned toolchain (Go + Rust + Node + Python + .NET), wire hooks
just ci            # local mirror of the PR pipeline (all five languages)
```

Recipes are language-namespaced:

| Target | Purpose |
| --- | --- |
| `just go-build` / `go-test` / `go-lint` | Go library |
| `just rust-build` / `rust-test` / `rust-lint` | Rust core |
| `just node-build` / `node-test` | npm package |
| `just py-build` / `py-test` | PyPI package |
| `just dotnet-build` / `dotnet-test` | NuGet package |
| `just quality` | markdown lint, link check, secret scan |
| `just ci` | the full local lane |

Complete per-language usage recipes (sync **and** async) live in
[`docs/examples/`](docs/examples/). See [`AGENTS.md`](AGENTS.md) for the canonical
project guide.

## License

To the extent possible under law, [Caian Ertl][me] has waived __all copyright
and related or neighboring rights to this work__. In the spirit of _freedom of
information_, I encourage you to fork, modify, change, share, or do whatever
you like with this project! [`^C ^V`][kopimi]

[![License][cc-shield]][cc-url]

[me]: https://github.com/upsetbit
[cc-shield]: https://forthebadge.com/images/badges/cc-0.svg
[cc-url]: http://creativecommons.org/publicdomain/zero/1.0
[kopimi]: https://kopimi.com
