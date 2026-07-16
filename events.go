package codexcw

import (
	"encoding/json"
	"time"
)

// EventType is the top-level type field emitted by codex exec --json.
type EventType string

const (
	// EventThreadStarted is emitted when Codex creates or resumes a thread.
	EventThreadStarted EventType = "thread.started"

	// EventTurnStarted is emitted when a Codex turn begins.
	EventTurnStarted EventType = "turn.started"

	// EventTurnCompleted is emitted when a Codex turn finishes successfully.
	EventTurnCompleted EventType = "turn.completed"

	// EventTurnFailed is emitted when a Codex turn fails.
	EventTurnFailed EventType = "turn.failed"

	// EventItemStarted is emitted when Codex starts a streamed item.
	EventItemStarted EventType = "item.started"

	// EventItemCompleted is emitted when Codex completes a streamed item.
	EventItemCompleted EventType = "item.completed"

	// EventError is emitted for a top-level Codex error.
	EventError EventType = "error"
)

// ItemType is the item.type field inside item.started and item.completed.
type ItemType string

const (
	// ItemAgentMessage carries assistant text.
	ItemAgentMessage ItemType = "agent_message"

	// ItemReasoning carries a Codex reasoning item.
	ItemReasoning ItemType = "reasoning"

	// ItemCommandExecution carries a shell command execution.
	ItemCommandExecution ItemType = "command_execution"

	// ItemFileChange carries file edits made by Codex.
	ItemFileChange ItemType = "file_change"

	// ItemMCPToolCall carries an MCP tool call.
	ItemMCPToolCall ItemType = "mcp_tool_call"

	// ItemWebSearch carries a web search operation.
	ItemWebSearch ItemType = "web_search"

	// ItemPlanUpdate carries a Codex plan update.
	ItemPlanUpdate ItemType = "plan_update"

	// ItemCollabToolCall carries a multi-agent collab tool call
	// (spawn/wait/send between agent threads).
	ItemCollabToolCall ItemType = "collab_tool_call"

	// ItemError carries an item-scoped Codex error.
	ItemError ItemType = "error"
)

// Event is one decoded JSONL event from codex exec.
type Event struct {
	// Type is the top-level Codex event type.
	Type EventType

	// RunID is the wrapper-assigned run id.
	RunID string

	// ThreadID is the Codex thread id once known.
	ThreadID string

	// ReceivedAt is the local time when the line was decoded.
	ReceivedAt time.Time

	// Raw is the original JSON event payload.
	Raw json.RawMessage

	// ThreadStarted is set for EventThreadStarted.
	ThreadStarted *ThreadStartedEvent

	// TurnStarted is set for EventTurnStarted.
	TurnStarted *TurnStartedEvent

	// TurnCompleted is set for EventTurnCompleted.
	TurnCompleted *TurnCompletedEvent

	// TurnFailed is set for EventTurnFailed.
	TurnFailed *TurnFailedEvent

	// ItemStarted is set for EventItemStarted.
	ItemStarted *ItemEvent

	// ItemCompleted is set for EventItemCompleted.
	ItemCompleted *ItemEvent

	// Error is set for EventError.
	Error *CodexErrorEvent
}

// ThreadStartedEvent carries the Codex thread id.
type ThreadStartedEvent struct {
	// ThreadID is the Codex thread identifier.
	ThreadID string `json:"thread_id"`
}

// TurnStartedEvent marks the start of a turn.
type TurnStartedEvent struct{}

// TurnCompletedEvent carries token usage for the completed turn.
type TurnCompletedEvent struct {
	// Usage is the token usage reported by Codex.
	Usage Usage `json:"usage"`
}

// TurnFailedEvent carries the Codex error payload for a failed turn.
type TurnFailedEvent struct {
	// Error describes the failed turn.
	Error ErrorPayload `json:"error"`
}

// CodexErrorEvent carries a top-level Codex error event.
type CodexErrorEvent struct {
	// Message is the human-readable Codex error text.
	Message string `json:"message"`

	// Raw is the raw nested error payload when present.
	Raw json.RawMessage `json:"-"`
}

// ErrorPayload is the common error object used by turn.failed.
type ErrorPayload struct {
	// Message is the human-readable error text.
	Message string `json:"message"`

	// Raw is the raw nested error payload.
	Raw json.RawMessage `json:"-"`
}

// Usage is token usage reported by turn.completed.
type Usage struct {
	// InputTokens is the number of input tokens consumed.
	InputTokens int64 `json:"input_tokens"`

	// CachedInputTokens is the number of cached input tokens.
	CachedInputTokens int64 `json:"cached_input_tokens"`

	// OutputTokens is the number of output tokens produced.
	OutputTokens int64 `json:"output_tokens"`

	// ReasoningOutputTokens is the number of reasoning output tokens.
	ReasoningOutputTokens int64 `json:"reasoning_output_tokens"`

	// TotalTokens is populated when Codex reports a total.
	TotalTokens int64 `json:"total_tokens"`
}

// ItemEvent wraps one item payload.
type ItemEvent struct {
	// Item is the typed projection of the nested item.
	Item Item `json:"item"`
}

// Item is a typed projection of the item payload. Raw remains authoritative.
type Item struct {
	// ID is the Codex item id.
	ID string

	// Type is the Codex item type.
	Type ItemType

	// Status is the item status when Codex reports one.
	Status string

	// Raw is the original nested item payload.
	Raw json.RawMessage

	// Text is assistant text for agent_message items.
	Text string

	// Message is error text for error items.
	Message string

	// Command is the shell command for command_execution items.
	Command string

	// AggregatedOutput is combined command output reported by Codex.
	AggregatedOutput string

	// ExitCode is the command exit code when available.
	ExitCode *int

	// Changes lists file edits for file_change items.
	Changes []FileChange
}

// FileChange describes one file_change entry.
type FileChange struct {
	// Path is the absolute or workspace-relative file path.
	Path string `json:"path"`

	// Kind is the change kind reported by Codex.
	Kind string `json:"kind"`
}
