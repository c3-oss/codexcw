# codexcw

[![CI](https://github.com/c3-oss/codexcw/actions/workflows/ci.yml/badge.svg)](https://github.com/c3-oss/codexcw/actions/workflows/ci.yml)
[![Release](https://github.com/c3-oss/codexcw/actions/workflows/release.yml/badge.svg)](https://github.com/c3-oss/codexcw/actions/workflows/release.yml)
[![License: CC0 1.0](https://img.shields.io/badge/license-CC0%201.0-lightgrey.svg)](LICENSE)

`codexcw` is a Go wrapper for running Codex CLI non-interactively through
`codex exec --json`. It spawns Codex processes, decodes the JSONL event stream,
and exposes the run as channels, callbacks, results, and typed errors.

## Install

```bash
go get github.com/c3-oss/codexcw
```

The `codex` executable must be available on `PATH`, authenticated, and new
enough to support `codex exec --json`.

## Library usage

```go
package main

import (
	"context"
	"fmt"
	"log"

	"github.com/c3-oss/codexcw"
)

func main() {
	runner := codexcw.New()
	result, err := runner.Run(context.Background(), codexcw.Request{
		Prompt: "diga oi",
	})
	if err != nil {
		log.Fatal(err)
	}

	fmt.Println(result.FinalMessage)
}
```

Defaults are automation-friendly: JSONL streaming, prompt via stdin, ephemeral
sessions, read-only sandbox, approval policy `never`, color disabled, and the
Git repository check skipped.

## Streaming

```go
session, err := runner.Start(ctx, codexcw.Request{Prompt: "resuma este repo"})
if err != nil {
	return err
}

for event := range session.Events() {
	if event.ItemCompleted != nil && event.ItemCompleted.Item.Type == codexcw.ItemAgentMessage {
		fmt.Println(event.ItemCompleted.Item.Text)
	}
}

result, err := session.Wait()
```

Every event keeps `Raw json.RawMessage` so callers can inspect new Codex event
fields before the wrapper adds typed helpers.

## Running many Codex instances

```go
group, err := runner.RunMany(ctx, []codexcw.Request{
	{Prompt: "review package A"},
	{Prompt: "review package B"},
}, codexcw.WithMaxConcurrent(2))
if err != nil {
	return err
}

for event := range group.Events() {
	fmt.Printf("[%d] %s\n", event.Index, event.Event.Type)
}

results, err := group.Wait()
```

## CLI example

The repository also builds a small example binary:

```bash
codexcw run "diga oi"
printf 'diga oi' | codexcw run
```

`codexcw run` prints the final agent message to stdout and progress to stderr.

## Quick reference

```bash
just build         # compile all cmd/* into bin/
just run           # build then run the default binary
just test-race     # full race detector
just lint          # golangci-lint v2
just lint-sec      # gosec
just lint-vuln     # govulncheck
just quality       # markdown + link check + secret scan
just ci            # local mirror of the PR pipeline
just snapshot      # goreleaser --snapshot (writes dist/ with SBOMs)
just docker-build  # build the local Docker image
```

See [`AGENTS.md`](AGENTS.md) for the canonical project guide.

## License

[CC0 1.0 Universal](LICENSE).
