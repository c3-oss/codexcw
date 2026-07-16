// Package codexcw wraps agent CLIs' non-interactive JSONL modes for Go code.
//
// The package starts codex exec --json (or, with WithAgent(AgentClaude),
// claude -p --output-format stream-json) as a child process, streams decoded
// events through channels or handlers, and keeps raw JSON payloads available
// for callers that need fields not yet modeled by typed helpers. Claude
// events are normalized into the same Event model.
//
// Account usage helpers are agent-specific: GetAccountUsage reads token
// usage, limits, and credits through codex app-server, and
// GetClaudeAccountUsage reads Claude Code's /usage report.
package codexcw
