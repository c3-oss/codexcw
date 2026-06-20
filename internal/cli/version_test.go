package cli

import (
	"bytes"
	"strings"
	"testing"

	"github.com/stretchr/testify/require"
)

func TestVersionCommand(t *testing.T) {
	cmd := newRootCmd()
	cmd.SetArgs([]string{"version"})

	var stdout bytes.Buffer
	cmd.SetOut(&stdout)
	cmd.SetErr(&stdout)

	require.NoError(t, cmd.Execute())

	out := stdout.String()
	for _, want := range []string{"version:", "commit:", "date:"} {
		require.True(t, strings.Contains(out, want), "expected %q in output, got: %s", want, out)
	}
}
