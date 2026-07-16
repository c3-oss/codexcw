package codexcw

import (
	"encoding/json"
	"fmt"
	"time"
)

type wireEvent struct {
	Type     EventType       `json:"type"`
	ThreadID string          `json:"thread_id"`
	Usage    Usage           `json:"usage"`
	Item     json.RawMessage `json:"item"`
	Error    json.RawMessage `json:"error"`
	Message  string          `json:"message"`
}

type wireItem struct {
	ID                string       `json:"id"`
	Type              ItemType     `json:"type"`
	Status            string       `json:"status"`
	Text              string       `json:"text"`
	Message           string       `json:"message"`
	Command           string       `json:"command"`
	AggregatedOutput  string       `json:"aggregated_output"`
	ExitCode          *int         `json:"exit_code"`
	Changes           []FileChange `json:"changes"`
	Tool              string       `json:"tool"`
	SenderThreadID    string       `json:"sender_thread_id"`
	ReceiverThreadIDs []string     `json:"receiver_thread_ids"`
}

func decodeEvent(line []byte, runID string, threadID string, now time.Time) (Event, error) {
	raw := append(json.RawMessage(nil), line...)

	var wire wireEvent
	if err := json.Unmarshal(line, &wire); err != nil {
		return Event{}, err
	}
	if wire.Type == "" {
		return Event{}, fmt.Errorf("missing event type")
	}

	event := Event{
		Type:       wire.Type,
		RunID:      runID,
		ThreadID:   threadID,
		ReceivedAt: now,
		Raw:        raw,
	}

	switch wire.Type {
	case EventThreadStarted:
		event.ThreadID = wire.ThreadID
		event.ThreadStarted = &ThreadStartedEvent{ThreadID: wire.ThreadID}
	case EventTurnStarted:
		event.TurnStarted = &TurnStartedEvent{}
	case EventTurnCompleted:
		event.TurnCompleted = &TurnCompletedEvent{Usage: normalizeCodexUsage(wire.Usage)}
	case EventTurnFailed:
		event.TurnFailed = &TurnFailedEvent{
			Error: decodeEventError(wire.Error),
			Usage: normalizeCodexUsage(wire.Usage),
		}
	case EventItemStarted:
		item, err := decodeItem(wire.Item)
		if err != nil {
			return Event{}, err
		}
		event.ItemStarted = &ItemEvent{Item: item}
	case EventItemCompleted:
		item, err := decodeItem(wire.Item)
		if err != nil {
			return Event{}, err
		}
		event.ItemCompleted = &ItemEvent{Item: item}
	case EventError:
		event.Error = &CodexErrorEvent{Message: wire.Message, Raw: append(json.RawMessage(nil), wire.Error...)}
		if event.Error.Message == "" && len(wire.Error) > 0 {
			event.Error.Message = string(wire.Error)
		}
	}

	return event, nil
}

func decodeItem(raw json.RawMessage) (Item, error) {
	if len(raw) == 0 {
		return Item{}, fmt.Errorf("missing item payload")
	}
	var wire wireItem
	if err := json.Unmarshal(raw, &wire); err != nil {
		return Item{}, err
	}
	return Item{
		ID:                wire.ID,
		Type:              wire.Type,
		Status:            wire.Status,
		Raw:               append(json.RawMessage(nil), raw...),
		Text:              wire.Text,
		Message:           wire.Message,
		Command:           wire.Command,
		AggregatedOutput:  wire.AggregatedOutput,
		ExitCode:          wire.ExitCode,
		Changes:           wire.Changes,
		Tool:              wire.Tool,
		SenderThreadID:    wire.SenderThreadID,
		ReceiverThreadIDs: wire.ReceiverThreadIDs,
	}, nil
}

// normalizeCodexUsage derives the total when Codex omits total_tokens.
// cached_input_tokens is a subset of input_tokens on the codex wire, so the
// derived total is input plus output; an explicit total is preserved.
func normalizeCodexUsage(usage Usage) Usage {
	if usage.TotalTokens == 0 {
		usage.TotalTokens = usage.InputTokens + usage.OutputTokens
	}
	return usage
}

func decodeEventError(raw json.RawMessage) ErrorPayload {
	err := ErrorPayload{Raw: append(json.RawMessage(nil), raw...)}
	if len(raw) == 0 {
		return err
	}
	var wire struct {
		Message string `json:"message"`
	}
	if json.Unmarshal(raw, &wire) == nil {
		err.Message = wire.Message
	}
	if err.Message == "" {
		err.Message = string(raw)
	}
	return err
}
