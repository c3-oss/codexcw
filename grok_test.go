package codexcw

import (
	"context"
	"errors"
	"io"
	"os"
	"path/filepath"
	"strings"
	"testing"
	"time"

	"github.com/stretchr/testify/assert"
	"github.com/stretchr/testify/require"
)

func TestGrokRunNormalizesEventsAndUsesPromptFile(t *testing.T) {
	argsFile := filepath.Join(t.TempDir(), "args.txt")
	stdinFile := filepath.Join(t.TempDir(), "stdin.txt")
	fake := writeFakeCodex(t, `
record_args "$@"
prompt_file=
previous=
for arg in "$@"; do
  if [ "$previous" = "--prompt-file" ]; then
    prompt_file=$arg
  fi
  previous=$arg
done
cat "$prompt_file" > "$CODEXCW_STDIN_FILE"
printf '%s\n' '{"type":"system","subtype":"init","session_id":"grok-session"}'
printf '%s\n' '{"type":"assistant","message":{"id":"msg_1","content":[{"type":"thinking","thinking":"Checking."},{"type":"tool_use","id":"tool_1","name":"run_terminal_command","input":{"command":"printf hi","description":"Print text"}}]},"session_id":"grok-session"}'
printf '%s\n' '{"type":"user","message":{"content":[{"type":"tool_result","tool_use_id":"tool_1","content":"{\"type\":\"Bash\",\"output\":[104,105,10],\"output_for_prompt\":\"exit: 0\\nhi\\n\",\"exit_code\":0}"}]},"session_id":"grok-session"}'
printf '%s\n' '{"type":"assistant","message":{"id":"msg_2","content":[{"type":"tool_use","id":"tool_2","name":"search_replace","input":{"target_file":"src/main.go"}}]},"session_id":"grok-session"}'
printf '%s\n' '{"type":"user","message":{"content":[{"type":"tool_result","tool_use_id":"tool_2","content":"updated src/main.go"}]},"session_id":"grok-session"}'
printf '%s\n' '{"type":"assistant","message":{"id":"msg_3","content":[{"type":"text","text":"Done."}]},"session_id":"grok-session"}'
printf '%s\n' '{"type":"result","subtype":"success","is_error":false,"result":"Done.","session_id":"grok-session","total_cost_usd":0.01,"usage":{"input_tokens":11,"cache_read_input_tokens":5,"output_tokens":7},"modelUsage":{"grok-code-fast-1":{"inputTokens":11,"cacheReadInputTokens":5,"outputTokens":7,"costUSD":0.01}}}'
`)

	result, err := New(
		WithAgent(AgentGrok),
		WithExecutable(fake),
		WithEnv("CODEXCW_ARGS_FILE="+argsFile, "CODEXCW_STDIN_FILE="+stdinFile),
	).Run(context.Background(), Request{
		Prompt:       "inspect",
		Stdin:        strings.NewReader("extra context"),
		AllowedTools: []string{"Bash(git *)"},
	})
	require.NoError(t, err)
	require.NotNil(t, result)

	assert.Equal(t, "grok-session", result.ThreadID)
	assert.Equal(t, "Done.", result.FinalMessage)
	assert.Equal(t, int64(11), result.Usage.InputTokens)
	assert.Equal(t, int64(5), result.Usage.CachedInputTokens)
	assert.Equal(t, int64(7), result.Usage.OutputTokens)
	assert.Equal(t, int64(23), result.Usage.TotalTokens)
	assert.Zero(t, result.Usage.ReasoningOutputTokens)
	assert.InDelta(t, 0.01, result.Usage.TotalCostUSD, 0.000001)

	var command, file *Item
	for index := range result.Events {
		event := &result.Events[index]
		if event.ItemCompleted == nil {
			continue
		}
		item := &event.ItemCompleted.Item
		switch item.Type {
		case ItemCommandExecution:
			command = item
		case ItemFileChange:
			file = item
		}
		assert.NotEmpty(t, event.Raw)
	}
	require.NotNil(t, command)
	assert.Equal(t, "printf hi", command.Command)
	assert.Equal(t, "hi\n", command.AggregatedOutput)
	require.NotNil(t, command.ExitCode)
	assert.Zero(t, *command.ExitCode)
	require.NotNil(t, file)
	require.Len(t, file.Changes, 1)
	assert.Equal(t, "src/main.go", file.Changes[0].Path)
	assert.Equal(t, "update", file.Changes[0].Kind)

	prompt, err := os.ReadFile(stdinFile)
	require.NoError(t, err)
	assert.Equal(t, "inspect\n\n<stdin>\nextra context\n</stdin>\n", string(prompt))

	args := readArgs(t, argsFile)
	for _, expected := range []string{
		"--no-auto-update",
		"--output-format", "streaming-messages-json",
		"--verbatim",
		"--prompt-file",
		"--sandbox", "read-only",
		"--permission-mode", "dontAsk",
		"--allow", "Bash(git *)",
	} {
		assert.Contains(t, args, expected)
	}
	promptIndex := indexOf(args, "--prompt-file")
	require.NotEqual(t, -1, promptIndex)
	assert.NoFileExists(t, args[promptIndex+1])
}

