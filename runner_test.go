package codexcw

import (
	"context"
	"errors"
	"io"
	"os"
	"os/exec"
	"path/filepath"
	"strings"
	"testing"
	"time"

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

func TestCollabToolCallItemIsTyped(t *testing.T) {
	fake := writeFakeCodex(t, `
record_args "$@"
cat >/dev/null
printf '%s\n' '{"type":"thread.started","thread_id":"thread-3"}'
printf '%s\n' '{"type":"turn.started"}'
printf '%s\n' '{"type":"item.started","item":{"id":"item_0","type":"collab_tool_call","tool":"wait","sender_thread_id":"thread-3","receiver_thread_ids":[],"agents_states":{},"status":"in_progress"}}'
printf '%s\n' '{"type":"item.completed","item":{"id":"item_0","type":"collab_tool_call","tool":"wait","sender_thread_id":"thread-3","receiver_thread_ids":[],"agents_states":{},"status":"completed"}}'
printf '%s\n' '{"type":"item.completed","item":{"id":"item_1","type":"agent_message","text":"red, green, blue"}}'
printf '%s\n' '{"type":"turn.completed","usage":{"input_tokens":1,"output_tokens":1}}'
`)

	result, err := New(WithExecutable(fake)).Run(context.Background(), Request{Prompt: "spawn agents"})
	require.NoError(t, err)
	require.NotNil(t, result)
	require.Len(t, result.Events, 6)

	started := result.Events[2].ItemStarted.Item
	assert.Equal(t, ItemCollabToolCall, started.Type)
	assert.Equal(t, "in_progress", started.Status)
	assert.Contains(t, string(started.Raw), `"tool":"wait"`)

	completed := result.Events[3].ItemCompleted.Item
	assert.Equal(t, ItemCollabToolCall, completed.Type)
	assert.Equal(t, "completed", completed.Status)
	assert.Equal(t, "red, green, blue", result.FinalMessage)
}

func TestProcessExitErrorCarriesStderrAndLastEvent(t *testing.T) {
	fake := writeFakeCodex(t, `
record_args "$@"
cat >/dev/null
printf '%s\n' '{"type":"thread.started","thread_id":"thread-3"}'
printf '%s\n' '{"type":"turn.started"}'
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
	assert.Equal(t, EventTurnStarted, exitErr.LastEvent.Type)
}

func TestFastExitStderrIsReliable(t *testing.T) {
	fake := writeFakeCodex(t, `
record_args "$@"
cat >/dev/null
printf '%s\n' 'fast stderr detail' >&2
exit 1
`)
	runner := New(WithExecutable(fake))

	const iterations = 100
	for iteration := range iterations {
		result, err := runner.Run(context.Background(), Request{Prompt: "fail"})
		require.Error(t, err, "iteration %d", iteration)
		require.NotNil(t, result, "iteration %d", iteration)

		var exitErr *ExitError
		require.True(t, errors.As(err, &exitErr), "iteration %d", iteration)
		assert.Contains(t, exitErr.Stderr, "fast stderr detail", "iteration %d", iteration)
		assert.Contains(t, result.Stderr, "fast stderr detail", "iteration %d", iteration)
	}
}

func TestSuccessfulExitCapturesStderr(t *testing.T) {
	fake := writeFakeCodex(t, `
record_args "$@"
cat >/dev/null
printf '%s\n' 'successful stderr detail' >&2
printf '%s\n' '{"type":"thread.started","thread_id":"thread-stderr"}'
printf '%s\n' '{"type":"turn.completed","usage":{"input_tokens":1,"output_tokens":1}}'
`)

	result, err := New(WithExecutable(fake)).Run(context.Background(), Request{Prompt: "ok"})
	require.NoError(t, err)
	require.NotNil(t, result)
	assert.Contains(t, result.Stderr, "successful stderr detail")
}

func TestDescendantHoldingStderrIsBounded(t *testing.T) {
	fake := writeFakeCodex(t, `
record_args "$@"
cat >/dev/null
sleep 3 >/dev/null &
printf '%s\n' 'parent stderr detail' >&2
printf '%s\n' '{"type":"thread.started","thread_id":"thread-descendant"}'
printf '%s\n' '{"type":"turn.completed","usage":{"input_tokens":1,"output_tokens":1}}'
`)

	result, err := New(WithExecutable(fake)).Run(context.Background(), Request{Prompt: "ok"})
	require.ErrorIs(t, err, exec.ErrWaitDelay)
	require.NotNil(t, result)
	assert.Contains(t, result.Stderr, "parent stderr detail")
}

func TestCancellationCapturesStderr(t *testing.T) {
	fake := writeFakeCodex(t, `
record_args "$@"
cat >/dev/null
printf '%s\n' 'cancelled stderr detail' >&2
printf '%s\n' '{"type":"thread.started","thread_id":"thread-cancel"}'
while :; do :; done
`)

	session, err := New(WithExecutable(fake)).Start(context.Background(), Request{Prompt: "cancel"})
	require.NoError(t, err)
	require.Equal(t, EventThreadStarted, (<-session.Events()).Type)
	require.NoError(t, session.Cancel())

	result, err := session.Wait()
	require.ErrorIs(t, err, context.Canceled)
	require.NotNil(t, result)
	assert.Contains(t, result.Stderr, "cancelled stderr detail")
}

func TestCancellationWithDescendantHoldingStderrIsBounded(t *testing.T) {
	fake := writeFakeCodex(t, `
record_args "$@"
cat >/dev/null
sleep 3 >/dev/null &
printf '%s\n' 'cancelled descendant stderr detail' >&2
printf '%s\n' '{"type":"thread.started","thread_id":"thread-cancel-descendant"}'
while :; do :; done
`)

	session, err := New(WithExecutable(fake)).Start(context.Background(), Request{Prompt: "cancel"})
	require.NoError(t, err)
	require.Equal(t, EventThreadStarted, (<-session.Events()).Type)

	startedAt := time.Now()
	require.NoError(t, session.Cancel())
	result, err := session.Wait()

	require.ErrorIs(t, err, context.Canceled)
	require.NotNil(t, result)
	assert.Less(t, time.Since(startedAt), 2500*time.Millisecond)
	assert.Contains(t, result.Stderr, "cancelled descendant stderr detail")
}

func TestLargeStderrOutputPreservesTail(t *testing.T) {
	fake := writeFakeCodex(t, `
record_args "$@"
cat >/dev/null
i=0
while [ "$i" -lt 8192 ]; do
  printf '%s' '0123456789abcdef' >&2
  i=$((i + 1))
done
printf '%s\n' 'stderr end marker' >&2
exit 1
`)

	result, err := New(WithExecutable(fake), WithStderrLimit(256)).Run(
		context.Background(),
		Request{Prompt: "fail"},
	)
	require.Error(t, err)
	require.NotNil(t, result)
	assert.LessOrEqual(t, len(result.Stderr), 256)
	assert.Contains(t, result.Stderr, "stderr end marker")

	var exitErr *ExitError
	require.True(t, errors.As(err, &exitErr))
	assert.Equal(t, result.Stderr, exitErr.Stderr)
}

func TestCodexEventErrorPrecedesExitError(t *testing.T) {
	fake := writeFakeCodex(t, `
record_args "$@"
cat >/dev/null
printf '%s\n' '{"type":"thread.started","thread_id":"thread-3"}'
printf '%s\n' '{"type":"turn.started"}'
printf '%s\n' '{"type":"error","message":"invalid_json_schema: bad model"}'
printf '%s\n' 'stderr detail' >&2
exit 1
`)

	result, err := New(WithExecutable(fake)).Run(context.Background(), Request{Prompt: "fail"})
	require.Error(t, err)
	require.NotNil(t, result)

	var exitErr *ExitError
	assert.False(t, errors.As(err, &exitErr), "codex error event must take precedence over generic exit error")

	var codexErr *CodexError
	require.True(t, errors.As(err, &codexErr))
	assert.Equal(t, EventError, codexErr.Event.Type)
	assert.Contains(t, codexErr.Error(), "invalid_json_schema: bad model")
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

func TestPrepareBuildsAdvancedArgs(t *testing.T) {
	runner := New()
	req := Request{
		Prompt:                 "prompt",
		Stdin:                  strings.NewReader("extra"),
		Dir:                    "/work",
		AddDirs:                []string{"/other"},
		Images:                 []string{"image.png"},
		Model:                  "gpt-test",
		Profile:                "work",
		Sandbox:                SandboxWorkspaceWrite,
		Approval:               ApprovalOnRequest,
		Config:                 []ConfigOverride{{Key: "foo.bar", Value: `"baz"`}, {Value: "raw=true"}},
		Enable:                 []string{"feature-a"},
		Disable:                []string{"feature-b"},
		StrictConfig:           true,
		IgnoreUserConfig:       true,
		IgnoreRules:            true,
		OutputSchema:           []byte(`{"type":"object"}`),
		OutputLastMessagePath:  "last.txt",
		DangerouslyBypassHooks: true,
		Env:                    []string{"IGNORED=1"},
	}

	args, stdin, cleanup, err := runner.prepare(req)
	require.NoError(t, err)
	require.NotNil(t, cleanup)
	defer cleanup()

	data, err := io.ReadAll(stdin)
	require.NoError(t, err)
	assert.Equal(t, "prompt\n\n<stdin>\nextra\n</stdin>\n", string(data))

	schemaIndex := indexOf(args, "--output-schema")
	require.NotEqual(t, -1, schemaIndex)
	schemaBytes, err := os.ReadFile(args[schemaIndex+1])
	require.NoError(t, err)
	assert.JSONEq(t, `{"type":"object"}`, string(schemaBytes))

	for _, want := range []string{
		"exec", "--json", "--color", "never", "--strict-config", "-m", "gpt-test",
		"-p", "work", "--enable", "feature-a", "--disable", "feature-b", "-i",
		"image.png", "--skip-git-repo-check", "--ephemeral", "--ignore-user-config",
		"--ignore-rules", "--sandbox", "workspace-write", "-c", `approval_policy="on-request"`,
		"--dangerously-bypass-hook-trust", "-C", "/work", "--add-dir", "/other",
		"-o", "last.txt", `foo.bar="baz"`, "raw=true", "-",
	} {
		assert.Contains(t, args, want)
	}
}

func TestPrepareResumeArgs(t *testing.T) {
	args, stdin, cleanup, err := New().prepare(Request{
		Prompt:     "continue",
		ResumeID:   "thread-id",
		ResumeAll:  true,
		Persistent: true,
		Sandbox:    SandboxDangerFullAccess,
		Approval:   ApprovalUntrusted,
	})
	require.NoError(t, err)
	require.Nil(t, cleanup)
	require.NotNil(t, stdin)

	assert.Equal(t, "exec", args[0])
	assert.Equal(t, "resume", args[1])
	assert.Contains(t, args, "--all")
	assert.Contains(t, args, "thread-id")
	assert.Contains(t, args, `sandbox_mode="danger-full-access"`)
	assert.Contains(t, args, `approval_policy="untrusted"`)
	assert.NotContains(t, args, "--ephemeral")
	assert.Equal(t, "-", args[len(args)-1])
}

func TestValidateRequest(t *testing.T) {
	tests := []struct {
		name string
		req  Request
		want error
	}{
		{name: "missing prompt", req: Request{}, want: ErrPromptRequired},
		{
			name: "schema conflict",
			req:  Request{Prompt: "x", OutputSchemaPath: "schema.json", OutputSchema: []byte("{}")},
			want: ErrInvalidRequest,
		},
		{name: "resume id and last", req: Request{Prompt: "x", ResumeID: "id", ResumeLast: true}, want: ErrInvalidRequest},
		{name: "resume all without resume", req: Request{Prompt: "x", ResumeAll: true}, want: ErrInvalidRequest},
		{name: "resume with dir", req: Request{Prompt: "x", ResumeLast: true, Dir: "."}, want: ErrInvalidRequest},
	}

	for _, tt := range tests {
		t.Run(tt.name, func(t *testing.T) {
			err := validateRequest(tt.req)
			require.Error(t, err)
			assert.ErrorIs(t, err, tt.want)
		})
	}
}

func TestCodexTurnFailedReturnsCodexError(t *testing.T) {
	fake := writeFakeCodex(t, `
record_args "$@"
cat >/dev/null
printf '%s\n' '{"type":"thread.started","thread_id":"thread-failed"}'
printf '%s\n' '{"type":"turn.failed","error":{"message":"turn broke"}}'
`)

	result, err := New(WithExecutable(fake)).Run(context.Background(), Request{Prompt: "fail"})
	require.Error(t, err)
	require.NotNil(t, result)

	var codexErr *CodexError
	require.True(t, errors.As(err, &codexErr))
	assert.Contains(t, codexErr.Error(), "turn broke")
}

func TestStderrTailLimit(t *testing.T) {
	fake := writeFakeCodex(t, `
record_args "$@"
cat >/dev/null
printf '%s' '0123456789' >&2
exit 1
`)

	result, err := New(WithExecutable(fake), WithStderrLimit(4)).Run(context.Background(), Request{Prompt: "fail"})
	require.Error(t, err)
	require.NotNil(t, result)
	assert.Equal(t, "6789", result.Stderr)
}

func TestRunManyReturnsGroupError(t *testing.T) {
	fake := writeFakeCodex(t, `
record_args "$@"
cat >/dev/null
printf '%s\n' '{"type":"thread.started","thread_id":"thread-ok"}'
printf '%s\n' '{"type":"turn.completed","usage":{"input_tokens":1,"output_tokens":1}}'
`)

	group, err := New(WithExecutable(fake)).RunMany(context.Background(), []Request{{Prompt: "ok"}, {}})
	require.NoError(t, err)
	for range group.Events() {
	}

	results, err := group.Wait()
	require.Error(t, err)
	require.Len(t, results, 2)

	var groupErr *GroupError
	require.True(t, errors.As(err, &groupErr))
	assert.Contains(t, groupErr.Error(), "1 agent run(s) failed")
	assert.ErrorIs(t, results[1].Err, ErrPromptRequired)
}

func TestOptionsAndErrorFormatting(t *testing.T) {
	runner := New(
		WithExecutable("codex-test"),
		WithEnv("A=B"),
		WithEventBuffer(2),
		WithStderrLimit(3),
		WithScanMaxBytes(4),
		WithDefaultSandbox(SandboxWorkspaceWrite),
		WithDefaultApproval(ApprovalOnRequest),
	)

	assert.Equal(t, "codex-test", runner.executable)
	assert.Equal(t, []string{"A=B"}, runner.env)
	assert.Equal(t, 2, runner.eventBuffer)
	assert.Equal(t, 3, runner.stderrLimit)
	assert.Equal(t, 4, runner.scanMaxBytes)
	assert.Equal(t, SandboxWorkspaceWrite, runner.defaultSandbox)
	assert.Equal(t, ApprovalOnRequest, runner.defaultApproval)

	assert.Equal(t, "raw=true", ConfigOverride{Value: "raw=true"}.String())
	assert.Equal(t, "a=b", ConfigOverride{Key: "a", Value: "b"}.String())
	assert.Equal(t, "", (*ExitError)(nil).Error())
	assert.Equal(t, "", (*DecodeError)(nil).Error())
	assert.Equal(t, "", (*CodexError)(nil).Error())
	assert.Equal(t, "agent event handler failed", (&HandlerError{}).Error())
	assert.Equal(t, "", (*GroupError)(nil).Error())
}

func indexOf(values []string, value string) int {
	for i, got := range values {
		if got == value {
			return i
		}
	}
	return -1
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
