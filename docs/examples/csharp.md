# codexcw — C# examples

The .NET library lives in `dotnet/Codexcw` and is published to NuGet as
`C3OSS.Codexcw` (namespace `C3OSS.Codexcw`) by the `dotnet-v*` release train.

```bash
dotnet add package C3OSS.Codexcw
```

To build against the repository directly, reference the project instead:

```bash
dotnet add reference path/to/codexcw/dotnet/Codexcw/Codexcw.csproj
```

Runners drive Codex (the default agent) or Claude Code; the selected agent's
executable must be on `PATH` and authenticated — `codex` new enough to support
`codex exec --json`, `claude` new enough to support
`--output-format stream-json` (see the Claude agent section below). Codex
defaults are automation-friendly: read-only sandbox, approval `never`,
ephemeral sessions, color off, git-check skipped.

## Two ways to run

Every recipe works in either mode:

- **Blocking** — `runner.RunAsync(request)` starts the process, drains its
  events, and returns the final `RunResult`.
- **Streaming** — `runner.Start(request)` returns immediately with a
  `Session`; consume `session.Events()` with `await foreach` and call
  `session.WaitAsync()` for the result.

```csharp
using C3OSS.Codexcw;

var runner = new Runner();

// Blocking.
var result = await runner.RunAsync(new Request { Prompt = "diga oi" });
Console.WriteLine(result.FinalMessage);
```

```csharp
// Streaming: consume events live, then collect the result.
using var session = runner.Start(new Request { Prompt = "resuma este repo" });

await foreach (var evt in session.Events())
{
    if (evt.ItemCompleted?.Item is { Kind: ItemKind.AgentMessage } item)
    {
        Console.WriteLine(item.Text);
    }
}

var result = await session.WaitAsync();
Console.WriteLine($"usage: {result.Usage.TotalTokens}");
```

The event stream is single-consumer, and waiting never depends on it: calling
`WaitAsync` without reading `Events()` is safe. `Session` and `Group` are
disposable: `Dispose` cancels anything still active and releases the run's
resources, and `await using` (`DisposeAsync`) additionally waits for the
internal tasks to finish. The recipes below use `RunAsync` for brevity; swap
in the `Start` pattern above to consume events live.

## Per-event callback

A `RunOptions.Handler` runs for every decoded event. Throwing cancels the run.

```csharp
var result = await runner.RunAsync(new Request { Prompt = "trabalhe" }, new RunOptions
{
    Handler = (evt, _) =>
    {
        switch (evt.Kind)
        {
            case EventKind.ItemCompleted when evt.ItemCompleted!.Item.Kind == ItemKind.CommandExecution:
                Console.WriteLine($"$ {evt.ItemCompleted.Item.Command}");
                break;
            case EventKind.TurnCompleted:
                Console.WriteLine($"tokens: {evt.TurnCompleted!.Usage.TotalTokens}");
                break;
        }
        return ValueTask.CompletedTask;
    },
});
```

```csharp
// A handler that aborts the run on the first command execution.
try
{
    await runner.RunAsync(new Request { Prompt = "..." }, new RunOptions
    {
        Handler = (evt, _) =>
            evt.ItemStarted?.Item.Kind == ItemKind.CommandExecution
                ? throw new InvalidOperationException("stop")
                : ValueTask.CompletedTask,
    });
}
catch (HandlerException ex)
{
    Console.WriteLine($"cancelled by handler: {ex.InnerException?.Message}");
}
```

## Resume a session

Codex sessions are resumable by thread id, but only persisted ones: runs are
ephemeral by default (`--ephemeral`), so both the original run and the resume
need `Persistent = true`. Run once, capture `result.ThreadId`, then continue
the same thread with `ResumeId`.

```csharp
var first = await runner.RunAsync(new Request
{
    Prompt = "crie um arquivo TODO.md",
    Persistent = true,
});
var threadId = first.ThreadId;

var second = await runner.RunAsync(new Request
{
    Prompt = "agora adicione 3 itens ao TODO.md",
    ResumeId = threadId,
    Persistent = true,
});
Console.WriteLine(second.FinalMessage);
```

```csharp
// Resume the most recent persisted thread instead of tracking ids yourself.
await runner.RunAsync(new Request { Prompt = "continue", ResumeLast = true, Persistent = true });

// ResumeAll disables Codex's cwd filtering while resuming.
await runner.RunAsync(new Request
{
    Prompt = "continue",
    ResumeId = threadId,
    ResumeAll = true,
    Persistent = true,
});
```

> Resume runs do **not** accept `Dir`, `AddDirs`, or `Profile` — setting them
> throws `InvalidRequestException`.

## Sandbox modes

```csharp
// Read-only is the default. Let Codex write inside the workspace:
await runner.RunAsync(new Request
{
    Prompt = "refatore o pacote foo",
    Sandbox = SandboxMode.WorkspaceWrite,
});

// Remove sandbox filesystem restrictions entirely:
await runner.RunAsync(new Request
{
    Prompt = "...",
    Sandbox = SandboxMode.DangerFullAccess,
});
```