func TestGrokErrorResultReturnsGrokError(t *testing.T) {
	fake := writeFakeCodex(t, `
printf '%s\n' '{"type":"system","subtype":"init","session_id":"grok-error"}'
printf '%s\n' '{"type":"result","subtype":"error_max_turns","is_error":true,"errors":["Reached the maximum number of turns"],"session_id":"grok-error"}'
printf '%s\n' 'Error: max turns reached' >&2
exit 1
`)

	result, err := New(WithAgent(AgentGrok), WithExecutable(fake)).
		Run(context.Background(), Request{Prompt: "fail"})
	require.Error(t, err)
	require.NotNil(t, result)

	var grokErr *GrokError
	require.True(t, errors.As(err, &grokErr))
	assert.Contains(t, grokErr.Error(), "maximum number of turns")
	var exitErr *ExitError
	assert.False(t, errors.As(err, &exitErr))
}

func TestGrokPrepareBuildsAdvancedArgsAndBuffersStdin(t *testing.T) {
	runner := New(WithAgent(AgentGrok))
	args, stdin, cleanup, err := runner.prepare(Request{
		Prompt:          "prompt",
		Stdin:           strings.NewReader("extra"),
		Dir:             "/work",
		Model:           "grok-code-fast-1",
		Profile:         "reviewer",
		Sandbox:         SandboxWorkspaceWrite,
		PermissionMode:  PermissionAcceptEdits,
		AllowedTools:    []string{"Bash(git *)"},
		DisallowedTools: []string{"WebSearch"},
		OutputSchema:    []byte(`{"type":"object"}`),
		ResumeID:        "session-9",
	})
	require.NoError(t, err)
	require.Nil(t, stdin)
	require.NotNil(t, cleanup)

	for _, expected := range []string{
		"--cwd", "/work",
		"--model", "grok-code-fast-1",
		"--agent", "reviewer",
		"--sandbox", "workspace",
		"--permission-mode", "acceptEdits",
		"--allow", "Bash(git *)",
		"--deny", "WebSearch",
		"--json-schema", `{"type":"object"}`,
		"--resume", "session-9",
	} {
		assert.Contains(t, args, expected)
	}
	promptIndex := indexOf(args, "--prompt-file")
	require.NotEqual(t, -1, promptIndex)
	promptPath := args[promptIndex+1]
	prompt, err := os.ReadFile(promptPath)
	require.NoError(t, err)
	assert.Equal(t, "prompt\n\n<stdin>\nextra\n</stdin>\n", string(prompt))
	cleanup()
	assert.NoFileExists(t, promptPath)
}

func TestGrokResumeUsesSavedSandboxUnlessExplicit(t *testing.T) {
	runner := New(WithAgent(AgentGrok))
	args, _, cleanup, err := runner.prepare(Request{Prompt: "continue", ResumeLast: true})
	require.NoError(t, err)
	t.Cleanup(cleanup)
	assert.NotContains(t, args, "--sandbox")
	assert.Contains(t, args, "--continue")
}

func TestGrokToolKindMapping(t *testing.T) {
	decoder := newGrokEventDecoder()
	events, err := decoder.decode(
		[]byte(`{"type":"assistant","message":{"id":"m","content":[{"type":"tool_use","id":"cmd","name":"run_terminal_command","input":{"command":"true"}},{"type":"tool_use","id":"edit","name":"search_replace","input":{"target_file":"a.go"}},{"type":"tool_use","id":"sub","name":"spawn_subagent","input":{}},{"type":"tool_use","id":"todo","name":"todo_write","input":{}},{"type":"tool_use","id":"mcp","name":"use_tool","input":{}},{"type":"server_tool_use","id":"web","name":"web_search","input":{}}]}}`),
		"run",
		"session",
		time.Now(),
	)
	require.NoError(t, err)
	require.Len(t, events, 6)
	assert.Equal(t, ItemCommandExecution, events[0].ItemStarted.Item.Type)
	assert.Equal(t, ItemFileChange, events[1].ItemStarted.Item.Type)
	assert.Equal(t, ItemCollabToolCall, events[2].ItemStarted.Item.Type)
	assert.Equal(t, ItemPlanUpdate, events[3].ItemStarted.Item.Type)
	assert.Equal(t, ItemMCPToolCall, events[4].ItemStarted.Item.Type)
	assert.Equal(t, ItemWebSearch, events[5].ItemStarted.Item.Type)
}

func TestValidateGrokRequest(t *testing.T) {
	for _, req := range []Request{
		{},
		{Prompt: "x", AddDirs: []string{"/other"}},
		{Prompt: "x", Approval: ApprovalNever, PermissionMode: PermissionDontAsk},
		{Prompt: "x", ResumeID: "id", ResumeLast: true},
	} {
		assert.Error(t, validateGrokRequest(req))
	}
}

func TestLiveGrokRun(t *testing.T) {
	if os.Getenv("CODEXCW_LIVE_GROK") != "1" {
		t.Skip("set CODEXCW_LIVE_GROK=1 to run the authenticated Grok CLI")
	}
	ctx, cancel := context.WithTimeout(context.Background(), time.Minute)
	defer cancel()

	result, err := New(WithAgent(AgentGrok)).Run(
		ctx,
		Request{Prompt: "Reply with exactly: CODEXCW_GROK_OK"},
	)
	require.NoError(t, err)
	assert.Equal(t, "CODEXCW_GROK_OK", strings.TrimSpace(result.FinalMessage))
	assert.NotEmpty(t, result.ThreadID)
}

func TestGrokPromptReadFailureCleansUp(t *testing.T) {
	reader, writer := io.Pipe()
	require.NoError(t, writer.CloseWithError(errors.New("read failed")))
	_, _, cleanup, err := New(WithAgent(AgentGrok)).prepare(Request{Stdin: reader})
	require.Error(t, err)
	assert.Nil(t, cleanup)
}
