package codexcw

import (
	"errors"
	"fmt"
)

var (
	// ErrPromptRequired is returned when a non-resume run has no prompt input.
	ErrPromptRequired = errors.New("codexcw: prompt or stdin is required")

	// ErrInvalidRequest is wrapped by validation failures in Request.
	ErrInvalidRequest = errors.New("codexcw: invalid request")
)

// ExitError reports a non-zero codex process exit.
type ExitError struct {
	Code      int
	Stderr    string
	LastEvent *Event
	Err       error
}

func (e *ExitError) Error() string {
	if e == nil {
		return ""
	}
	if e.Err != nil {
		return fmt.Sprintf("codex exited with code %d: %v", e.Code, e.Err)
	}
	return fmt.Sprintf("codex exited with code %d", e.Code)
}

func (e *ExitError) Unwrap() error {
	if e == nil {
		return nil
	}
	return e.Err
}

// DecodeError reports malformed JSONL from codex stdout.
type DecodeError struct {
	Line int
	Raw  []byte
	Err  error
}

func (e *DecodeError) Error() string {
	if e == nil {
		return ""
	}
	return fmt.Sprintf("decode codex JSONL line %d: %v", e.Line, e.Err)
}

func (e *DecodeError) Unwrap() error {
	if e == nil {
		return nil
	}
	return e.Err
}

// CodexError reports a top-level error or failed turn event from Codex.
type CodexError struct {
	Event Event
}

func (e *CodexError) Error() string {
	if e == nil {
		return ""
	}
	if e.Event.Error != nil && e.Event.Error.Message != "" {
		return "codex error: " + e.Event.Error.Message
	}
	if e.Event.TurnFailed != nil && e.Event.TurnFailed.Error.Message != "" {
		return "codex turn failed: " + e.Event.TurnFailed.Error.Message
	}
	return "codex error event"
}

// HandlerError wraps an error returned by a Handler.
type HandlerError struct {
	Err error
}

func (e *HandlerError) Error() string {
	if e == nil || e.Err == nil {
		return "codex event handler failed"
	}
	return "codex event handler failed: " + e.Err.Error()
}

func (e *HandlerError) Unwrap() error {
	if e == nil {
		return nil
	}
	return e.Err
}

// GroupError reports one or more failed runs from RunMany.
type GroupError struct {
	Results []GroupResult
}

func (e *GroupError) Error() string {
	if e == nil {
		return ""
	}
	failed := 0
	for _, result := range e.Results {
		if result.Err != nil {
			failed++
		}
	}
	return fmt.Sprintf("%d codex run(s) failed", failed)
}
