package cli

import (
	"bytes"
	"io"
	"os"
	"path/filepath"
	"testing"

	"github.com/stretchr/testify/assert"
	"github.com/stretchr/testify/require"

	"github.com/c3-oss/codexcw"
)

func TestRunCommandPrintsFinalMessage(t *testing.T) {
	fake := writeFakeCodex(t, `
cat >/dev/null
printf '%s\n' '{"type":"thread.started","thread_id":"cli-thread"}'
printf '%s\n' '{"type":"item.completed","item":{"id":"item_0","type":"agent_message","text":"hello"}}'
printf '%s\n' '{"type":"turn.completed","usage":{"input_tokens":1,"output_tokens":1}}'
`)

	cmd := newRootCmd()
	cmd.SetArgs([]string{"run", "--codex-bin", fake, "say hello"})

	var stdout bytes.Buffer
	var stderr bytes.Buffer
	cmd.SetOut(&stdout)
	cmd.SetErr(&stderr)

	require.NoError(t, cmd.Execute())
	assert.Equal(t, "hello\n", stdout.String())
	assert.Contains(t, stderr.String(), "thread cli-thread started")
	assert.Contains(t, stderr.String(), "completed: input=1 output=1 reasoning=0")
}

func TestRunCommandPassesCyberAccessProgram(t *testing.T) {
	argsFile := filepath.Join(t.TempDir(), "args")
	t.Setenv("CODEXCW_ARGS_FILE", argsFile)
	fake := writeFakeCodex(t, `
printf '%s\n' "$@" >"$CODEXCW_ARGS_FILE"
cat >/dev/null
printf '%s\n' '{"type":"thread.started","thread_id":"cli-thread"}'
printf '%s\n' '{"type":"turn.completed","usage":{"input_tokens":1,"output_tokens":1}}'
`)

	cmd := newRootCmd()
	cmd.SetArgs([]string{"run", "--codex-bin", fake, "--cyber-access-program", "daybreak_red", "say hello"})
	cmd.SetOut(&bytes.Buffer{})
	cmd.SetErr(&bytes.Buffer{})

	require.NoError(t, cmd.Execute())
	args, err := os.ReadFile(argsFile)
	require.NoError(t, err)
	assert.Contains(t, string(args), "--cyber-access-program\ndaybreak_red\n")
}

func TestRunCommandRejectsInvalidCyberAccessProgram(t *testing.T) {
	for _, program := range []string{"daybreak_green", ""} {
		t.Run("program="+program, func(t *testing.T) {
			marker := filepath.Join(t.TempDir(), "started")
			t.Setenv("CODEXCW_STARTED_FILE", marker)
			fake := writeFakeCodex(t, `
: >"$CODEXCW_STARTED_FILE"
`)

			// No positional prompt: the flag is rejected without reading stdin.
			stdin := &countingReader{}
			cmd := newRootCmd()
			cmd.SetArgs([]string{"run", "--codex-bin", fake, "--cyber-access-program=" + program})
			cmd.SetIn(stdin)
			cmd.SetOut(&bytes.Buffer{})
			cmd.SetErr(&bytes.Buffer{})

			require.ErrorIs(t, cmd.Execute(), codexcw.ErrInvalidRequest)
			assert.Zero(t, stdin.reads)
			assert.NoFileExists(t, marker)
		})
	}
}

type countingReader struct{ reads int }

func (r *countingReader) Read([]byte) (int, error) {
	r.reads++
	return 0, io.EOF
}

func writeFakeCodex(t *testing.T, body string) string {
	t.Helper()

	dir := t.TempDir()
	path := filepath.Join(dir, "codex")
	script := `#!/bin/sh
set -eu
` + body
	require.NoError(t, os.WriteFile(path, []byte(script), 0o755))
	return path
}
