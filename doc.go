// Package codexcw wraps Codex CLI's non-interactive JSONL mode for Go code.
//
// The package starts codex exec --json as a child process, streams decoded
// events through channels or handlers, and keeps raw JSON payloads available
// for callers that need fields not yet modeled by typed helpers.
//
// Account usage helpers read token usage, limits, and credits through codex
// app-server.
package codexcw
