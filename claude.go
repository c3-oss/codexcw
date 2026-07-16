package codexcw

import (
	"encoding/json"
	"fmt"
	"io"
	"os"
	"strconv"
	"strings"
	"time"
)

// PermissionMode controls the permission behavior of a claude agent run.
type PermissionMode string

const (
	// PermissionAcceptEdits auto-approves file edits inside the workspace.
	PermissionAcceptEdits PermissionMode = "acceptEdits"

	// PermissionAuto lets Claude choose when to request permission.
	PermissionAuto PermissionMode = "auto"

	// PermissionBypassPermissions skips all permission checks.
	PermissionBypassPermissions PermissionMode = "bypassPermissions"

	// PermissionManual requires explicit permission for tool use.
	PermissionManual PermissionMode = "manual"

	// PermissionPlan keeps Claude in read-only planning mode.
	PermissionPlan PermissionMode = "plan"

	// PermissionDontAsk denies any action that would prompt for approval.
	PermissionDontAsk PermissionMode = "dontAsk"
)

const (
	// ClaudeModelHaiku selects the latest Claude Haiku model.
	ClaudeModelHaiku = "haiku"

	// ClaudeModelSonnet selects the latest Claude Sonnet model.
	ClaudeModelSonnet = "sonnet"

	// ClaudeModelOpus selects the latest Claude Opus model.
	ClaudeModelOpus = "opus"
)

func (r *Runner) prepareClaude(req Request) (_ []string, _ io.Reader, cleanup func(), err error) {
	if err := validateClaudeRequest(req); err != nil {
		return nil, nil, nil, err
	}

	schema := req.OutputSchema
	if req.OutputSchemaPath != "" {
		data, err := os.ReadFile(req.OutputSchemaPath)
		if err != nil {
			return nil, nil, nil, err
		}
		schema = data
	}

	args := []string{"-p", "--output-format", "stream-json", "--verbose"}
	if req.Model != "" {
		args = append(args, "--model", req.Model)
	}
	if req.PermissionMode != "" {
		args = append(args, "--permission-mode", string(req.PermissionMode))
	}
	for _, tool := range req.AllowedTools {
		args = append(args, "--allowed-tools", tool)
	}
	for _, tool := range req.DisallowedTools {
		args = append(args, "--disallowed-tools", tool)
	}
	if req.DangerouslyBypassSandbox {
		args = append(args, "--dangerously-skip-permissions")
	}
	if !req.Persistent {
		args = append(args, "--no-session-persistence")
	}
	for _, dir := range req.AddDirs {
		args = append(args, "--add-dir", dir)
	}
	if len(schema) > 0 {
		args = append(args, "--json-schema", string(schema))
	}
	if req.ResumeID != "" {
		args = append(args, "--resume", req.ResumeID)
	}
	if req.ResumeLast {
		args = append(args, "--continue")
	}

	return args, promptReader(req), nil, nil
}

func validateClaudeRequest(req Request) error {
	if req.Prompt == "" && req.Stdin == nil {
		return ErrPromptRequired
	}
	if len(req.OutputSchema) > 0 && req.OutputSchemaPath != "" {
		return fmt.Errorf("%w: output schema path and inline schema are mutually exclusive", ErrInvalidRequest)
	}
	if req.ResumeID != "" && req.ResumeLast {
		return fmt.Errorf("%w: resume id and resume last are mutually exclusive", ErrInvalidRequest)
	}

	unsupported := []struct {
		set  bool
		name string
	}{
		{len(req.Images) > 0, "images"},
		{req.Profile != "", "profile"},
		{req.Sandbox != "", "sandbox"},
		{req.Approval != "", "approval"},
		{len(req.Config) > 0, "config overrides"},
		{len(req.Enable) > 0, "enable flags"},
		{len(req.Disable) > 0, "disable flags"},
		{req.StrictConfig, "strict config"},
		{req.IgnoreUserConfig, "ignore user config"},
		{req.IgnoreRules, "ignore rules"},
		{req.RequireGitRepo, "require git repo"},
		{req.OutputLastMessagePath != "", "output last message path"},
		{req.DangerouslyBypassHooks, "dangerously bypass hooks"},
		{req.ResumeAll, "resume all"},
	}
	for _, field := range unsupported {
		if field.set {
			return fmt.Errorf("%w: %s is not supported by the claude agent", ErrInvalidRequest, field.name)
		}
	}
	return nil
}

