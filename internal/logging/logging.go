// Package logging provides a single slog handler configured from the CLI flag.
//
// All log output goes to stderr. stdout is reserved for command output so
// callers can pipe it without polluting the data stream with diagnostics.
package logging

import (
	"fmt"
	"log/slog"
	"os"
	"strings"
)

// Configure installs slog's default logger with the requested level.
// Accepts "debug", "info", "warn", "error" (case-insensitive).
func Configure(level string) error {
	lvl, err := parseLevel(level)
	if err != nil {
		return err
	}
	handler := slog.NewTextHandler(os.Stderr, &slog.HandlerOptions{Level: lvl})
	slog.SetDefault(slog.New(handler))
	return nil
}

func parseLevel(s string) (slog.Level, error) {
	switch strings.ToLower(strings.TrimSpace(s)) {
	case "debug":
		return slog.LevelDebug, nil
	case "", "info":
		return slog.LevelInfo, nil
	case "warn", "warning":
		return slog.LevelWarn, nil
	case "error":
		return slog.LevelError, nil
	default:
		return slog.LevelInfo, fmt.Errorf("invalid log level %q", s)
	}
}
