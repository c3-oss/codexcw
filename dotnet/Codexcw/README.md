# C3OSS.Codexcw

Run the Codex and Claude Code CLIs non-interactively from .NET. `C3OSS.Codexcw`
wraps `codex exec --json` (and `claude -p --output-format stream-json`): it
spawns agent processes, decodes the JSONL event stream, and exposes each run as
async event streams, callbacks, results, and typed exceptions.

Defaults are automation-friendly: JSONL streaming, ephemeral sessions,
read-only sandbox, approval policy `never`, color disabled, and the Git
repository check skipped.

The `codex` (or `claude`) executable must be available on `PATH`,
authenticated, and new enough to support `codex exec --json`.

## Quickstart

```csharp
using C3OSS.Codexcw;

var runner = new Runner();
var result = await runner.RunAsync(new Request { Prompt = "say hi" });
Console.WriteLine(result.FinalMessage);
```

## Streaming

```csharp
var session = runner.Start(new Request { Prompt = "summarize this repo" });
await foreach (var evt in session.Events())
{
    if (evt.ItemCompleted?.Item is { Kind: ItemKind.AgentMessage } item)
    {
        Console.WriteLine(item.Text);
    }
}
var result = await session.WaitAsync();
```

Full recipes (resume, sandbox and approval modes, batches, structured output,
the Claude agent, account usage, error handling) live in the repository:
<https://github.com/c3-oss/codexcw/blob/master/docs/examples/csharp.md>.
