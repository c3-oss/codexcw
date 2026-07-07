# codexcw (Python)

Run the Codex CLI non-interactively from Python, backed by a Rust core. It
spawns `codex exec --json`, decodes the JSONL event stream, and exposes each run
as iterables, callbacks, results, and typed errors.

The `codex` executable must be on `PATH`, authenticated, and new enough to
support `codex exec --json`. Defaults are automation-friendly: read-only
sandbox, approval `never`, ephemeral sessions, color disabled, git check
skipped.

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
```

## Running many Codex instances

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
