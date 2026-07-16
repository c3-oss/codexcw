# Examples

Complete, runnable usage recipes for each implementation. Every page covers the
same recipe set, in both execution modes for that language (sync **and** async
where the language has both).

| Page | Modes shown |
| --- | --- |
| [Go](go.md) | blocking (`Run`) + concurrent (`Start` / `Events`) |
| [Rust](rust.md) | async (`.await`) + blocking (`Runtime::block_on`) |
| [TypeScript / Node.js](typescript.md) | async (Promises / `for await` / callback) |
| [Python](python.md) | sync (`codexcw`) + async (`codexcw.aio`) |

Each page works through: quickstart, streaming events, per-event callbacks,
**resuming a session**, sandbox modes, approval policies, **bypassing the sandbox
and approvals**, bounded-concurrency batches, config overrides, structured
output, Fast mode (`/fast`), working directories, model/profile selection, stdin
input, custom executable/env, account usage and limits, typed error handling,
Claude per-run and account usage, and cancellation.

See the [README](../../README.md) for the cross-language overview and
[AGENTS.md](../../AGENTS.md) for the project guide.
