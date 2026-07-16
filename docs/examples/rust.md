# codexcw — Rust examples

The Rust core crate is `codexcw` (crates.io), in `crates/codexcw`.

```bash
cargo add codexcw
# the streaming recipes also use:
cargo add tokio --features macros,rt-multi-thread
cargo add tokio-stream
```

The `codex` executable must be on `PATH`, authenticated, and new enough to
support `codex exec --json`. Defaults are automation-friendly: read-only sandbox,
approval `never`, ephemeral sessions, color off, git-check skipped.

## Async vs blocking

The core is async (tokio). Use `.await` inside an async runtime, or wrap a call
in `Runtime::block_on` for synchronous callers.

```rust
use codexcw::{Request, Runner};

// Async (the idiomatic form).
#[tokio::main]
async fn main() -> Result<(), codexcw::Error> {
    let runner = Runner::new();
    let result = runner.run(Request::new("diga oi")).await?;
    println!("{}", result.final_message);
    Ok(())
}
```

```rust
// Blocking: drive the same async call from sync code.
fn main() -> Result<(), Box<dyn std::error::Error>> {
    let rt = tokio::runtime::Runtime::new()?;
    let runner = codexcw::Runner::new();
    let result = rt.block_on(runner.run(codexcw::Request::new("diga oi")))?;
    println!("{}", result.final_message);
    Ok(())
}
```

Every recipe below shows the `.await` form; wrap it in `rt.block_on(...)` for the
blocking equivalent.

## Streaming events

```rust
use codexcw::{EventPayload, ItemKind, Request, Runner};
use tokio_stream::StreamExt;

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
println!("tokens: {}", result.usage.total_tokens);
```

## Per-event callback

`RunOptions::with_handler` registers an async callback for every event. Return
`Err` to cancel the run (surfaced as `Error::Handler`).

```rust
use codexcw::{handler, EventPayload, ItemKind, Request, RunOptions, Runner};

let runner = Runner::new();
let opts = RunOptions::with_handler(handler(|event| async move {
    if let EventPayload::ItemCompleted(item) = &event.payload {
        if item.kind == ItemKind::CommandExecution {
            println!("$ {}", item.command);
        }
    }
    Ok(())
}));

let result = runner.run_opts(Request::new("trabalhe"), opts).await?;
let _ = result;
```

```rust
// A handler that aborts on the first command execution.
let opts = RunOptions::with_handler(handler(|event| async move {
    match &event.payload {
        EventPayload::ItemStarted(item) if item.kind == ItemKind::CommandExecution => {
            Err("stop".to_string())
        }
        _ => Ok(()),
    }
}));

match runner.run_opts(Request::new("..."), opts).await {
    Err(codexcw::Error::Handler(msg)) => println!("cancelled by handler: {msg}"),
    other => { let _ = other; }
}
```

## Resume a session

```rust
let runner = Runner::new();

let first = runner.run(Request::new("crie um arquivo TODO.md")).await?;
let thread_id = first.thread_id.clone();

let second = runner
    .run(Request {
        prompt: "agora adicione 3 itens ao TODO.md".to_string(),
        resume_id: Some(thread_id),
        ..Default::default()
    })
    .await?;
println!("{}", second.final_message);
```

```rust
// Resume the most recent thread:
runner.run(Request { prompt: "continue".into(), resume_last: true, ..Default::default() }).await?;

// ResumeAll disables Codex's cwd filtering while resuming:
runner.run(Request {
    prompt: "continue".into(),
    resume_id: Some(thread_id),
    resume_all: true,
    ..Default::default()
}).await?;
```

> Resume runs reject `dir`, `add_dirs`, and `profile` with
> `Error::InvalidRequest`.

## Sandbox modes

```rust
use codexcw::{Request, SandboxMode};

// Read-only is the default. Let Codex write inside the workspace:
runner.run(Request::new("refatore o módulo foo").sandbox(SandboxMode::WorkspaceWrite)).await?;

// Remove sandbox filesystem restrictions:
runner.run(Request::new("...").sandbox(SandboxMode::DangerFullAccess)).await?;
```

