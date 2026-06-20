package codexcw

import (
	"context"
	"errors"
	"os"
	"path/filepath"
	"strings"
	"testing"

	"github.com/stretchr/testify/assert"
	"github.com/stretchr/testify/require"
)

func TestRunDecodesEventsAndUsesSafeDefaults(t *testing.T) {
	argsFile := filepath.Join(t.TempDir(), "args.txt")
	stdinFile := filepath.Join(t.TempDir(), "stdin.txt")
	fake := writeFakeCodex(t, `
record_args "$@"
cat > "$CODEXCW_STDIN_FILE"
printf '%s\n' '{"type":"thread.started","thread_id":"thread-1"}'
printf '%s\n' '{"type":"turn.started"}'
printf '%s\n' '{"type":"item.completed","item":{"id":"item_0","type":"agent_message","text":"Oi."}}'
printf '%s\n' '{"type":"turn.completed","usage":{"input_tokens":10,"cached_input_tokens":2,"output_tokens":3,"reasoning_output_tokens":1}}'
`)

	runner := New(WithExecutable(fake), WithEnv(
		"CODEXCW_ARGS_FILE="+argsFile,
		"CODEXCW_STDIN_FILE="+stdinFile,
	))

	result, err := runner.Run(context.Background(), Request{Prompt: "diga oi"})
	require.NoError(t, err)
	require.NotNil(t, result)
	assert.Equal(t, "thread-1", result.ThreadID)
	assert.Equal(t, "Oi.", result.FinalMessage)
	assert.Equal(t, int64(10), result.Usage.InputTokens)
	assert.Len(t, result.Events, 4)

	stdinBytes, err := os.ReadFile(stdinFile)
	require.NoError(t, err)
	assert.Equal(t, "diga oi", string(stdinBytes))

	args := readArgs(t, argsFile)
	assert.Contains(t, args, "exec")
	assert.Contains(t, args, "--json")
	assert.Contains(t, args, "--color")
	assert.Contains(t, args, "never")
	assert.Contains(t, args, "--skip-git-repo-check")
	assert.Contains(t, args, "--ephemeral")
	assert.Contains(t, args, "--sandbox")
	assert.Contains(t, args, "read-only")
	assert.Contains(t, args, "-c")
	assert.Contains(t, args, `approval_policy="never"`)
	assert.Equal(t, "-", args[len(args)-1])
}

func TestCommandExecutionFailureIsAnEventNotRunError(t *testing.T) {
	fake := writeFakeCodex(t, `
record_args "$@"
cat >/dev/null
printf '%s\n' '{"type":"thread.started","thread_id":"thread-2"}'
printf '%s\n' '{"type":"turn.started"}'
printf '%s\n' '{"type":"item.completed","item":{"id":"item_0","type":"command_execution","command":"false","aggregated_output":"boom\n","exit_code":7,"status":"failed"}}'
printf '%s\n' '{"type":"item.completed","item":{"id":"item_1","type":"agent_message","text":"Exit 7"}}'
printf '%s\n' '{"type":"turn.completed","usage":{"input_tokens":1,"output_tokens":1}}'
`)

	result, err := New(WithExecutable(fake)).Run(context.Background(), Request{Prompt: "run false"})
	require.NoError(t, err)
	require.NotNil(t, result)
	require.Len(t, result.Events, 5)

	item := result.Events[2].ItemCompleted.Item
	require.NotNil(t, item.ExitCode)
	assert.Equal(t, 7, *item.ExitCode)
	assert.Equal(t, "failed", item.Status)
	assert.Equal(t, "Exit 7", result.FinalMessage)
}

