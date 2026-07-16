package codexcw

import (
	"bytes"
	"context"
	"encoding/json"
	"errors"
	"fmt"
	"os"
	"os/exec"
	"regexp"
	"strconv"
	"strings"
	"time"
)

const claudeAccountUsageTimeout = 10 * time.Second

var claudeUsageWindowPattern = regexp.MustCompile(
	`(?m)^([^:\n]+):\s*([0-9]+(?:\.[0-9]+)?)% used(?:\s*·\s*resets\s+(.+))?$`,
)

// ClaudeAccountUsageRequest configures one Claude account usage lookup.
type ClaudeAccountUsageRequest struct {
	// Executable is the claude executable path. Defaults to "claude".
	Executable string

	// Env appends environment variables for the Claude child process.
	Env map[string]string

	// Timeout bounds the lookup. Values <= 0 use the 10s default.
	Timeout time.Duration
}

// ClaudeAccountUsage is the usage report returned by Claude Code's /usage command.
type ClaudeAccountUsage struct {
	// Report is Claude Code's human-readable usage report.
	Report string

	// Windows contains percentage and reset details parsed from the report.
	Windows []ClaudeAccountUsageWindow

	// Raw is the complete JSON result emitted by Claude Code.
	Raw json.RawMessage
}

// ClaudeAccountUsageWindow is one window from Claude Code's /usage report.
type ClaudeAccountUsageWindow struct {
	// Label is the display label reported by Claude Code.
	Label string

	// UsedPercent is the percentage consumed in this window.
	UsedPercent float64

	// ResetsAt is Claude Code's human-readable reset time.
	ResetsAt string
}

// GetClaudeAccountUsage reads account usage through Claude Code's /usage command.
func GetClaudeAccountUsage(ctx context.Context, req ClaudeAccountUsageRequest) (*ClaudeAccountUsage, error) {
	if ctx == nil {
		ctx = context.Background()
	}
	executable := strings.TrimSpace(req.Executable)
	if executable == "" {
		executable = string(AgentClaude)
	}
	timeout := req.Timeout
	if timeout <= 0 {
		timeout = claudeAccountUsageTimeout
	}
	runCtx, cancel := context.WithTimeout(ctx, timeout)
	defer cancel()

	// #nosec G204 -- launching the configured Claude executable is the wrapper boundary.
	cmd := exec.CommandContext(
		runCtx,
		executable,
		"-p",
		"--output-format",
		"json",
		"--no-session-persistence",
	)
	cmd.Stdin = strings.NewReader("/usage")
	cmd.Env = append(os.Environ(), mapEnv(req.Env)...)
	cmd.WaitDelay = time.Second
	var stderr bytes.Buffer
	cmd.Stderr = &stderr

	raw, runErr := cmd.Output()
	raw = bytes.TrimSpace(raw)
	if runErr != nil {
		if err := runCtx.Err(); err != nil {
			return nil, fmt.Errorf("read claude account usage: %w", err)
		}
		var exitErr *exec.ExitError
		if errors.As(runErr, &exitErr) {
			return nil, &ExitError{
				Code:   exitErr.ExitCode(),
				Stderr: stderr.String(),
				Err:    runErr,
			}
		}
		return nil, fmt.Errorf("read claude account usage: %w%s", runErr, stderrSuffix(stderr.String()))
	}

	var wire struct {
		IsError bool     `json:"is_error"`
		Result  string   `json:"result"`
		Errors  []string `json:"errors"`
	}
	if err := json.Unmarshal(raw, &wire); err != nil {
		return nil, fmt.Errorf("decode claude account usage: %w", err)
	}
	if wire.IsError {
		message := strings.TrimSpace(wire.Result)
		if message == "" {
			message = strings.Join(wire.Errors, "; ")
		}
		if message == "" {
			message = "unknown error"
		}
		return nil, fmt.Errorf("claude account usage failed: %s", message)
	}

	return &ClaudeAccountUsage{
		Report:  wire.Result,
		Windows: parseClaudeUsageWindows(wire.Result),
		Raw:     append(json.RawMessage(nil), raw...),
	}, nil
}

func parseClaudeUsageWindows(report string) []ClaudeAccountUsageWindow {
	matches := claudeUsageWindowPattern.FindAllStringSubmatch(report, -1)
	windows := make([]ClaudeAccountUsageWindow, 0, len(matches))
	for _, match := range matches {
		usedPercent, err := strconv.ParseFloat(match[2], 64)
		if err != nil {
			continue
		}
		label := strings.TrimSpace(match[1])
		windows = append(windows, ClaudeAccountUsageWindow{
			Label:       label,
			UsedPercent: usedPercent,
			ResetsAt:    strings.TrimSpace(match[3]),
		})
	}
	return windows
}

func mapEnv(values map[string]string) []string {
	env := make([]string, 0, len(values))
	for key, value := range values {
		env = append(env, key+"="+value)
	}
	return env
}