## Approval policies

```rust
use codexcw::{ApprovalPolicy, Request, SandboxMode};

// Defaults to ApprovalPolicy::Never. The safer interactive middle ground:
let req = Request::new("...")
    .sandbox(SandboxMode::WorkspaceWrite)
    .approval(ApprovalPolicy::OnRequest);
runner.run(req).await?;
```

## ⚠️ Bypass sandbox and approvals

> **Danger.** `dangerously_bypass_sandbox` runs Codex with
> `--dangerously-bypass-approvals-and-sandbox`: no sandbox, no approval prompts.
> Only use this in a disposable, fully-trusted environment.

```rust
runner.run(Request {
    prompt: "...".to_string(),
    dangerously_bypass_sandbox: true,
    ..Default::default()
}).await?;

// Run enabled hooks without persisted trust:
runner.run(Request {
    prompt: "...".to_string(),
    dangerously_bypass_hooks: true,
    ..Default::default()
}).await?;
```

## Run many with bounded concurrency

```rust
use codexcw::{ManyOptions, Request, Runner};
use tokio_stream::StreamExt;

let runner = Runner::new();
let mut group = runner
    .run_many(
        vec![
            Request::new("review package A"),
            Request::new("review package B"),
            Request::new("review package C"),
        ],
        ManyOptions { max_concurrent: 2, ..Default::default() },
    )
    .await;

let mut events = group.events();
while let Some(run_event) = events.next().await {
    println!("[{}] {}", run_event.index, run_event.event.kind);
}

match group.wait().await {
    Ok(results) => {
        for r in results {
            if let Some(report) = r.result {
                println!("[{}] {}", r.index, report.final_message);
            }
        }
    }
    Err(group_error) => {
        for r in group_error.results {
            if let Some(err) = r.error {
                println!("[{}] failed: {err}", r.index);
            }
        }
    }
}
```

## Config overrides

```rust
use codexcw::{ConfigOverride, Request};

runner.run(Request {
    prompt: "...".to_string(),
    config: vec![
        ConfigOverride::new("model_reasoning_effort", "\"high\""),
        ConfigOverride::new("tools.web_search", "true"),
    ],
    ..Default::default()
}).await?;
```

## Fast mode (`/fast`)

Codex Fast mode uses the `priority` service tier.

```rust
runner.run(Request {
    prompt: "...".to_string(),
    config: vec![ConfigOverride::new("service_tier", "\"priority\"")],
    ..Default::default()
}).await?;
```

## Structured output

```rust
let schema = br#"{"type":"object","properties":{"summary":{"type":"string"}},"required":["summary"]}"#;

let result = runner.run(Request {
    prompt: "resuma o repo como JSON".to_string(),
    output_schema: Some(schema.to_vec()),
    output_last_message_path: Some("out.json".to_string()),
    ..Default::default()
}).await?;
println!("{}", result.final_message);
```

## Working directory and extra dirs

```rust
runner.run(Request {
    prompt: "...".to_string(),
    dir: Some("/work/project".to_string()),
    add_dirs: vec!["/work/shared".to_string(), "/work/vendor".to_string()],
    ..Default::default()
}).await?;
```

## Model and profile

```rust
runner.run(Request {
    prompt: "...".to_string(),
    model: Some("o3".to_string()),
    profile: Some("work".to_string()),
    ..Default::default()
}).await?;
```

## Claude Code agent

The runner also wraps Claude Code's non-interactive mode
(`claude -p --output-format stream-json`). Select it on the builder; the
`claude` executable must be on `PATH` and authenticated. Events are normalized
into the same [`Event`] model — `thread.started` carries the Claude session
id, tool calls become `item.started`/`item.completed` pairs, and the final
`result` maps to `turn.completed` — with `raw` always keeping the original
Claude JSON line.

