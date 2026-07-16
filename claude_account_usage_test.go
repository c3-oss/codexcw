package codexcw

import (
	"context"
	"encoding/json"
	"os"
	"path/filepath"
	"testing"
	"time"

	"github.com/stretchr/testify/assert"
	"github.com/stretchr/testify/require"
)

func TestGetClaudeAccountUsageReadsReportWindowsAndRaw(t *testing.T) {
	argsFile := filepath.Join(t.TempDir(), "args.txt")
	envFile := filepath.Join(t.TempDir(), "env.txt")
	stdinFile := filepath.Join(t.TempDir(), "stdin.txt")
	fake := writeFakeCodex(t, `
record_args "$@"
cat > "$CODEXCW_STDIN_FILE"
printf '%s' "$CLAUDE_CONFIG_DIR" > "$CODEXCW_ENV_FILE"
printf '%s\n' '{"type":"result","subtype":"success","is_error":false,"result":"You are currently using your subscription\n\nCurrent session: 13% used · resets Jul 16 at 3:50pm (America/Sao_Paulo)\nCurrent week (all models): 5% used · resets Jul 18 at 9am (America/Sao_Paulo)\nCurrent week (Fable): 7.5% used · resets Jul 18 at 9am (America/Sao_Paulo)","total_cost_usd":0,"usage":{},"modelUsage":{}}'
`)

	usage, err := GetClaudeAccountUsage(context.Background(), ClaudeAccountUsageRequest{
		Executable: fake,
		Env: map[string]string{
			"CLAUDE_CONFIG_DIR":  "/tmp/claude-config",
			"CODEXCW_ARGS_FILE":  argsFile,
			"CODEXCW_ENV_FILE":   envFile,
			"CODEXCW_STDIN_FILE": stdinFile,
		},
	})
	require.NoError(t, err)
	require.NotNil(t, usage)

	assert.Contains(t, usage.Report, "Current session")
	require.Len(t, usage.Windows, 3)
	assert.Equal(t, ClaudeAccountUsageWindow{
		Label:       "Current session",
		UsedPercent: 13,
		ResetsAt:    "Jul 16 at 3:50pm (America/Sao_Paulo)",
	}, usage.Windows[0])
	assert.Equal(t, "Current week (all models)", usage.Windows[1].Label)
	assert.Equal(t, 5.0, usage.Windows[1].UsedPercent)
	assert.Equal(t, "Current week (Fable)", usage.Windows[2].Label)
	assert.Equal(t, 7.5, usage.Windows[2].UsedPercent)
	assert.True(t, json.Valid(usage.Raw))

	assert.Equal(t, []string{
		"-p",
		"--output-format",
		"json",
		"--no-session-persistence",
	}, readArgs(t, argsFile))
	stdin, err := os.ReadFile(stdinFile)
	require.NoError(t, err)
	assert.Equal(t, "/usage", string(stdin))
	env, err := os.ReadFile(envFile)
	require.NoError(t, err)
	assert.Equal(t, "/tmp/claude-config", string(env))
}

func TestGetClaudeAccountUsageHonorsTimeout(t *testing.T) {
	fake := writeFakeCodex(t, `sleep 1`)

	_, err := GetClaudeAccountUsage(context.Background(), ClaudeAccountUsageRequest{
		Executable: fake,
		Timeout:    20 * time.Millisecond,
	})
	require.Error(t, err)
	assert.ErrorIs(t, err, context.DeadlineExceeded)
}

func TestGetClaudeAccountUsageReturnsClaudeExitError(t *testing.T) {
	fake := writeFakeCodex(t, `
cat >/dev/null
printf '%s\n' 'not available' >&2
exit 7
`)

	_, err := GetClaudeAccountUsage(context.Background(), ClaudeAccountUsageRequest{Executable: fake})
	require.Error(t, err)

	var exitErr *ExitError
	require.ErrorAs(t, err, &exitErr)
	assert.Equal(t, 7, exitErr.Code)
	assert.Equal(t, "not available\n", exitErr.Stderr)
	assert.Contains(t, exitErr.Error(), "agent exited")
	assert.NotContains(t, exitErr.Error(), "codex")
}

func TestGetClaudeAccountUsageUsesReportedErrors(t *testing.T) {
	fake := writeFakeCodex(t, `
cat >/dev/null
printf '%s\n' '{"type":"result","is_error":true,"result":"","errors":["subscription unavailable","try later"]}'
`)

	_, err := GetClaudeAccountUsage(context.Background(), ClaudeAccountUsageRequest{Executable: fake})
	require.Error(t, err)
	assert.Equal(t, "claude account usage failed: subscription unavailable; try later", err.Error())
}

func TestLiveGetClaudeAccountUsage(t *testing.T) {
	if os.Getenv("CODEXCW_LIVE_TEST") != "1" {
		t.Skip("set CODEXCW_LIVE_TEST=1 to query the authenticated Claude CLI")
	}
	ctx, cancel := context.WithTimeout(context.Background(), 30*time.Second)
	defer cancel()

	usage, err := GetClaudeAccountUsage(ctx, ClaudeAccountUsageRequest{})
	require.NoError(t, err)
	assert.NotEmpty(t, usage.Report)
	assert.NotEmpty(t, usage.Windows)
	assert.True(t, json.Valid(usage.Raw))
}
