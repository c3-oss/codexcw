# codexcw (Python)

Run Codex or Claude Code non-interactively from Python, backed by a Rust core.
The Codex agent wraps `codex exec --json`; the Claude agent wraps
`claude -p --output-format stream-json`. Both expose iterables, callbacks,
results, typed usage, and typed errors through the same API.

The selected agent executable must be on `PATH` and authenticated. Codex must
support `codex exec --json`; Claude must support `--output-format stream-json`.
Defaults are automation-friendly: ephemeral sessions and non-interactive
execution, with Codex using a read-only sandbox and approval `never`.

The Claude agent is selected with `Runner(agent=codexcw.AGENT_CLAUDE)`; its
events are normalized into the same event model, with model selection via the
`haiku`/`sonnet`/`opus` aliases (`CLAUDE_MODEL_*`).

## Install

```bash
pip install codexcw
```

## Usage

```python
from codexcw import Runner, Request

runner = Runner()
result = runner.run(Request(prompt="diga oi"))
print(result.final_message)
print(result.usage.total_tokens)
print(result.usage.total_cost_usd)
```

## Streaming

```python
from codexcw import Runner, Request

runner = Runner()
session = runner.start(Request(prompt="resuma este repo"))
for event in session.events():
    if event.type == "item.completed" and event.item.type == "agent_message":
        print(event.item.text)
result = session.wait()
```

## Account usage

```python
from codexcw import AccountUsageRequest, get_account_usage

usage = get_account_usage(AccountUsageRequest(env={"CODEX_HOME": "/tmp/codex-home"}))
print(usage.rate_limits.primary.used_percent if usage.rate_limits.primary else None)
print(usage.token_usage.summary.lifetime_tokens if usage.token_usage else None)

from codexcw import get_claude_account_usage

claude_usage = get_claude_account_usage()
print(claude_usage.windows)
```

## Running many agent instances

```python
from codexcw import Runner, Request

runner = Runner()
group = runner.run_many(
    [Request(prompt="review package A"), Request(prompt="review package B")],
    max_concurrent=2,
)
for run_event in group.events():
    print(run_event.index, run_event.event.type)
results = group.wait()
```

## Async

```python
import asyncio
from codexcw import Request
from codexcw.aio import Runner

async def main():
    runner = Runner()
    session = await runner.start(Request(prompt="oi"))
    async for event in session.events():
        print(event.type)
    result = await session.wait()
    print(result.final_message)

asyncio.run(main())
```

## License

[CC0 1.0 Universal](https://github.com/c3-oss/codexcw/blob/master/LICENSE).