// claudeEventDecoder normalizes the claude -p stream-json events into the
// shared Event model. Raw always keeps the original Claude JSON line.
type claudeEventDecoder struct {
	pending        map[string]Item
	lastAgentText  string
	blockSequences map[string]uint64
}

func newClaudeEventDecoder() *claudeEventDecoder {
	return &claudeEventDecoder{
		pending:        make(map[string]Item),
		blockSequences: make(map[string]uint64),
	}
}

type claudeWireEvent struct {
	Type          string                          `json:"type"`
	Subtype       string                          `json:"subtype"`
	SessionID     string                          `json:"session_id"`
	Message       *claudeWireMessage              `json:"message"`
	IsError       bool                            `json:"is_error"`
	Result        string                          `json:"result"`
	Usage         claudeWireUsage                 `json:"usage"`
	TotalCostUSD  float64                         `json:"total_cost_usd"`
	ModelUsage    map[string]claudeWireModelUsage `json:"modelUsage"`
	ToolUseResult json.RawMessage                 `json:"tool_use_result"`
}

type claudeWireMessage struct {
	ID      string          `json:"id"`
	Content json.RawMessage `json:"content"`
}

type claudeWireBlock struct {
	Type      string          `json:"type"`
	Text      string          `json:"text"`
	Thinking  string          `json:"thinking"`
	ID        string          `json:"id"`
	Name      string          `json:"name"`
	Input     json.RawMessage `json:"input"`
	ToolUseID string          `json:"tool_use_id"`
	Content   json.RawMessage `json:"content"`
	IsError   bool            `json:"is_error"`
}

type claudeWireUsage struct {
	InputTokens              int64 `json:"input_tokens"`
	CacheCreationInputTokens int64 `json:"cache_creation_input_tokens"`
	CacheReadInputTokens     int64 `json:"cache_read_input_tokens"`
	OutputTokens             int64 `json:"output_tokens"`
}

type claudeWireModelUsage struct {
	InputTokens              int64   `json:"inputTokens"`
	OutputTokens             int64   `json:"outputTokens"`
	CacheReadInputTokens     int64   `json:"cacheReadInputTokens"`
	CacheCreationInputTokens int64   `json:"cacheCreationInputTokens"`
	WebSearchRequests        int64   `json:"webSearchRequests"`
	CostUSD                  float64 `json:"costUSD"`
	ContextWindow            int64   `json:"contextWindow"`
	MaxOutputTokens          int64   `json:"maxOutputTokens"`
}

func (d *claudeEventDecoder) decode(line []byte, runID, threadID string, now time.Time) ([]Event, error) {
	raw := append(json.RawMessage(nil), line...)

	var wire claudeWireEvent
	if err := json.Unmarshal(line, &wire); err != nil {
		return nil, err
	}
	if wire.Type == "" {
		return nil, fmt.Errorf("missing event type")
	}

	base := Event{RunID: runID, ThreadID: threadID, ReceivedAt: now, Raw: raw}
	if wire.SessionID != "" {
		base.ThreadID = wire.SessionID
	}

	switch wire.Type {
	case "system":
		if wire.Subtype != "init" {
			return []Event{claudePassthrough(base, wire.Type)}, nil
		}
		started := base
		started.Type = EventThreadStarted
		started.ThreadStarted = &ThreadStartedEvent{ThreadID: wire.SessionID}
		turn := base
		turn.Type = EventTurnStarted
		turn.TurnStarted = &TurnStartedEvent{}
		return []Event{started, turn}, nil
	case "assistant":
		return d.decodeAssistant(base, &wire)
	case "user":
		return d.decodeUser(base, &wire)
	case "result":
		return d.decodeResult(base, &wire), nil
	default:
		return []Event{claudePassthrough(base, wire.Type)}, nil
	}
}

