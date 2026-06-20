package cli

import (
	"bytes"
	"os"
	"path/filepath"
	"testing"

	"github.com/stretchr/testify/assert"
	"github.com/stretchr/testify/require"
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