## Approval policies

```csharp
// Defaults to ApprovalPolicy.Never (no prompts). The safer interactive middle ground:
await runner.RunAsync(new Request
{
    Prompt = "...",
    Sandbox = SandboxMode.WorkspaceWrite,
    Approval = ApprovalPolicy.OnRequest,
});
```

## ⚠️ Bypass sandbox and approvals

> **Danger.** `DangerouslyBypassSandbox` runs Codex with
> `--dangerously-bypass-approvals-and-sandbox`: no sandbox, no approval prompts.
> Only use this in a disposable, fully-trusted environment.

```csharp
await runner.RunAsync(new Request
{
    Prompt = "...",
    DangerouslyBypassSandbox = true,
});

// Run enabled hooks without persisted trust:
await runner.RunAsync(new Request
{
    Prompt = "...",
    DangerouslyBypassHooks = true,
});
```

## Run many with bounded concurrency

```csharp
using var group = runner.RunMany(
    [
        new Request { Prompt = "review package A" },
        new Request { Prompt = "review package B" },
        new Request { Prompt = "review package C" },
    ],
    new GroupOptions { MaxConcurrent = 2 });

// Multiplexed events across all runs.
await foreach (var runEvent in group.Events())
{
    Console.WriteLine($"[{runEvent.Index}] {runEvent.Event.Type}");
}

try
{
    var results = await group.WaitAsync();
    foreach (var result in results)
    {
        Console.WriteLine($"[{result.Index}] {result.Result!.FinalMessage}");
    }
}
catch (GroupException ex) // thrown when any run failed; all results ride along
{
    foreach (var result in ex.Results)
    {
        Console.WriteLine(result.Error is null
            ? $"[{result.Index}] {result.Result!.FinalMessage}"
            : $"[{result.Index}] failed: {result.Error.Message}");
    }
}
```

## Config overrides

Each `ConfigOverride` becomes a `-c key=value` argument.

```csharp
await runner.RunAsync(new Request
{
    Prompt = "...",
    Config =
    [
        new ConfigOverride("model_reasoning_effort", "\"high\""),
        new ConfigOverride("tools.web_search", "true"),
    ],
});
```

## Fast mode (`/fast`)

Codex Fast mode uses the `priority` service tier.

```csharp
await runner.RunAsync(new Request
{
    Prompt = "...",
    Config = [new ConfigOverride("service_tier", "\"priority\"")],
});
```

## Structured output

Ask Codex to conform its final message to a JSON Schema, and write it to a file.

```csharp
const string schema = """{"type":"object","properties":{"summary":{"type":"string"}},"required":["summary"]}""";

var result = await runner.RunAsync(new Request
{
    Prompt = "resuma o repo como JSON",
    OutputSchema = schema, // written to a temp file and passed as --output-schema
    OutputLastMessagePath = "out.json",
});
Console.WriteLine(result.FinalMessage); // conforms to the schema
```

## Working directory and extra dirs

```csharp
await runner.RunAsync(new Request
{
    Prompt = "...",
    Dir = "/work/project",
    AddDirs = ["/work/shared", "/work/vendor"],
});
```

## Model and profile

Model availability depends on how the `codex` CLI is authenticated (a ChatGPT
account exposes a different set than an API key); a request for an unavailable
model fails the run with a Codex error event.

```csharp
await runner.RunAsync(new Request
{
    Prompt = "...",
    Model = "gpt-5.4-mini",
    Profile = "work",
});
```

## Claude Code agent

The runner also wraps Claude Code's non-interactive mode
(`claude -p --output-format stream-json`). Select it with
`RunnerOptions.Agent`; the `claude` executable must be on `PATH` and
authenticated. Events are normalized into the same `Event` model —
`thread.started` carries the Claude session id, tool calls become
`item.started`/`item.completed` pairs, and the final `result` maps to
`turn.completed` — with `Raw` always keeping the original Claude JSON line.

```csharp
var runner = new Runner(new RunnerOptions { Agent = Agent.Claude });

var result = await runner.RunAsync(new Request
{
    Prompt = "crie um arquivo TODO.md",
    Model = ClaudeModels.Haiku, // "haiku", "sonnet", or "opus"
    PermissionMode = PermissionMode.AcceptEdits,
});

Console.WriteLine($"tokens: {result.Usage.TotalTokens}");
Console.WriteLine($"cost USD: {result.Usage.TotalCostUsd}");
```

```csharp
// Tool filters, structured output, and resume work per request:
await runner.RunAsync(new Request
{
    Prompt = "rode os testes",
    Model = ClaudeModels.Sonnet,
    AllowedTools = ["Bash(dotnet test *)", "Read"],
    DisallowedTools = ["WebSearch"],
});

var first = await runner.RunAsync(new Request { Prompt = "lembre disto", Persistent = true });
await runner.RunAsync(new Request
{
    Prompt = "continue",
    ResumeId = first.ThreadId, // or ResumeLast = true
    Persistent = true,
});
```