func (d *claudeEventDecoder) decodeAssistant(base Event, wire *claudeWireEvent) ([]Event, error) {
	var events []Event
	for _, rawBlock := range claudeContentBlocks(wire.Message) {
		var block claudeWireBlock
		if err := json.Unmarshal(rawBlock, &block); err != nil {
			return nil, err
		}
		switch block.Type {
		case "text":
			d.lastAgentText = block.Text
			events = append(events, claudeItemCompleted(base, Item{
				ID:     d.claudeBlockID(wire.Message.ID),
				Type:   ItemAgentMessage,
				Status: "completed",
				Raw:    append(json.RawMessage(nil), rawBlock...),
				Text:   block.Text,
			}))
		case "thinking":
			events = append(events, claudeItemCompleted(base, Item{
				ID:     d.claudeBlockID(wire.Message.ID),
				Type:   ItemReasoning,
				Status: "completed",
				Raw:    append(json.RawMessage(nil), rawBlock...),
				Text:   block.Thinking,
			}))
		case "tool_use":
			item := claudeToolItem(block, rawBlock)
			d.pending[block.ID] = item
			started := base
			started.Type = EventItemStarted
			started.ItemStarted = &ItemEvent{Item: item}
			events = append(events, started)
		}
	}
	if len(events) == 0 {
		return []Event{claudePassthrough(base, wire.Type)}, nil
	}
	return events, nil
}

func (d *claudeEventDecoder) decodeUser(base Event, wire *claudeWireEvent) ([]Event, error) {
	var events []Event
	for _, rawBlock := range claudeContentBlocks(wire.Message) {
		var block claudeWireBlock
		if err := json.Unmarshal(rawBlock, &block); err != nil {
			return nil, err
		}
		if block.Type != "tool_result" {
			continue
		}
		item, ok := d.pending[block.ToolUseID]
		if !ok {
			continue
		}
		delete(d.pending, block.ToolUseID)

		item.Raw = append(json.RawMessage(nil), rawBlock...)
		item.AggregatedOutput = claudeToolResultText(block.Content)
		item.Status = "completed"
		if block.IsError {
			item.Status = "failed"
		}
		if item.Type == ItemCommandExecution {
			item.ExitCode = claudeCommandExitCode(block.Content, wire.ToolUseResult)
			if item.ExitCode == nil && !block.IsError {
				exitCode := 0
				item.ExitCode = &exitCode
			}
		}
		if item.Type == ItemFileChange && len(item.Changes) > 0 {
			if kind := claudeFileChangeKind(wire.ToolUseResult); kind != "" {
				item.Changes[0].Kind = kind
			}
		}
		events = append(events, claudeItemCompleted(base, item))
	}
	if len(events) == 0 {
		return []Event{claudePassthrough(base, wire.Type)}, nil
	}
	return events, nil
}

func (d *claudeEventDecoder) decodeResult(base Event, wire *claudeWireEvent) []Event {
	usage := claudeResultUsage(wire)
	if wire.IsError {
		message := wire.Result
		if message == "" {
			message = "claude run failed"
		}
		failed := base
		failed.Type = EventTurnFailed
		failed.TurnFailed = &TurnFailedEvent{
			Error: ErrorPayload{
				Message: message,
				Raw:     base.Raw,
			},
			Usage: usage,
		}
		return []Event{failed}
	}

	var events []Event
	if wire.Result != "" && wire.Result != d.lastAgentText {
		events = append(events, claudeItemCompleted(base, Item{
			ID:     "result",
			Type:   ItemAgentMessage,
			Status: "completed",
			Raw:    base.Raw,
			Text:   wire.Result,
		}))
	}
	completed := base
	completed.Type = EventTurnCompleted
	completed.TurnCompleted = &TurnCompletedEvent{Usage: usage}
	return append(events, completed)
}

func claudeResultUsage(wire *claudeWireEvent) Usage {
	modelUsage := make(map[string]ModelUsage, len(wire.ModelUsage))
	for model, usage := range wire.ModelUsage {
		modelUsage[model] = ModelUsage(usage)
	}
	return Usage{
		InputTokens:              wire.Usage.InputTokens,
		CachedInputTokens:        wire.Usage.CacheReadInputTokens,
		CacheCreationInputTokens: wire.Usage.CacheCreationInputTokens,
		OutputTokens:             wire.Usage.OutputTokens,
		TotalTokens: wire.Usage.InputTokens +
			wire.Usage.CacheCreationInputTokens +
			wire.Usage.CacheReadInputTokens +
			wire.Usage.OutputTokens,
		TotalCostUSD: wire.TotalCostUSD,
		ModelUsage:   modelUsage,
	}
}

