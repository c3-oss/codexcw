package codexcw

import (
	"context"
	"errors"
	"io"
	"os"
	"path/filepath"
	"strings"
	"testing"

	"github.com/stretchr/testify/assert"
	"github.com/stretchr/testify/require"
)

func TestClaudeRunNormalizesEvents(t *testing.T) {
	argsFile := filepath.Join(t.TempDir(), "args.txt")
	stdinFile := filepath.Join(t.TempDir(), "stdin.txt")
	fake := writeFakeCodex(t, `
record_args "$@"
cat > "$CODEXCW_STDIN_FILE"
printf '%s\n' '{"type":"system","subtype":"init","cwd":"/work","session_id":"sess-1","model":"claude-haiku-4-5"}'
printf '%s\n' '{"type":"assistant","message":{"id":"msg_1","role":"assistant","content":[{"type":"thinking","thinking":"pondering"}]},"session_id":"sess-1"}'
printf '%s\n' '{"type":"assistant","message":{"id":"msg_1","role":"assistant","content":[{"type":"tool_use","id":"toolu_1","name":"Write","input":{"file_path":"/work/hello.txt","content":"hello"}}]},"session_id":"sess-1"}'
printf '%s\n' '{"type":"rate_limit_event","rate_limit_info":{"status":"allowed"},"session_id":"sess-1"}'
printf '%s\n' '{"type":"user","message":{"role":"user","content":[{"tool_use_id":"toolu_1","type":"tool_result","content":"File created successfully"}]},"session_id":"sess-1","tool_use_result":{"type":"create","filePath":"/work/hello.txt"}}'
printf '%s\n' '{"type":"assistant","message":{"id":"msg_2","role":"assistant","content":[{"type":"text","text":"Done."}]},"session_id":"sess-1"}'
printf '%s\n' '{"type":"result","subtype":"success","is_error":false,"num_turns":2,"result":"Done.","session_id":"sess-1","usage":{"input_tokens":18,"cache_creation_input_tokens":3750,"cache_read_input_tokens":45921,"output_tokens":380}}'
`)

	runner := New(
		WithAgent(AgentClaude),
		WithExecutable(fake),
		WithEnv("CODEXCW_ARGS_FILE="+argsFile, "CODEXCW_STDIN_FILE="+stdinFile),
	)

	result, err := runner.Run(context.Background(), Request{Prompt: "create hello.txt", Model: ClaudeModelHaiku})
	require.NoError(t, err)
	require.NotNil(t, result)

	assert.Equal(t, "sess-1", result.ThreadID)
	assert.Equal(t, "Done.", result.FinalMessage)
	assert.Equal(t, int64(18), result.Usage.InputTokens)
	assert.Equal(t, int64(45921), result.Usage.CachedInputTokens)
	assert.Equal(t, int64(380), result.Usage.OutputTokens)

	types := make([]EventType, 0, len(result.Events))
	for _, event := range result.Events {
		types = append(types, event.Type)
		assert.Equal(t, "sess-1", event.ThreadID)
		assert.NotEmpty(t, event.Raw)
	}
	assert.Equal(t, []EventType{
		EventThreadStarted,
		EventTurnStarted,
		EventItemCompleted,
		EventItemStarted,
		EventType("rate_limit_event"),
		EventItemCompleted,
		EventItemCompleted,
		EventTurnCompleted,
	}, types)

	reasoning := result.Events[2].ItemCompleted.Item
	assert.Equal(t, ItemReasoning, reasoning.Type)
	assert.Equal(t, "pondering", reasoning.Text)

	started := result.Events[3].ItemStarted.Item
	assert.Equal(t, ItemFileChange, started.Type)
	assert.Equal(t, "toolu_1", started.ID)
	assert.Equal(t, "in_progress", started.Status)
	require.Len(t, started.Changes, 1)
	assert.Equal(t, "/work/hello.txt", started.Changes[0].Path)

	completed := result.Events[5].ItemCompleted.Item
	assert.Equal(t, ItemFileChange, completed.Type)
	assert.Equal(t, "toolu_1", completed.ID)
	assert.Equal(t, "completed", completed.Status)
	assert.Equal(t, "File created successfully", completed.AggregatedOutput)
	require.Len(t, completed.Changes, 1)
	assert.Equal(t, "add", completed.Changes[0].Kind)

	message := result.Events[6].ItemCompleted.Item
	assert.Equal(t, ItemAgentMessage, message.Type)
	assert.Equal(t, "Done.", message.Text)

	stdinBytes, err := os.ReadFile(stdinFile)
	require.NoError(t, err)
	assert.Equal(t, "create hello.txt", string(stdinBytes))

	args := readArgs(t, argsFile)
	assert.Equal(t, "-p", args[0])
	assert.Contains(t, args, "--output-format")
	assert.Contains(t, args, "stream-json")
	assert.Contains(t, args, "--verbose")
	assert.Contains(t, args, "--model")
	assert.Contains(t, args, "haiku")
	assert.Contains(t, args, "--no-session-persistence")
}

