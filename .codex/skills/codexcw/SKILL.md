---
name: codexcw
description: Run Codex non-interactively from Go, Rust, Node.js, or Python using the codexcw library (a wrapper around `codex exec --json`). Use when building automation that spawns Codex, streams its JSONL events, resumes threads, or controls sandbox/approval policy.
---

# codexcw

`codexcw` wraps `codex exec --json`: it spawns Codex, decodes the JSONL event
stream, and exposes each run as streams, callbacks, results, and typed errors.
A selectable `claude` agent wraps `claude -p --output-format stream-json`
instead, normalizing Claude Code's events into the same event model.
It ships as four independent, idiomatic implementations of the same contract —
pick the one matching your host language.

| Language | Package | Import |
| --- | --- | --- |
| Go | `github.com/c3-oss/codexcw` | `import "github.com/c3-oss/codexcw"` |
| Rust | `codexcw` (crates.io) | `use codexcw::{Runner, Request};` |
| TypeScript | `@c3-oss/codexcw` (npm) | `import { Runner } from '@c3-oss/codexcw'` |
| Python | `codexcw` (PyPI) | `from codexcw import Runner, Request` (+ `codexcw.aio`) |

## Shape of the API (same across languages)

- A **`Runner`** with three entry points: `run` (one-shot → final result),
  `start` (returns a session whose event stream you consume, then `wait`), and
  `run_many` (bounded-concurrency batch).
- A **`Request`** carrying the prompt plus optional knobs (sandbox, approval,
  resume, config overrides, output schema, dir/add-dirs, model/profile, stdin).
- A **typed error** carrying a `kind`/variant (`exit`, `decode`, `codex`,
  `claude`, `handler`, `cancelled`, `invalidRequest`, `promptRequired`,
  `process`).
- Agent-specific **account usage helpers**. The Codex helper reads limits,
  credits, and token usage through `codex app-server`; the Claude helper reads
  the `/usage` report, parsed percentage windows, and raw JSON.

## Safe defaults

Every runner defaults to: read-only sandbox, approval `never`, ephemeral
sessions, JSONL streaming, color disabled, git-repo check skipped. The `codex`
executable must be on `PATH`, authenticated, and support `codex exec --json`.

## Common tasks

- **Resume a thread:** capture `result.thread_id` / `ThreadID` / `threadId` from
  a run, then pass it as `resume_id` on the next request (or use `resume_last`).
  Resume requests must not set `dir`/`add_dirs`/`profile`.
- **Loosen the sandbox:** set `sandbox` to `workspace-write` (or
  `danger-full-access`), optionally with `approval: on-request`.
- **Fast mode (`/fast`):** set the config override `service_tier="priority"`.
- **⚠️ Bypass entirely:** `dangerously_bypass_sandbox` runs with
  `--dangerously-bypass-approvals-and-sandbox`. No sandbox, no approvals — only in
  a disposable, fully-trusted environment.
- **Read account usage:** call the Codex helper (`GetAccountUsage`,
  `get_account_usage`, `getAccountUsage`) or the Claude helper
  (`GetClaudeAccountUsage`, `get_claude_account_usage`,
  `getClaudeAccountUsage`) with optional executable/env overrides and a
  timeout.

## Full recipes

Complete, copy-pasteable examples for every task and execution mode live in the
repo:

- Go — `docs/examples/go.md`
- Rust — `docs/examples/rust.md`
- TypeScript — `docs/examples/typescript.md`
- Python — `docs/examples/python.md`

See also `AGENTS.md` (project guide) and `docs/architecture.md`.