Claude runs support `Dir` (applied as the process working directory),
`AddDirs`, `OutputSchema`/`OutputSchemaPath` (passed as `--json-schema`), and
`DangerouslyBypassSandbox` (passed as `--dangerously-skip-permissions`).
`PermissionMode`, `AllowedTools`, and `DisallowedTools` are claude-only;
codex-only fields (`Sandbox`, `Approval`, `Profile`, `Config`, `Images`,
feature flags) throw `InvalidRequestException` on a claude runner.
The permission modes are `AcceptEdits`, `Auto`, `BypassPermissions`, `Manual`,
`DontAsk`, and `Plan`. Claude usage includes cache creation, total cost, and
per-model details in `Usage.ModelUsage`.

Claude account limits are available through the CLI's `/usage` report:

```csharp
var accountUsage = await ClaudeAccount.GetClaudeAccountUsageAsync();
foreach (var window in accountUsage.Windows)
{
    Console.WriteLine($"{window.Label}: {window.UsedPercent:0.#}% used, resets {window.ResetsAt}");
}
```

`ClaudeAccountUsage.Raw` preserves the complete JSON result and `Report`
preserves Claude Code's human-readable response.

## Stdin input

```csharp
using System.Text;

// Prompt via stdin only:
await runner.RunAsync(new Request
{
    Stdin = new MemoryStream(Encoding.UTF8.GetBytes("diga oi")),
});

// Prompt plus extra stdin context (wrapped in <stdin> markers):
await runner.RunAsync(new Request
{
    Prompt = "resuma o diff abaixo",
    Stdin = File.OpenRead("large.diff"),
});
```

## Custom executable and environment

```csharp
var runner = new Runner(new RunnerOptions
{
    Executable = "/opt/codex/bin/codex",
    Env = ["CODEX_HOME=/tmp/codex-home"],
});
```

## Account usage and limits

`CodexAccount.GetAccountUsageAsync` reads account limits and credits through
`codex app-server`. It accepts the same executable/env shape used by runners.
`CODEX_HOME` defaults to `~/.codex` when it is not set. `Timeout` bounds each
JSON-RPC request; non-positive values use the 10-second default.

```csharp
var usage = await CodexAccount.GetAccountUsageAsync(new AccountUsageRequest
{
    Env = new Dictionary<string, string> { ["CODEX_HOME"] = "/tmp/codex-home" },
    Timeout = TimeSpan.FromSeconds(5),
});

if (usage.Account is { } account)
{
    Console.WriteLine($"account: {account.Email}");
}
if (usage.RateLimits.Primary is { } primary)
{
    Console.WriteLine($"primary used: {primary.UsedPercent}");
}
if (usage.TokenUsage is { } tokens)
{
    Console.WriteLine($"lifetime tokens: {tokens.Summary.LifetimeTokens}");
}
```

`Account` and `TokenUsage` are null when codex answers those reads with a
JSON-RPC error; transport errors and timeouts fail the whole call.

## Error handling

Failures are typed exceptions rooted at `CodexcwException`, and every run
failure carries the partial report in `Result`. The one exception is
cancellation: `RunCanceledException` derives from
`OperationCanceledException` — so idiomatic cancellation handling keeps
working — and exposes the same `Result` property.

```csharp
try
{
    var result = await runner.RunAsync(new Request { Prompt = "..." });
}
catch (ExitException ex)
{
    Console.WriteLine($"agent exited {ex.ExitCode}: {ex.Stderr}");
}
catch (CodexErrorException ex)
{
    Console.WriteLine($"codex reported an error: {ex.Message}");
}
catch (ClaudeErrorException ex)
{
    Console.WriteLine($"claude reported an error: {ex.Message}");
}
catch (DecodeException ex)
{
    Console.WriteLine($"bad JSONL on line {ex.Line}");
}
catch (PromptRequiredException)
{
    Console.WriteLine("prompt or stdin is required");
}
catch (CodexcwException ex)
{
    Console.WriteLine($"error: {ex.Message} (events so far: {ex.Result?.Events.Count})");
}
```

## Cancellation

```csharp
// Cancel a streaming session explicitly:
using var session = runner.Start(new Request { Prompt = "..." });
_ = Task.Delay(TimeSpan.FromSeconds(5)).ContinueWith(_ => session.Cancel());
await foreach (var _ in session.Events())
{
}
try
{
    await session.WaitAsync();
}
catch (RunCanceledException ex) // derives OperationCanceledException
{
    Console.WriteLine($"cancelled; events so far: {ex.Result?.Events.Count}");
}

// Or cancel through a CancellationToken:
using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
await runner.RunAsync(new Request { Prompt = "..." }, cancellationToken: cts.Token);
```

---

See the [README](../../README.md) for the cross-language overview and
[AGENTS.md](../../AGENTS.md) for the project guide.