func TestClaudeErrorResultReturnsCodexError(t *testing.T) {
	fake := writeFakeCodex(t, `
record_args "$@"
cat >/dev/null
printf '%s\n' '{"type":"system","subtype":"init","session_id":"sess-err"}'
printf '%s\n' '{"type":"result","subtype":"success","is_error":true,"result":"There is an issue with the selected model","session_id":"sess-err","usage":{"input_tokens":0,"output_tokens":0}}'
exit 1
`)

	result, err := New(WithAgent(AgentClaude), WithExecutable(fake)).
		Run(context.Background(), Request{Prompt: "hi", Model: "totally-bogus-model"})
	require.Error(t, err)
	require.NotNil(t, result)

	var codexErr *CodexError
	require.True(t, errors.As(err, &codexErr))
	assert.Contains(t, codexErr.Error(), "issue with the selected model")
	require.NotNil(t, codexErr.Event.TurnFailed)
}

func TestClaudeStructuredOutputBecomesFinalMessage(t *testing.T) {
	fake := writeFakeCodex(t, `
record_args "$@"
cat >/dev/null
printf '%s\n' '{"type":"system","subtype":"init","session_id":"sess-schema"}'
printf '%s\n' '{"type":"result","subtype":"success","is_error":false,"result":"{\"name\":\"Ada\"}","structured_output":{"name":"Ada"},"session_id":"sess-schema","usage":{"input_tokens":9,"output_tokens":205}}'
`)

	result, err := New(WithAgent(AgentClaude), WithExecutable(fake)).Run(
		context.Background(),
		Request{Prompt: "who?", OutputSchema: []byte(`{"type":"object"}`)},
	)
	require.NoError(t, err)
	assert.Equal(t, `{"name":"Ada"}`, result.FinalMessage)

	synthetic := result.Events[len(result.Events)-2]
	require.NotNil(t, synthetic.ItemCompleted)
	assert.Equal(t, ItemAgentMessage, synthetic.ItemCompleted.Item.Type)
}

