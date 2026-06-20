package codexcw

import (
	"encoding/json"
	"time"
)

// EventType is the top-level type field emitted by codex exec --json.
type EventType string

const (
	EventThreadStarted EventType = "thread.started"
	EventTurnStarted   EventType = "turn.started"
	EventTurnCompleted EventType = "turn.completed"
	EventTurnFailed    EventType = "turn.failed"
	EventItemStarted   EventType = "item.started"
	EventItemCompleted EventType = "item.completed"
	EventError         EventType = "error"
)

// ItemType is the item.type field inside item.started and item.completed.
type ItemType string

const (
	ItemAgentMessage     ItemType = "agent_message"
	ItemReasoning        ItemType = "reasoning"
	ItemCommandExecution ItemType = "command_execution"
	ItemFileChange       ItemType = "file_change"
	ItemMCPToolCall      ItemType = "mcp_tool_call"
	ItemWebSearch        ItemType = "web_search"
	ItemPlanUpdate       ItemType = "plan_update"
	ItemError            ItemType = "error"
)

// Event is one decoded JSONL event from codex exec.
type Event struct {
	Type       EventType
	RunID      string
	ThreadID   string
	ReceivedAt time.Time
	Raw        json.RawMessage

	ThreadStarted *ThreadStartedEvent
	TurnStarted   *TurnStartedEvent
	TurnCompleted *TurnCompletedEvent
	TurnFailed    *TurnFailedEvent
	ItemStarted   *ItemEvent
	ItemCompleted *ItemEvent
	Error         *CodexErrorEvent
}

// ThreadStartedEvent carries the Codex thread id.
type ThreadStartedEvent struct {
	ThreadID string `json:"thread_id"`
}

// TurnStartedEvent marks the start of a turn.
type TurnStartedEvent struct{}

// TurnCompletedEvent carries token usage for the completed turn.
type TurnCompletedEvent struct {
	Usage Usage `json:"usage"`
}

// TurnFailedEvent carries the Codex error payload for a failed turn.
type TurnFailedEvent struct {
	Error ErrorPayload `json:"error"`
}

// CodexErrorEvent carries a top-level Codex error event.
type CodexErrorEvent struct {
	Message string          `json:"message"`
	Raw     json.RawMessage `json:"-"`
}

// ErrorPayload is the common error object used by turn.failed.
type ErrorPayload struct {
	Message string          `json:"message"`
	Raw     json.RawMessage `json:"-"`
}

// Usage is token usage reported by turn.completed.
type Usage struct {
	InputTokens           int64 `json:"input_tokens"`
	CachedInputTokens     int64 `json:"cached_input_tokens"`
	OutputTokens          int64 `json:"output_tokens"`
	ReasoningOutputTokens int64 `json:"reasoning_output_tokens"`
	TotalTokens           int64 `json:"total_tokens"`
}

// ItemEvent wraps one item payload.
type ItemEvent struct {
	Item Item `json:"item"`
}

// Item is a typed projection of the item payload. Raw remains authoritative.
type Item struct {
	ID     string
	Type   ItemType
	Status string
	Raw    json.RawMessage

	Text             string
	Message          string
	Command          string
	AggregatedOutput string
	ExitCode         *int
	Changes          []FileChange
}

// FileChange describes one file_change entry.
type FileChange struct {
	Path string `json:"path"`
	Kind string `json:"kind"`
}
