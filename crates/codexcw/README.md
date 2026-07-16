# codexcw

`codexcw` runs Codex or Claude Code non-interactively. It spawns the selected
agent CLI, decodes the JSONL event stream, and exposes each run as async
streams, callbacks, results, and typed errors.

Two agents share the same `Event` model:

- **Codex** (the default) spawns `codex exec --json`. Defaults are
  automation-friendly: JSONL streaming, ephemeral sessions, read-only sandbox,
  approval policy `never`, color disabled, and the Git repository check
  skipped.
- **Claude Code** — `Runner::builder().agent(Agent::Claude)` — spawns
  `claude -p --output-format stream-json` and normalizes its events into the
  same `Event` model, with model selection via the `haiku`/`sonnet`/`opus`
  aliases (`claude_model`).

The selected agent's executable must be on `PATH` and authenticated: `codex`
new enough to support `codex exec --json`, `claude` new enough to support
`--output-format stream-json`.

Claude permission modes are available through `permission_mode`, including
`AUTO` and `MANUAL`. Completed Claude runs expose cache-creation tokens, total
cost, and per-model usage through `RunResult::usage`. Failed Claude results
surface as `Error::Claude`.

## Usage

```rust,no_run
use codexcw::{Request, Runner};

#[tokio::main]
async fn main() -> Result<(), codexcw::Error> {
    let runner = Runner::new();
    let result = runner.run(Request::new("diga oi")).await?;
    println!("{}", result.final_message);
    Ok(())
}
```

## Streaming

```rust,no_run
use codexcw::{EventPayload, ItemKind, Request, Runner};
use tokio_stream::StreamExt;

# async fn run() -> Result<(), codexcw::Error> {
let runner = Runner::new();
let mut session = runner.start(Request::new("resuma este repo")).await?;
let mut events = session.events();
while let Some(event) = events.next().await {
    if let EventPayload::ItemCompleted(item) = &event.payload {
        if item.kind == ItemKind::AgentMessage {
            println!("{}", item.text);
        }
    }
}
let result = session.wait().await?;
# Ok(())
# }
```

Every event keeps `raw` (the original JSON text) so callers can inspect new
agent event fields before the wrapper adds typed helpers.

## Account usage

```rust,no_run
use codexcw::{get_account_usage, AccountUsageRequest};

# async fn run() -> Result<(), codexcw::Error> {
let usage = get_account_usage(AccountUsageRequest {
    env: vec![("CODEX_HOME".to_string(), "/tmp/codex-home".to_string())],
    ..Default::default()
}).await?;
if let Some(primary) = &usage.rate_limits.primary {
    println!("primary used: {}", primary.used_percent);
}
if let Some(token_usage) = &usage.token_usage {
    println!("lifetime tokens: {:?}", token_usage.summary.lifetime_tokens);
}
# Ok(())
# }
```

Claude account usage is available through its `/usage` command:

```rust,no_run
use codexcw::{get_claude_account_usage, ClaudeAccountUsageRequest};

# async fn run() -> Result<(), codexcw::Error> {
let usage = get_claude_account_usage(ClaudeAccountUsageRequest::default()).await?;
for window in usage.windows {
    println!("{}: {}% used", window.label, window.used_percent);
}
# Ok(())
# }
```

## Running many agent instances

```rust,no_run
use codexcw::{ManyOptions, Request, Runner};

# async fn run() -> Result<(), Box<dyn std::error::Error>> {
let runner = Runner::new();
let mut group = runner
    .run_many(
        vec![Request::new("review package A"), Request::new("review package B")],
        ManyOptions { max_concurrent: 2, ..Default::default() },
    )
    .await;

while let Some(run_event) = group.next_event().await {
    println!("[{}] {}", run_event.index, run_event.event.kind);
}

let results = group.wait().await?;
# let _ = results;
# Ok(())
# }
```

This crate is the Rust core behind the [`@c3-oss/codexcw`][npm] (npm) and
[`codexcw`][pypi] (PyPI) packages.

## License

[CC0 1.0 Universal](https://github.com/c3-oss/codexcw/blob/master/LICENSE).

[npm]: https://www.npmjs.com/package/@c3-oss/codexcw
[pypi]: https://pypi.org/project/codexcw/