func TestClaudeToolKindMapping(t *testing.T) {
	fake := writeFakeCodex(t, `
record_args "$@"
cat >/dev/null
printf '%s\n' '{"type":"system","subtype":"init","session_id":"sess-tools"}'
printf '%s\n' '{"type":"assistant","message":{"id":"msg_1","content":[{"type":"tool_use","id":"t1","name":"Bash","input":{"command":"ls -la"}},{"type":"tool_use","id":"t2","name":"mcp__github__get_issue","input":{}},{"type":"tool_use","id":"t3","name":"WebSearch","input":{}},{"type":"tool_use","id":"t4","name":"Read","input":{}}]},"session_id":"sess-tools"}'
printf '%s\n' '{"type":"user","message":{"content":[{"type":"tool_result","tool_use_id":"t1","content":[{"type":"text","text":"total 0"}],"is_error":false}]},"session_id":"sess-tools"}'
printf '%s\n' '{"type":"result","subtype":"success","is_error":false,"result":"ok","session_id":"sess-tools","usage":{"input_tokens":1,"output_tokens":1}}'
`)

	result, err := New(WithAgent(AgentClaude), WithExecutable(fake)).
		Run(context.Background(), Request{Prompt: "tools"})
	require.NoError(t, err)

	kinds := map[string]ItemType{}
	for _, event := range result.Events {
		if event.ItemStarted != nil {
			kinds[event.ItemStarted.Item.ID] = event.ItemStarted.Item.Type
		}
	}
	assert.Equal(t, ItemCommandExecution, kinds["t1"])
	assert.Equal(t, ItemMCPToolCall, kinds["t2"])
	assert.Equal(t, ItemWebSearch, kinds["t3"])
	assert.Equal(t, ItemToolCall, kinds["t4"])

	for _, event := range result.Events {
		if event.ItemCompleted != nil && event.ItemCompleted.Item.ID == "t1" {
			assert.Equal(t, "ls -la", event.ItemCompleted.Item.Command)
			assert.Equal(t, "total 0", event.ItemCompleted.Item.AggregatedOutput)
		}
	}
}

func TestClaudePrepareBuildsAdvancedArgs(t *testing.T) {
	runner := New(WithAgent(AgentClaude))
	req := Request{
		Prompt:                   "prompt",
		Stdin:                    strings.NewReader("extra"),
		Dir:                      "/work",
		AddDirs:                  []string{"/other"},
		Model:                    ClaudeModelOpus,
		PermissionMode:           PermissionAcceptEdits,
		AllowedTools:             []string{"Bash(git *)", "Edit"},
		DisallowedTools:          []string{"WebSearch"},
		OutputSchema:             []byte(`{"type":"object"}`),
		DangerouslyBypassSandbox: true,
		Persistent:               true,
	}

	args, stdin, cleanup, err := runner.prepare(req)
	require.NoError(t, err)
	require.Nil(t, cleanup)

	data, err := io.ReadAll(stdin)
	require.NoError(t, err)
	assert.Equal(t, "prompt\n\n<stdin>\nextra\n</stdin>\n", string(data))

	for _, want := range []string{
		"-p", "--output-format", "stream-json", "--verbose",
		"--model", "opus", "--permission-mode", "acceptEdits",
		"--allowed-tools", "Bash(git *)", "Edit", "--disallowed-tools", "WebSearch",
		"--dangerously-skip-permissions", "--add-dir", "/other",
		"--json-schema", `{"type":"object"}`,
	} {
		assert.Contains(t, args, want)
	}
	assert.NotContains(t, args, "--no-session-persistence")
	assert.NotContains(t, args, "-C")
}

func TestClaudePrepareResumeArgs(t *testing.T) {
	runner := New(WithAgent(AgentClaude))

	args, _, _, err := runner.prepare(Request{Prompt: "go on", ResumeID: "sess-9", Persistent: true})
	require.NoError(t, err)
	resumeIndex := indexOf(args, "--resume")
	require.NotEqual(t, -1, resumeIndex)
	assert.Equal(t, "sess-9", args[resumeIndex+1])

	args, _, _, err = runner.prepare(Request{Prompt: "go on", ResumeLast: true, Persistent: true})
	require.NoError(t, err)
	assert.Contains(t, args, "--continue")
}

func TestClaudePrepareReadsSchemaPath(t *testing.T) {
	schemaPath := filepath.Join(t.TempDir(), "schema.json")
	require.NoError(t, os.WriteFile(schemaPath, []byte(`{"type":"object"}`), 0o600))

	args, _, _, err := New(WithAgent(AgentClaude)).prepare(Request{Prompt: "x", OutputSchemaPath: schemaPath})
	require.NoError(t, err)
	schemaIndex := indexOf(args, "--json-schema")
	require.NotEqual(t, -1, schemaIndex)
	assert.JSONEq(t, `{"type":"object"}`, args[schemaIndex+1])
}

