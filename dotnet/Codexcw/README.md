# C3OSS.Codexcw

Run Codex, Claude Code, and Grok Build non-interactively from .NET.
`C3OSS.Codexcw` wraps `codex exec --json`, Claude `stream-json`, and Grok
`streaming-messages-json` with `--no-auto-update`. It spawns agent processes,
decodes JSONL events, and exposes each run as async event streams, callbacks,
results, and typed exceptions.

Codex defaults are automation-friendly: JSONL streaming, ephemeral sessions,
read-only sandbox, approval policy `never`, color disabled, and the Git
repository check skipped. New Grok runs use read-only sandboxing and `dontAsk`
permissions, and Grok always persists sessions.

The selected agent's executable must be available on `PATH`, authenticated,
and new enough for the wrapped mode: `codex` must support `codex exec --json`,
`claude` must support `--output-format stream-json`, and `grok` must support
`streaming-messages-json`.

## Quickstart

```csharp
using C3OSS.Codexcw;

var runner = new Runner();
var result = await runner.RunAsync(new Request { Prompt = "say hi" });
Console.WriteLine(result.FinalMessage);
```

## Streaming

```csharp
using var session = runner.Start(new Request { Prompt = "summarize this repo" });
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
the Claude and Grok agents, account usage, error handling) live in the repository:
<https://github.com/c3-oss/codexcw/blob/master/docs/examples/csharp.md>.
