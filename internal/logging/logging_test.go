package logging

import (
	"log/slog"
	"testing"

	"github.com/stretchr/testify/require"
)

func TestParseLevel(t *testing.T) {
	tests := map[string]slog.Level{
		"":        slog.LevelInfo,
		"debug":   slog.LevelDebug,
		"INFO":    slog.LevelInfo,
		"warn":    slog.LevelWarn,
		"warning": slog.LevelWarn,
		"error":   slog.LevelError,
	}
	for input, want := range tests {
		got, err := parseLevel(input)
		require.NoError(t, err, "input %q", input)
		require.Equal(t, want, got, "input %q", input)
	}
}

func TestParseLevelInvalid(t *testing.T) {
	_, err := parseLevel("loud")
	require.Error(t, err)
}

func TestConfigure(t *testing.T) {
	require.NoError(t, Configure("debug"))
	require.NoError(t, Configure("info"))
	require.Error(t, Configure("nope"))
}
