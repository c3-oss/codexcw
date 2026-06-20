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
	// Code is the process exit code.
	Code int

	// Stderr is the captured stderr tail.
	Stderr string

	// LastEvent is the last decoded event before process exit.
	LastEvent *Event

	// Err is the wrapped process error.
	Err error
}

// Error formats the process exit failure.
func (e *ExitError) Error() string {
	if e == nil {
		return ""
	}
	if e.Err != nil {
		return fmt.Sprintf("codex exited with code %d: %v", e.Code, e.Err)
	}
	return fmt.Sprintf("codex exited with code %d", e.Code)
}

// Unwrap returns the wrapped process error.
func (e *ExitError) Unwrap() error {
	if e == nil {
		return nil
	}
	return e.Err
}

// DecodeError reports malformed JSONL from codex stdout.
type DecodeError struct {
	// Line is the one-based JSONL line number.
	Line int

	// Raw is the malformed line when available.
	Raw []byte

	// Err is the wrapped decode error.
	Err error
}

// Error formats the JSONL decode failure.
func (e *DecodeError) Error() string {
	if e == nil {
		return ""
	}
	return fmt.Sprintf("decode codex JSONL line %d: %v", e.Line, e.Err)
}

// Unwrap returns the wrapped decode error.
func (e *DecodeError) Unwrap() error {
	if e == nil {
		return nil
	}
	return e.Err
}

// CodexError reports a top-level error or failed turn event from Codex.
type CodexError struct {
	// Event is the Codex error event.
	Event Event
}

// Error formats the Codex error event.
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
	// Err is the handler error.
	Err error
}

// Error formats the handler failure.
func (e *HandlerError) Error() string {
	if e == nil || e.Err == nil {
		return "codex event handler failed"
	}
	return "codex event handler failed: " + e.Err.Error()
}

// Unwrap returns the handler error.
func (e *HandlerError) Unwrap() error {
	if e == nil {
		return nil
	}
	return e.Err
}

// GroupError reports one or more failed runs from RunMany.
type GroupError struct {
	// Results contains every run result, including failed runs.
	Results []GroupResult
}

// Error formats the number of failed runs.
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
