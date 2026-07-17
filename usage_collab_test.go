package codexcw

import (
	"testing"
	"time"

	"github.com/stretchr/testify/assert"
	"github.com/stretchr/testify/require"
)

func TestCodexTurnFailedCarriesUsage(t *testing.T) {
	event, err := decodeEvent(
		[]byte(`{"type":"turn.failed","error":{"message":"boom"},"usage":{"input_tokens":12966,"cached_input_tokens":8960,"output_tokens":6}}`),
		"run", "", time.Now(),
	)
	require.NoError(t, err)
	require.NotNil(t, event.TurnFailed)
	assert.Equal(t, "boom", event.TurnFailed.Error.Message)
	assert.Equal(t, int64(12966), event.TurnFailed.Usage.InputTokens)
	assert.Equal(t, int64(12972), event.TurnFailed.Usage.TotalTokens)
}

func TestCodexTotalTokensDerivedWhenOmitted(t *testing.T) {
	// cached_input_tokens is a subset of input_tokens on the codex wire and
	// must not be counted twice.
	event, err := decodeEvent(
		[]byte(`{"type":"turn.completed","usage":{"input_tokens":12966,"cached_input_tokens":8960,"output_tokens":6,"reasoning_output_tokens":0}}`),
		"run", "", time.Now(),
	)
	require.NoError(t, err)
	assert.Equal(t, int64(12972), event.TurnCompleted.Usage.TotalTokens)

	explicit, err := decodeEvent(
		[]byte(`{"type":"turn.completed","usage":{"input_tokens":10,"output_tokens":3,"total_tokens":999}}`),
		"run", "", time.Now(),
	)
	require.NoError(t, err)
	assert.Equal(t, int64(999), explicit.TurnCompleted.Usage.TotalTokens)
}

func TestCodexCollabItemTypedFields(t *testing.T) {
	event, err := decodeEvent(
		[]byte(`{"type":"item.started","item":{"id":"i0","type":"collab_tool_call","tool":"spawn_agent","sender_thread_id":"t-parent","receiver_thread_ids":["t-child"],"agents_states":{},"status":"in_progress"}}`),
		"run", "", time.Now(),
	)
	require.NoError(t, err)
	item := event.ItemStarted.Item
	assert.Equal(t, ItemCollabToolCall, item.Type)
	assert.Equal(t, "spawn_agent", item.Tool)
	assert.Equal(t, "t-parent", item.SenderThreadID)
	assert.Equal(t, []string{"t-child"}, item.ReceiverThreadIDs)
}

func TestClaudeAgentToolMapsToCollab(t *testing.T) {
	// Current Claude Code CLIs call the subagent tool "Agent"; "Task" is the
	// legacy name. This mirrors a real 2.1.x payload.
	decoder := newClaudeEventDecoder()
	now := time.Now()

	started, err := decoder.decode(
		[]byte(`{"type":"assistant","message":{"id":"msg_1","content":[{"type":"tool_use","id":"toolu_1","name":"Agent","input":{"subagent_type":"Explore","prompt":"reply SUBAGENT_CHILD_OK"}}]},"session_id":"s"}`),
		"run", "s", now,
	)
	require.NoError(t, err)
	require.Len(t, started, 1)
	item := started[0].ItemStarted.Item
	assert.Equal(t, ItemCollabToolCall, item.Type)
	assert.Equal(t, "Agent", item.Tool)

	completed, err := decoder.decode(
		[]byte(`{"type":"user","message":{"content":[{"type":"tool_result","tool_use_id":"toolu_1","content":[{"type":"text","text":"SUBAGENT_CHILD_OK"}]}]},"tool_use_result":{"status":"completed","agentType":"Explore","agentId":"agent-123","resolvedModel":"claude-sonnet-5","totalTokens":12460},"session_id":"s"}`),
		"run", "s", now,
	)
	require.NoError(t, err)
	require.Len(t, completed, 1)
	done := completed[0].ItemCompleted.Item
	assert.Equal(t, ItemCollabToolCall, done.Type)
	assert.Equal(t, "Agent", done.Tool)
	assert.Equal(t, "completed", done.Status)
	assert.Equal(t, []string{"agent-123"}, done.ReceiverThreadIDs)
	assert.Equal(t, "SUBAGENT_CHILD_OK", done.AggregatedOutput)
}

func TestClaudeTotalTokensFromModelUsage(t *testing.T) {
	decoder := newClaudeEventDecoder()
	events, err := decoder.decode(
		[]byte(`{"type":"result","is_error":false,"result":"ok","session_id":"s","total_cost_usd":0.5,"usage":{"input_tokens":100,"cache_creation_input_tokens":200,"cache_read_input_tokens":300,"output_tokens":50},"modelUsage":{"claude-sonnet-5":{"inputTokens":100,"outputTokens":50,"cacheReadInputTokens":300,"cacheCreationInputTokens":200},"claude-haiku-4-5-20251001":{"inputTokens":8000,"outputTokens":460,"cacheReadInputTokens":3000,"cacheCreationInputTokens":1000}}}`),
		"run", "s", time.Now(),
	)
	require.NoError(t, err)

	usage := events[len(events)-1].TurnCompleted.Usage
	assert.Equal(t, int64(100), usage.InputTokens)
	// The root result.usage sums to 650; the subagent's 12460 tokens only
	// appear in modelUsage, and the full-run total must include them.
	assert.Equal(t, int64(650+12460), usage.TotalTokens)
}