func claudeToolItem(block claudeWireBlock, rawBlock json.RawMessage) Item {
	item := Item{
		ID:     block.ID,
		Status: "in_progress",
		Raw:    append(json.RawMessage(nil), rawBlock...),
	}

	var input struct {
		Command      string `json:"command"`
		FilePath     string `json:"file_path"`
		NotebookPath string `json:"notebook_path"`
	}
	_ = json.Unmarshal(block.Input, &input)

	switch {
	case block.Name == "Bash":
		item.Type = ItemCommandExecution
		item.Command = input.Command
	case block.Name == "Write" || block.Name == "Edit" || block.Name == "MultiEdit" || block.Name == "NotebookEdit":
		item.Type = ItemFileChange
		path := input.FilePath
		if path == "" {
			path = input.NotebookPath
		}
		kind := "update"
		if block.Name == "Write" {
			kind = "add"
		}
		item.Changes = []FileChange{{Path: path, Kind: kind}}
	case strings.HasPrefix(block.Name, "mcp__"):
		item.Type = ItemMCPToolCall
	case block.Name == "WebSearch":
		item.Type = ItemWebSearch
	case block.Name == "Task":
		item.Type = ItemCollabToolCall
	case block.Name == "TodoWrite":
		item.Type = ItemPlanUpdate
	default:
		item.Type = ItemToolCall
	}
	return item
}

func claudeContentBlocks(message *claudeWireMessage) []json.RawMessage {
	if message == nil || len(message.Content) == 0 {
		return nil
	}
	var blocks []json.RawMessage
	if json.Unmarshal(message.Content, &blocks) != nil {
		return nil
	}
	return blocks
}

func claudeToolResultText(raw json.RawMessage) string {
	if len(raw) == 0 {
		return ""
	}
	var text string
	if json.Unmarshal(raw, &text) == nil {
		return text
	}
	var blocks []struct {
		Type string `json:"type"`
		Text string `json:"text"`
	}
	if json.Unmarshal(raw, &blocks) == nil {
		var parts []string
		for _, block := range blocks {
			if block.Type == "text" && block.Text != "" {
				parts = append(parts, block.Text)
			}
		}
		return strings.Join(parts, "\n")
	}
	return ""
}

func claudeFileChangeKind(raw json.RawMessage) string {
	if len(raw) == 0 {
		return ""
	}
	var result struct {
		Type string `json:"type"`
	}
	if json.Unmarshal(raw, &result) != nil {
		return ""
	}
	switch result.Type {
	case "create":
		return "add"
	case "update":
		return "update"
	default:
		return ""
	}
}

func claudeCommandExitCode(content, toolUseResult json.RawMessage) *int {
	for _, raw := range []json.RawMessage{toolUseResult, content} {
		if len(raw) == 0 {
			continue
		}
		var result struct {
			ExitCode      *int `json:"exit_code"`
			ExitCodeCamel *int `json:"exitCode"`
		}
		if json.Unmarshal(raw, &result) == nil {
			switch {
			case result.ExitCode != nil:
				return result.ExitCode
			case result.ExitCodeCamel != nil:
				return result.ExitCodeCamel
			}
		}

		var text string
		if json.Unmarshal(raw, &text) != nil {
			text = claudeToolResultText(raw)
		}
		if exitCode := claudeExitCodeFromText(text); exitCode != nil {
			return exitCode
		}
	}
	return nil
}

func claudeExitCodeFromText(text string) *int {
	const marker = "exit code "
	lower := strings.ToLower(text)
	index := strings.Index(lower, marker)
	if index == -1 {
		return nil
	}
	value := text[index+len(marker):]
	end := 0
	for end < len(value) && (value[end] == '-' || value[end] >= '0' && value[end] <= '9') {
		end++
	}
	if end == 0 {
		return nil
	}
	code, err := strconv.Atoi(value[:end])
	if err != nil {
		return nil
	}
	return &code
}

func (d *claudeEventDecoder) claudeBlockID(messageID string) string {
	sequence := d.blockSequences[messageID]
	d.blockSequences[messageID]++
	if messageID == "" {
		return fmt.Sprintf("block_%d", sequence)
	}
	return fmt.Sprintf("%s_%d", messageID, sequence)
}

func claudeItemCompleted(base Event, item Item) Event {
	base.Type = EventItemCompleted
	base.ItemCompleted = &ItemEvent{Item: item}
	return base
}

func claudePassthrough(base Event, eventType string) Event {
	base.Type = EventType(eventType)
	return base
}
