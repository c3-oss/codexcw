package codexcw

import (
	"encoding/json"
	"time"
)

// EventType is the normalized top-level event type.
type EventType string

const (
	// EventThreadStarted is emitted when an agent creates or resumes a session.
	EventThreadStarted EventType = "thread.started"

	// EventTurnStarted is emitted when an agent turn begins.
	EventTurnStarted EventType = "turn.started"

	// EventTurnCompleted is emitted when an agent turn finishes successfully.
	EventTurnCompleted EventType = "turn.completed"

	// EventTurnFailed is emitted when an agent turn fails.
	EventTurnFailed EventType = "turn.failed"

	// EventItemStarted is emitted when an agent starts a streamed item.
	EventItemStarted EventType = "item.started"

	// EventItemCompleted is emitted when an agent completes a streamed item.
	EventItemCompleted EventType = "item.completed"

	// EventError is emitted for a top-level agent error.
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

	// ItemToolCall carries a generic tool call from the claude agent.
	ItemToolCall ItemType = "tool_call"

	// ItemCollabToolCall carries a multi-agent collab tool call
	// (spawn/wait/send between agent threads).
	ItemCollabToolCall ItemType = "collab_tool_call"

	// ItemError carries an item-scoped Codex error.
	ItemError ItemType = "error"
)

// Event is one decoded or normalized agent JSONL event.
type Event struct {
	// Type is the normalized top-level event type.
	Type EventType

	// RunID is the wrapper-assigned run id.
	RunID string

	// ThreadID is the agent session or thread id once known.
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

// ThreadStartedEvent carries the agent session or thread id.
type ThreadStartedEvent struct {
	// ThreadID is the agent session or thread identifier.
	ThreadID string `json:"thread_id"`
}

// TurnStartedEvent marks the start of a turn.
type TurnStartedEvent struct{}

// TurnCompletedEvent carries token usage for the completed turn.
type TurnCompletedEvent struct {
	// Usage is the usage reported by the selected agent.
	Usage Usage `json:"usage"`
}

// TurnFailedEvent carries the error payload for a failed agent turn.
type TurnFailedEvent struct {
	// Error describes the failed turn.
	Error ErrorPayload `json:"error"`

	// Usage is the usage reported before the turn failed.
	Usage Usage `json:"usage"`
}

// CodexErrorEvent carries a normalized top-level error event.
type CodexErrorEvent struct {
	// Message is the human-readable agent error text.
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

// Usage is usage information reported for a turn.
type Usage struct {
	// InputTokens is the number of input tokens consumed.
	InputTokens int64 `json:"input_tokens"`

	// CachedInputTokens is the number of cached input tokens.
	CachedInputTokens int64 `json:"cached_input_tokens"`

	// CacheCreationInputTokens is the number of tokens written to Claude's cache.
	CacheCreationInputTokens int64 `json:"cache_creation_input_tokens"`

	// OutputTokens is the number of output tokens produced.
	OutputTokens int64 `json:"output_tokens"`

	// ReasoningOutputTokens is the number of reasoning output tokens.
	ReasoningOutputTokens int64 `json:"reasoning_output_tokens"`

	// TotalTokens is the reported or derived total token count.
	TotalTokens int64 `json:"total_tokens"`

	// TotalCostUSD is the total Claude run cost in US dollars.
	TotalCostUSD float64 `json:"total_cost_usd"`

	// ModelUsage contains Claude usage grouped by model identifier.
	ModelUsage map[string]ModelUsage `json:"model_usage"`
}

// ModelUsage is Claude usage and cost information for one model.
type ModelUsage struct {
	// InputTokens is the number of input tokens consumed.
	InputTokens int64 `json:"input_tokens"`

	// OutputTokens is the number of output tokens produced.
	OutputTokens int64 `json:"output_tokens"`

	// CacheReadInputTokens is the number of tokens read from cache.
	CacheReadInputTokens int64 `json:"cache_read_input_tokens"`

	// CacheCreationInputTokens is the number of tokens written to cache.
	CacheCreationInputTokens int64 `json:"cache_creation_input_tokens"`

	// WebSearchRequests is the number of web search requests.
	WebSearchRequests int64 `json:"web_search_requests"`

	// CostUSD is the model cost in US dollars.
	CostUSD float64 `json:"cost_usd"`

	// ContextWindow is the model context-window size.
	ContextWindow int64 `json:"context_window"`

	// MaxOutputTokens is the model maximum output-token count.
	MaxOutputTokens int64 `json:"max_output_tokens"`
}

// ItemEvent wraps one item payload.
type ItemEvent struct {
	// Item is the typed projection of the nested item.
	Item Item `json:"item"`
}

// Item is a typed projection of the item payload. Raw remains authoritative.
type Item struct {
	// ID is the native or synthesized agent item id.
	ID string

	// Type is the normalized item type.
	Type ItemType

	// Status is the item status when the agent reports one.
	Status string

	// Raw is the original nested item payload.
	Raw json.RawMessage

	// Text is assistant text for agent_message items.
	Text string

	// Message is error text for error items.
	Message string

	// Command is the shell command for command_execution items.
	Command string

	// AggregatedOutput is combined command output reported by the agent.
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

	// Kind is the normalized change kind reported by the agent.
	Kind string `json:"kind"`
}
