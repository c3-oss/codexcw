# codexcw — Go examples

The Go library lives at the module root: `github.com/c3-oss/codexcw`.

```bash
go get github.com/c3-oss/codexcw
```

The `codex` executable must be on `PATH`, authenticated, and new enough to
support `codex exec --json`. Defaults are automation-friendly: read-only sandbox,
approval `never`, ephemeral sessions, color off, git-check skipped.

## Two ways to run

Every recipe works in either mode:

- **Blocking** — `runner.Run(ctx, req)` starts the process, drains its events,
  and returns the final `*Result`.
- **Concurrent** — `runner.Start(ctx, req)` returns immediately with a
  `*Session`; consume `session.Events()` from a goroutine and call
  `session.Wait()` for the result. This is the Go-idiomatic "async" form.

```go
package main

import (
	"context"
	"fmt"
	"log"

	"github.com/c3-oss/codexcw"
)

func main() {
	ctx := context.Background()
	runner := codexcw.New()

	// Blocking.
	result, err := runner.Run(ctx, codexcw.Request{Prompt: "diga oi"})
	if err != nil {
		log.Fatal(err)
	}
	fmt.Println(result.FinalMessage)
}
```

```go
// Concurrent: stream events live, then collect the result.
session, err := runner.Start(ctx, codexcw.Request{Prompt: "resuma este repo"})
if err != nil {
	log.Fatal(err)
}

for event := range session.Events() {
	if event.ItemCompleted != nil && event.ItemCompleted.Item.Type == codexcw.ItemAgentMessage {
		fmt.Println(event.ItemCompleted.Item.Text)
	}
}

result, err := session.Wait()
if err != nil {
	log.Fatal(err)
}
fmt.Println("usage:", result.Usage.TotalTokens)
```

The recipes below use `Run` for brevity; swap in the `Start` + goroutine pattern
above to consume events live.

## Per-event callback

A `Handler` runs for every decoded event. Returning an error cancels the run.

```go
result, err := runner.Run(ctx, codexcw.Request{Prompt: "trabalhe"},
	codexcw.WithHandler(codexcw.HandlerFunc(func(ctx context.Context, e codexcw.Event) error {
		switch e.Type {
		case codexcw.EventItemCompleted:
			if e.ItemCompleted.Item.Type == codexcw.ItemCommandExecution {
				fmt.Println("$", e.ItemCompleted.Item.Command)
			}
		case codexcw.EventTurnCompleted:
			fmt.Println("tokens:", e.TurnCompleted.Usage.TotalTokens)
		}
		return nil
	})),
)
_ = result
_ = err
```

```go
// A handler that aborts the run on the first command execution.
var errStop = errors.New("stop")

_, err := runner.Run(ctx, codexcw.Request{Prompt: "..."},
	codexcw.WithHandler(codexcw.HandlerFunc(func(_ context.Context, e codexcw.Event) error {
		if e.Type == codexcw.EventItemStarted && e.ItemStarted.Item.Type == codexcw.ItemCommandExecution {
			return errStop
		}
		return nil
	})),
)

var handlerErr *codexcw.HandlerError
if errors.As(err, &handlerErr) {
	fmt.Println("cancelled by handler:", handlerErr.Err)
}
```

## Resume a session

Codex sessions are resumable by thread id. Run once, capture
`result.ThreadID`, then continue the same thread with `ResumeID`.

```go
first, err := runner.Run(ctx, codexcw.Request{Prompt: "crie um arquivo TODO.md"})
if err != nil {
	log.Fatal(err)
}
threadID := first.ThreadID

second, err := runner.Run(ctx, codexcw.Request{
	Prompt:   "agora adicione 3 itens ao TODO.md",
	ResumeID: threadID,
})
if err != nil {
	log.Fatal(err)
}
fmt.Println(second.FinalMessage)
```

```go
// Resume the most recent thread instead of tracking ids yourself.
_, _ = runner.Run(ctx, codexcw.Request{Prompt: "continue", ResumeLast: true})

// ResumeAll disables Codex's cwd filtering while resuming.
_, _ = runner.Run(ctx, codexcw.Request{Prompt: "continue", ResumeID: threadID, ResumeAll: true})
```

> Resume runs do **not** accept `Dir`, `AddDirs`, or `Profile` — setting them
> returns `ErrInvalidRequest`.

## Sandbox modes

```go
// Read-only is the default. Let Codex write inside the workspace:
_, _ = runner.Run(ctx, codexcw.Request{
	Prompt:  "refatore o pacote foo",
	Sandbox: codexcw.SandboxWorkspaceWrite,
})

// Remove sandbox filesystem restrictions entirely:
_, _ = runner.Run(ctx, codexcw.Request{
	Prompt:  "...",
	Sandbox: codexcw.SandboxDangerFullAccess,
})
```

## Approval policies

```go
// Defaults to ApprovalNever (no prompts). The safer interactive middle ground:
_, _ = runner.Run(ctx, codexcw.Request{
	Prompt:   "...",
	Sandbox:  codexcw.SandboxWorkspaceWrite,
	Approval: codexcw.ApprovalOnRequest,
})
```

