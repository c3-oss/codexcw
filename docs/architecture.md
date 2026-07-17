# Architecture

`codexcw` is one Rust core wrapped by two thin FFI bindings, plus two fully
native ports — Go at the repo root and .NET in `dotnet/` — shipped to five
registries. The native ports reimplement the core's behavior idiomatically
(no FFI) and stay in lockstep through the shared spec and test fixtures.

## Core (`crates/codexcw`)

The core is an async (tokio) library that defines the shared contract and
implements all of it for the Rust crate and the FFI bindings:

- **`args`** builds the `codex exec` argv and stdin payload from a `Request`,
  faithfully reproducing flag order, the sandbox/approval config quoting, and the
  prompt/stdin wrapping. Inline output schemas are written to a temp file that is
  deleted when the run ends.
- **`claude`** implements the claude agent: it builds the
  `claude -p --output-format stream-json` argv and statefully normalizes the
  Claude event stream into the shared `Event` model (init becomes
  `thread.started` + `turn.started`, tool_use/tool_result pairs become
  `item.started`/`item.completed`, the final result becomes `turn.completed`
  or `turn.failed`), keeping the original Claude JSON in `raw`. Tool calls map
  to the shared item kinds: Bash to `command_execution`, Write/Edit to
  `file_change`, `mcp__*` to `mcp_tool_call`, WebSearch to `web_search`,
  Task/Agent (subagents) to `collab_tool_call`, TodoWrite to `plan_update`,
  and anything else to `tool_call`.
- **`decoder` / `event`** parse each JSONL line into a typed `Event` while keeping
  the original JSON text in `raw` for forward compatibility. Unknown event and
  item types are preserved rather than dropped. The tolerance policy is shared
  by every implementation: a missing optional field keeps its zero value, and a
  field present with an incompatible type fails the line with a decode error.
- **`runner` / `session`** spawn the child process, stream decoded events over a
  bounded channel, capture a stderr tail, and classify the outcome. Error
  precedence matches the original: a runtime error (decode / handler / cancel)
  wins over the process-exit classification, which wins over a trailing Codex
  error event.
- **`group`** runs many requests with a bounded-concurrency semaphore and
  multiplexes their events.

The core depends on no FFI crate, so `cargo publish -p codexcw` ships a clean
library and the public API never leaks `napi`/`pyo3` types.

## Bindings

Both bindings translate the core to their ecosystem and reuse the same
**failed-session / outcome** pattern: methods do not reject on Codex run errors;
instead they return a result paired with an optional typed error that the
language wrapper raises.

- **Node (`bindings/node`)** uses napi-rs. The native classes expose `nextEvent`,
  `wait`, and friends; the hand-written `index.js` adds an `events()` async
  iterator, an optional `run(req, onEvent)` callback (pure JS, no thread-safe
  function), and the `CodexcwError` class.
- **Python (`bindings/python`)** uses PyO3. The native module is synchronous and
  blocks on a shared multi-threaded tokio runtime while releasing the GIL. The
  `codexcw` package adds the `Request` dataclass, typed exceptions, and — in
  `codexcw.aio` — an asyncio facade built on `asyncio.to_thread`.

## Native ports

Two implementations reproduce the same contract without touching the Rust
core, mapping it onto their platform's native process and concurrency
primitives:

- **Go (repo root)** builds on `os/exec` and channels. `cmd.WaitDelay` bounds
  the post-exit stdio drain, and the bounded event channel plus the collector
  goroutine implement the same backpressure contract as the core.
- **.NET (`dotnet/`)** builds on `System.Diagnostics.Process`,
  `System.Threading.Channels`, and `IAsyncEnumerable`. The collector writes to
  an unbounded internal channel that a forwarder task copies into the bounded
  public channel, so `WaitAsync` never depends on event consumption; a 1-second
  bounded drain mirrors Go's `WaitDelay`. Run failures surface as typed
  exceptions rooted at `CodexcwException` carrying the partial `RunResult`.

## Testing

A fake `codex` (and `claude`) shell script emits a fixed JSONL stream and
records its argv/stdin. The Rust integration tests, and the Node, Python, and
C# suites each keep a verbatim copy of the same fixture scripts, so every
implementation decodes identical streams without a real Codex install.