```rust
use codexcw::{claude_model, permission_mode, Agent, Request, Runner};

let runner = Runner::builder().agent(Agent::Claude).build();

let result = runner
    .run(Request {
        prompt: "crie um arquivo TODO.md".to_string(),
        model: Some(claude_model::HAIKU.to_string()), // HAIKU, SONNET, or OPUS
        permission_mode: Some(permission_mode::ACCEPT_EDITS.to_string()),
        ..Default::default()
    })
    .await?;
```

```rust
// Tool filters and resume work per request:
let _ = runner
    .run(Request {
        prompt: "rode os testes".to_string(),
        model: Some(claude_model::SONNET.to_string()),
        allowed_tools: vec!["Bash(cargo test *)".to_string(), "Read".to_string()],
        disallowed_tools: vec!["WebSearch".to_string()],
        ..Default::default()
    })
    .await;

let first = runner
    .run(Request {
        prompt: "lembre disto".to_string(),
        persistent: true,
        ..Default::default()
    })
    .await?;
let _ = runner
    .run(Request {
        prompt: "continue".to_string(),
        resume_id: Some(first.thread_id.clone()), // or resume_last: true
        persistent: true,
        ..Default::default()
    })
    .await;
```

Claude runs support `dir` (applied as the process working directory),
`add_dirs`, `output_schema`/`output_schema_path` (passed as `--json-schema`),
and `dangerously_bypass_sandbox` (passed as
`--dangerously-skip-permissions`). `permission_mode`, `allowed_tools`, and
`disallowed_tools` are claude-only; codex-only fields (`sandbox`, `approval`,
`profile`, `config`, `images`, feature flags) return
`Error::InvalidRequest` on a claude runner.

## Stdin input

```rust
// Prompt via stdin only:
runner.run(Request { stdin: Some(b"diga oi".to_vec()), ..Default::default() }).await?;

// Prompt plus extra stdin context (wrapped in <stdin> markers):
runner.run(Request::new("resuma o diff abaixo").stdin(large_diff.into_bytes())).await?;
```

## Custom executable and environment

```rust
let runner = Runner::builder()
    .executable("/opt/codex/bin/codex")
    .env("CODEX_HOME", "/tmp/codex-home")
    .build();
```

## Account usage and limits

`get_account_usage` reads account limits and credits through `codex app-server`.
It accepts an executable override and child-process environment. `CODEX_HOME`
defaults to `~/.codex` when it is not set. `timeout` bounds each JSON-RPC
request; `None` uses the 10-second default.

```rust
use std::time::Duration;

use codexcw::{get_account_usage, AccountUsageRequest};

let usage = get_account_usage(AccountUsageRequest {
    env: vec![("CODEX_HOME".to_string(), "/tmp/codex-home".to_string())],
    timeout: Some(Duration::from_secs(5)),
    ..Default::default()
}).await?;

if let Some(account) = &usage.account {
    println!("account: {}", account.email);
}
if let Some(primary) = &usage.rate_limits.primary {
    println!("primary used: {}", primary.used_percent);
}
if let Some(token_usage) = &usage.token_usage {
    println!("lifetime tokens: {:?}", token_usage.summary.lifetime_tokens);
}
```

`account` and `token_usage` are `None` when codex answers those reads with a
JSON-RPC error; transport errors and timeouts fail the whole call.

## Error handling

```rust
use codexcw::{Error, Request, Runner};

match runner.run(Request::new("...")).await {
    Ok(result) => println!("{}", result.final_message),
    Err(Error::Exit { code, stderr, .. }) => println!("codex exited {code}: {stderr}"),
    Err(Error::Codex { message, .. }) => println!("codex reported: {message}"),
    Err(Error::Decode { line, .. }) => println!("bad JSONL on line {line}"),
    Err(Error::PromptRequired) => println!("prompt or stdin is required"),
    Err(other) => println!("error: {other}"),
}
```

## Cancellation

```rust
let session = runner.start(Request::new("...")).await?;
session.cancel(); // stops the child process; wait() then reports the outcome
let _ = session.wait().await;
```

---

See the [README](../../README.md) for the cross-language overview and
[AGENTS.md](../../AGENTS.md) for the project guide.
