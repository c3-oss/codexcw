// Package codexcw wraps Codex CLI's non-interactive JSONL mode for Go code.
//
// The package starts codex exec --json as a child process, streams decoded
// events through channels or handlers, and keeps raw JSON payloads available
// for callers that need fields not yet modeled by typed helpers.
package codexcw
