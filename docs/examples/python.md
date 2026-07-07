# codexcw — Python examples

The PyPI package is `codexcw` (in `bindings/python`), a native extension backed
by the Rust core.

```bash
pip install codexcw
```

The `codex` executable must be on `PATH`, authenticated, and new enough to
support `codex exec --json`. Defaults are automation-friendly: read-only sandbox,
approval `never`, ephemeral sessions, color off, git-check skipped.

## Sync and async APIs

The package ships **both**: a synchronous API in `codexcw` and an asyncio API in
`codexcw.aio` with the same shapes. Every recipe below is shown in both.

```python
# Synchronous
from codexcw import Runner, Request

runner = Runner()
result = runner.run(Request(prompt="diga oi"))
print(result.final_message)
```

```python
# Asynchronous (codexcw.aio)
import asyncio
from codexcw import Request
from codexcw.aio import Runner

async def main():
    runner = Runner()
    result = await runner.run(Request(prompt="diga oi"))
    print(result.final_message)

asyncio.run(main())
```

## Streaming events

```python
# Sync
from codexcw import Runner, Request

runner = Runner()
session = runner.start(Request(prompt="resuma este repo"))
for event in session.events():
    if event.type == "item.completed" and event.item.type == "agent_message":
        print(event.item.text)
result = session.wait()
print("tokens:", result.usage.total_tokens)
```

```python
# Async
from codexcw import Request
from codexcw.aio import Runner

runner = Runner()
session = await runner.start(Request(prompt="resuma este repo"))
async for event in session.events():
    if event.type == "item.completed" and event.item.type == "agent_message":
        print(event.item.text)
result = await session.wait()
print("tokens:", result.usage.total_tokens)
```

## Per-event callback

`on_event` runs for every event. Raising cancels the run.

```python
# Sync
def on_event(event):
    if event.type == "item.completed" and event.item.type == "command_execution":
        print("$", event.item.command)

runner.run(Request(prompt="trabalhe"), on_event)
```

```python
# Async — the callback may be a plain function or a coroutine.
async def on_event(event):
    if event.type == "item.completed" and event.item.type == "command_execution":
        print("$", event.item.command)

await runner.run(Request(prompt="trabalhe"), on_event)
```

```python
# A callback that aborts on the first command execution.
from codexcw import CodexcwError

def stop_on_command(event):
    if event.type == "item.started" and event.item.type == "command_execution":
        raise RuntimeError("stop")

try:
    runner.run(Request(prompt="..."), stop_on_command)
except RuntimeError as err:
    print("cancelled:", err)
```

## Resume a session

```python
# Sync
first = runner.run(Request(prompt="crie um arquivo TODO.md"))
thread_id = first.thread_id

second = runner.run(Request(prompt="agora adicione 3 itens ao TODO.md", resume_id=thread_id))
print(second.final_message)
```

```python
# Async
first = await runner.run(Request(prompt="crie um arquivo TODO.md"))
second = await runner.run(Request(prompt="continue", resume_id=first.thread_id))
```

```python
# Resume the most recent thread, or disable cwd filtering with resume_all:
runner.run(Request(prompt="continue", resume_last=True))
runner.run(Request(prompt="continue", resume_id=thread_id, resume_all=True))
```

> Resume runs reject `dir`, `add_dirs`, and `profile` — raised as a
> `CodexcwError` with `kind == "invalidRequest"`.

## Sandbox modes

```python
# Read-only is the default. Let Codex write inside the workspace:
runner.run(Request(prompt="refatore o pacote foo", sandbox="workspace-write"))
await runner.run(Request(prompt="refatore o pacote foo", sandbox="workspace-write"))

# Remove sandbox filesystem restrictions:
runner.run(Request(prompt="...", sandbox="danger-full-access"))
```

## Approval policies

```python
# Defaults to "never". The safer interactive middle ground:
req = Request(prompt="...", sandbox="workspace-write", approval="on-request")
runner.run(req)              # sync
await runner.run(req)        # async
```

## ⚠️ Bypass sandbox and approvals

> **Danger.** `dangerously_bypass_sandbox` runs Codex with
> `--dangerously-bypass-approvals-and-sandbox`: no sandbox, no approval prompts.
> Only use this in a disposable, fully-trusted environment.

```python
runner.run(Request(prompt="...", dangerously_bypass_sandbox=True))
await runner.run(Request(prompt="...", dangerously_bypass_sandbox=True))

# Run enabled hooks without persisted trust:
runner.run(Request(prompt="...", dangerously_bypass_hooks=True))
```

