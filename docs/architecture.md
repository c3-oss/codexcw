# Architecture

`codexcw` is one Rust core wrapped by two thin FFI bindings and shipped to
three registries.

## Core (`crates/codexcw`)

The core is an async (tokio) library that owns all behavior:

- **`args`** builds the `codex exec` argv and stdin payload from a `Request`,
  faithfully reproducing flag order, the sandbox/approval config quoting, and the
  prompt/stdin wrapping. Inline output schemas are written to a temp file that is
  deleted when the run ends.
- **`decoder` / `event`** parse each JSONL line into a typed `Event` while keeping
  the original JSON text in `raw` for forward compatibility. Unknown event and
  item types are preserved rather than dropped.
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

## Testing

A single fake `codex` shell script emits a fixed JSONL stream and records its
argv/stdin. The same fixture drives the Rust integration tests and the Node and
Python smoke tests, so all three decode identically without a real Codex install.