func TestClaudeRunUsesRequestDirAsWorkingDirectory(t *testing.T) {
	dir := t.TempDir()
	pwdFile := filepath.Join(t.TempDir(), "pwd.txt")
	fake := writeFakeCodex(t, `
cat >/dev/null
pwd > "$CODEXCW_PWD_FILE"
printf '%s\n' '{"type":"system","subtype":"init","session_id":"sess-dir"}'
printf '%s\n' '{"type":"result","subtype":"success","is_error":false,"result":"ok","session_id":"sess-dir","usage":{"input_tokens":1,"output_tokens":1}}'
`)

	_, err := New(WithAgent(AgentClaude), WithExecutable(fake), WithEnv("CODEXCW_PWD_FILE="+pwdFile)).
		Run(context.Background(), Request{Prompt: "where", Dir: dir})
	require.NoError(t, err)

	pwd, err := os.ReadFile(pwdFile)
	require.NoError(t, err)
	resolved, err := filepath.EvalSymlinks(dir)
	require.NoError(t, err)
	assert.Equal(t, resolved, strings.TrimSpace(string(pwd)))
}

func TestValidateClaudeRequest(t *testing.T) {
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
		{name: "images", req: Request{Prompt: "x", Images: []string{"a.png"}}, want: ErrInvalidRequest},
		{name: "profile", req: Request{Prompt: "x", Profile: "work"}, want: ErrInvalidRequest},
		{name: "sandbox", req: Request{Prompt: "x", Sandbox: SandboxReadOnly}, want: ErrInvalidRequest},
		{name: "approval", req: Request{Prompt: "x", Approval: ApprovalNever}, want: ErrInvalidRequest},
		{name: "config", req: Request{Prompt: "x", Config: []ConfigOverride{{Key: "a", Value: "b"}}}, want: ErrInvalidRequest},
		{name: "enable", req: Request{Prompt: "x", Enable: []string{"f"}}, want: ErrInvalidRequest},
		{name: "disable", req: Request{Prompt: "x", Disable: []string{"f"}}, want: ErrInvalidRequest},
		{name: "strict config", req: Request{Prompt: "x", StrictConfig: true}, want: ErrInvalidRequest},
		{name: "ignore user config", req: Request{Prompt: "x", IgnoreUserConfig: true}, want: ErrInvalidRequest},
		{name: "ignore rules", req: Request{Prompt: "x", IgnoreRules: true}, want: ErrInvalidRequest},
		{name: "require git repo", req: Request{Prompt: "x", RequireGitRepo: true}, want: ErrInvalidRequest},
		{name: "output last message path", req: Request{Prompt: "x", OutputLastMessagePath: "o.txt"}, want: ErrInvalidRequest},
		{name: "bypass hooks", req: Request{Prompt: "x", DangerouslyBypassHooks: true}, want: ErrInvalidRequest},
		{name: "resume all", req: Request{Prompt: "x", ResumeID: "id", ResumeAll: true}, want: ErrInvalidRequest},
	}

	for _, tt := range tests {
		t.Run(tt.name, func(t *testing.T) {
			err := validateClaudeRequest(tt.req)
			require.Error(t, err)
			assert.ErrorIs(t, err, tt.want)
		})
	}
}

func TestValidateRequestRejectsClaudeOnlyFields(t *testing.T) {
	for _, req := range []Request{
		{Prompt: "x", PermissionMode: PermissionAcceptEdits},
		{Prompt: "x", AllowedTools: []string{"Edit"}},
		{Prompt: "x", DisallowedTools: []string{"Edit"}},
	} {
		assert.ErrorIs(t, validateRequest(req), ErrInvalidRequest)
	}
}