## ⚠️ Bypass sandbox and approvals

> **Danger.** `DangerouslyBypassSandbox` runs Codex with
> `--dangerously-bypass-approvals-and-sandbox`: no sandbox, no approval prompts.
> Only use this in a disposable, fully-trusted environment.

```go
_, _ = runner.Run(ctx, codexcw.Request{
	Prompt:                   "...",
	DangerouslyBypassSandbox: true,
})

// Run enabled hooks without persisted trust:
_, _ = runner.Run(ctx, codexcw.Request{
	Prompt:                 "...",
	DangerouslyBypassHooks: true,
})
```

## Run many with bounded concurrency

```go
group, err := runner.RunMany(ctx, []codexcw.Request{
	{Prompt: "review package A"},
	{Prompt: "review package B"},
	{Prompt: "review package C"},
}, codexcw.WithMaxConcurrent(2))
if err != nil {
	log.Fatal(err)
}

// Multiplexed events across all runs.
for ev := range group.Events() {
	fmt.Printf("[%d] %s\n", ev.Index, ev.Event.Type)
}

results, err := group.Wait() // err is *GroupError if any run failed
for _, r := range results {
	if r.Err != nil {
		fmt.Printf("[%d] failed: %v\n", r.Index, r.Err)
		continue
	}
	fmt.Printf("[%d] %s\n", r.Index, r.Result.FinalMessage)
}
_ = err
```

## Config overrides

Each `ConfigOverride` becomes a `-c key=value` argument.

```go
_, _ = runner.Run(ctx, codexcw.Request{
	Prompt: "...",
	Config: []codexcw.ConfigOverride{
		{Key: "model_reasoning_effort", Value: `"high"`},
		{Key: "tools.web_search", Value: "true"},
	},
})
```

## Structured output

Ask Codex to conform its final message to a JSON Schema, and write it to a file.

```go
schema := []byte(`{"type":"object","properties":{"summary":{"type":"string"}},"required":["summary"]}`)

result, err := runner.Run(ctx, codexcw.Request{
	Prompt:                "resuma o repo como JSON",
	OutputSchema:          schema, // written to a temp file and passed as --output-schema
	OutputLastMessagePath: "out.json",
})
if err != nil {
	log.Fatal(err)
}
fmt.Println(result.FinalMessage) // conforms to the schema
```

## Working directory and extra dirs

```go
_, _ = runner.Run(ctx, codexcw.Request{
	Prompt:  "...",
	Dir:     "/work/project",
	AddDirs: []string{"/work/shared", "/work/vendor"},
})
```

## Model and profile

```go
_, _ = runner.Run(ctx, codexcw.Request{
	Prompt:  "...",
	Model:   "o3",
	Profile: "work",
})
```

## Stdin input

```go
import "strings"

// Prompt via stdin only:
_, _ = runner.Run(ctx, codexcw.Request{Stdin: strings.NewReader("diga oi")})

// Prompt plus extra stdin context (wrapped in <stdin> markers):
_, _ = runner.Run(ctx, codexcw.Request{
	Prompt: "resuma o diff abaixo",
	Stdin:  strings.NewReader(largeDiff),
})
```

## Custom executable and environment

```go
runner := codexcw.New(
	codexcw.WithExecutable("/opt/codex/bin/codex"),
	codexcw.WithEnv("CODEX_HOME=/tmp/codex-home"),
)
```

## Error handling

Errors are typed; use `errors.As` to inspect them.

```go
result, err := runner.Run(ctx, codexcw.Request{Prompt: "..."})
if err != nil {
	var exitErr *codexcw.ExitError
	var codexErr *codexcw.CodexError
	var decodeErr *codexcw.DecodeError
	switch {
	case errors.As(err, &exitErr):
		fmt.Printf("codex exited %d: %s\n", exitErr.Code, exitErr.Stderr)
	case errors.As(err, &codexErr):
		fmt.Println("codex reported an error:", codexErr.Error())
	case errors.As(err, &decodeErr):
		fmt.Printf("bad JSONL on line %d\n", decodeErr.Line)
	case errors.Is(err, codexcw.ErrPromptRequired):
		fmt.Println("prompt or stdin is required")
	default:
		fmt.Println("error:", err)
	}
}
_ = result
```

## Cancellation

```go
// Cancel a streaming session explicitly:
session, _ := runner.Start(ctx, codexcw.Request{Prompt: "..."})
go func() {
	time.Sleep(5 * time.Second)
	_ = session.Cancel()
}()
for range session.Events() {
}
_, _ = session.Wait()

// Or cancel through the context:
ctx, cancel := context.WithTimeout(context.Background(), 30*time.Second)
defer cancel()
_, _ = runner.Run(ctx, codexcw.Request{Prompt: "..."})
```

---

See the [README](../../README.md) for the cross-language overview and
[AGENTS.md](../../AGENTS.md) for the project guide.