## Run many with bounded concurrency

```python
# Sync
group = runner.run_many(
    [Request(prompt="review A"), Request(prompt="review B"), Request(prompt="review C")],
    max_concurrent=2,
)
for run_event in group.events():
    print(f"[{run_event.index}] {run_event.event.type}")

for r in group.wait():
    if r.error is not None:
        print(f"[{r.index}] failed:", r.error.kind, r.error)
    else:
        print(f"[{r.index}] {r.result.final_message}")
```

```python
# Async
group = await runner.run_many(
    [Request(prompt="review A"), Request(prompt="review B"), Request(prompt="review C")],
    max_concurrent=2,
)
async for run_event in group.events():
    print(f"[{run_event.index}] {run_event.event.type}")
results = await group.wait()
```

## Config overrides

```python
from codexcw import ConfigOverride

req = Request(prompt="...", config=[
    ConfigOverride(key="model_reasoning_effort", value='"high"'),
    ConfigOverride(key="tools.web_search", value="true"),
])
runner.run(req)
await runner.run(req)
```

## Fast mode (`/fast`)

Codex Fast mode uses the ``priority`` service tier.

```python
req = Request(
    prompt="...",
    config=[ConfigOverride(key="service_tier", value='"priority"')],
)
runner.run(req)
await runner.run(req)
```

## Structured output

```python
import json

schema = json.dumps({
    "type": "object",
    "properties": {"summary": {"type": "string"}},
    "required": ["summary"],
})

req = Request(prompt="resuma o repo como JSON", output_schema=schema, output_last_message_path="out.json")
result = runner.run(req)             # sync
print(json.loads(result.final_message))

result = await runner.run(req)       # async
```

## Working directory and extra dirs

```python
req = Request(prompt="...", dir="/work/project", add_dirs=["/work/shared", "/work/vendor"])
runner.run(req)
await runner.run(req)
```

## Model and profile

```python
req = Request(prompt="...", model="o3", profile="work")
runner.run(req)
await runner.run(req)
```

## Stdin input

```python
# Prompt via stdin only:
runner.run(Request(stdin="diga oi"))
await runner.run(Request(stdin="diga oi"))

# Prompt plus extra stdin context (wrapped in <stdin> markers):
runner.run(Request(prompt="resuma o diff abaixo", stdin=large_diff))
```

## Custom executable and environment

```python
runner = Runner(executable="/opt/codex/bin/codex", env={"CODEX_HOME": "/tmp/codex-home"})
```

## Account usage and limits

`get_account_usage` reads account limits and credits through `codex app-server`.
It accepts an executable override and child-process environment. `CODEX_HOME`
defaults to `~/.codex` when it is not set.

```python
# Sync
from codexcw import AccountUsageRequest, get_account_usage

usage = get_account_usage(AccountUsageRequest(env={"CODEX_HOME": "/tmp/codex-home"}))

if usage.account is not None:
    print("account:", usage.account.email)
if usage.rate_limits.primary is not None:
    print("primary used:", usage.rate_limits.primary.used_percent)
if usage.token_usage is not None:
    print("lifetime tokens:", usage.token_usage.summary.lifetime_tokens)
```

```python
# Async
from codexcw import AccountUsageRequest
from codexcw.aio import get_account_usage

usage = await get_account_usage(AccountUsageRequest(env={"CODEX_HOME": "/tmp/codex-home"}))
```

## Error handling

Failures raise a typed `CodexcwError` whose `kind` discriminates the cause.

```python
from codexcw import CodexcwError

try:
    result = runner.run(Request(prompt="..."))   # or: await runner.run(...)
    print(result.final_message)
except CodexcwError as err:
    if err.kind == "exit":
        print(f"codex exited {err.code}: {err.stderr}")
    elif err.kind == "codex":
        print("codex reported:", err)
    elif err.kind == "decode":
        print(f"bad JSONL on line {err.line}")
    elif err.kind == "promptRequired":
        print("prompt or stdin is required")
    else:
        print(err.kind, err)
```

## Cancellation

```python
# Sync
session = runner.start(Request(prompt="..."))
session.cancel()           # stops the child process
session.wait()

# Async
session = await runner.start(Request(prompt="..."))
session.cancel()
await session.wait()
```

---

See the [README](../../README.md) for the cross-language overview and
[AGENTS.md](../../AGENTS.md) for the project guide.