func TestProcessExitErrorCarriesStderrAndLastEvent(t *testing.T) {
	fake := writeFakeCodex(t, `
record_args "$@"
cat >/dev/null
printf '%s\n' '{"type":"thread.started","thread_id":"thread-3"}'
printf '%s\n' '{"type":"turn.started"}'
printf '%s\n' '{"type":"error","message":"bad model"}'
printf '%s\n' 'stderr detail' >&2
exit 1
`)

	result, err := New(WithExecutable(fake)).Run(context.Background(), Request{Prompt: "fail"})
	require.Error(t, err)
	require.NotNil(t, result)

	var exitErr *ExitError
	require.True(t, errors.As(err, &exitErr))
	assert.Equal(t, 1, exitErr.Code)
	assert.Contains(t, exitErr.Stderr, "stderr detail")
	require.NotNil(t, exitErr.LastEvent)
	assert.Equal(t, EventError, exitErr.LastEvent.Type)
}

func TestDecodeError(t *testing.T) {
	fake := writeFakeCodex(t, `
record_args "$@"
cat >/dev/null
printf '%s\n' 'not-json'
`)

	result, err := New(WithExecutable(fake)).Run(context.Background(), Request{Prompt: "decode"})
	require.Error(t, err)
	require.NotNil(t, result)

	var decodeErr *DecodeError
	require.True(t, errors.As(err, &decodeErr))
	assert.Equal(t, 1, decodeErr.Line)
	assert.Equal(t, "not-json", string(decodeErr.Raw))
}

func TestHandlerErrorCancelsRun(t *testing.T) {
	wantErr := errors.New("stop")
	fake := writeFakeCodex(t, `
record_args "$@"
cat >/dev/null
printf '%s\n' '{"type":"thread.started","thread_id":"thread-4"}'
printf '%s\n' '{"type":"turn.started"}'
sleep 5
`)

	result, err := New(WithExecutable(fake)).Run(
		context.Background(),
		Request{Prompt: "handler"},
		WithHandler(HandlerFunc(func(_ context.Context, event Event) error {
			if event.Type == EventTurnStarted {
				return wantErr
			}
			return nil
		})),
	)
	require.Error(t, err)
	require.NotNil(t, result)

	var handlerErr *HandlerError
	require.True(t, errors.As(err, &handlerErr))
	assert.ErrorIs(t, handlerErr, wantErr)
}

func TestRunMany(t *testing.T) {
	fake := writeFakeCodex(t, `
record_args "$@"
cat >/dev/null
printf '%s\n' '{"type":"thread.started","thread_id":"thread-many"}'
printf '%s\n' '{"type":"item.completed","item":{"id":"item_0","type":"agent_message","text":"done"}}'
printf '%s\n' '{"type":"turn.completed","usage":{"input_tokens":1,"output_tokens":1}}'
`)

	group, err := New(WithExecutable(fake)).RunMany(
		context.Background(),
		[]Request{{Prompt: "a"}, {Prompt: "b"}, {Prompt: "c"}},
		WithMaxConcurrent(2),
	)
	require.NoError(t, err)

	var events []RunEvent
	for event := range group.Events() {
		events = append(events, event)
	}

	results, err := group.Wait()
	require.NoError(t, err)
	require.Len(t, results, 3)
	require.NotEmpty(t, events)
	for _, result := range results {
		require.NoError(t, result.Err)
		require.NotNil(t, result.Result)
		assert.Equal(t, "done", result.Result.FinalMessage)
	}
}

func writeFakeCodex(t *testing.T, body string) string {
	t.Helper()

	dir := t.TempDir()
	path := filepath.Join(dir, "codex")
	script := `#!/bin/sh
set -eu
record_args() {
  if [ "${CODEXCW_ARGS_FILE:-}" != "" ]; then
    : > "$CODEXCW_ARGS_FILE"
    for arg in "$@"; do
      printf '%s\n' "$arg" >> "$CODEXCW_ARGS_FILE"
    done
  fi
}
` + body

	require.NoError(t, os.WriteFile(path, []byte(script), 0o755))
	return path
}

func readArgs(t *testing.T, path string) []string {
	t.Helper()

	bytes, err := os.ReadFile(path)
	require.NoError(t, err)
	return strings.Split(strings.TrimSpace(string(bytes)), "\n")
}
