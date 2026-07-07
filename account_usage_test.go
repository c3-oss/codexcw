package codexcw

import (
	"context"
	"encoding/json"
	"os"
	"path/filepath"
	"strings"
	"testing"
	"time"

	"github.com/stretchr/testify/assert"
	"github.com/stretchr/testify/require"
)

func TestGetAccountUsageReadsRateLimitsAndAccount(t *testing.T) {
	argsFile := filepath.Join(t.TempDir(), "args.txt")
	envFile := filepath.Join(t.TempDir(), "env.txt")
	codexHome := filepath.Join(t.TempDir(), "codex-home")
	fake := writeFakeCodex(t, `
record_args "$@"
printf '%s\n' "$CODEX_HOME" > "$CODEXCW_ENV_FILE"
while IFS= read -r line; do
  case "$line" in
    *'"method":"initialized"'*)
      ;;
    *'"method":"initialize"'*)
      printf '%s\n' '{"id":1,"result":{}}'
      ;;
    *'"method":"account/rateLimits/read"'*)
      printf '%s\n' '{"id":2,"result":{"rateLimits":{"limitId":null,"limitName":null,"planType":"pro","rateLimitReachedType":null,"primary":{"usedPercent":12.5,"windowDurationMins":300,"resetsAt":1766948068},"secondary":{"usedPercent":43,"windowDurationMins":10080,"resetsAt":1767407914},"credits":{"hasCredits":true,"unlimited":false,"balance":"7"},"individualLimit":{"limit":"100","used":25,"remainingPercent":"75","resetsAt":"1768000000"}},"rateLimitsByLimitId":{"spark":{"limitName":"Codex Spark","primary":{"usedPercent":8,"windowDurationMins":300,"resetsAt":1767000000}}}}}'
      ;;
    *'"method":"account/usage/read"'*)
      printf '%s\n' '{"id":3,"result":{"summary":{"lifetimeTokens":"12345678901234567890","peakDailyTokens":456,"longestRunningTurnSec":"789","currentStreakDays":3,"longestStreakDays":"9"},"dailyUsageBuckets":[{"startDate":"2026-07-07","tokens":"42"}]}}'
      ;;
    *'"method":"account/read"'*'"params"'*|*'"params"'*'"method":"account/read"'*)
      printf '%s\n' '{"id":4,"result":{"account":{"type":"chatgpt","email":"stub@example.com","planType":"pro"},"requiresOpenaiAuth":false}}'
      ;;
    *'"method":"account/read"'*)
      printf '%s\n' '{"id":4,"error":{"code":-32600,"message":"Invalid request: missing field params"}}'
      ;;
  esac
done
`)

	usage, err := GetAccountUsage(context.Background(), AccountUsageRequest{
		Executable: fake,
		Env: map[string]string{
			"CODEXCW_ARGS_FILE": argsFile,
			"CODEXCW_ENV_FILE":  envFile,
			"CODEX_HOME":        codexHome,
		},
	})
	require.NoError(t, err)
	require.NotNil(t, usage)
	require.NotNil(t, usage.Account)
	assert.Equal(t, "stub@example.com", usage.Account.Email)
	assert.Equal(t, "pro", usage.Account.PlanType)
	assert.Equal(t, "pro", usage.RateLimits.PlanType)
	require.NotNil(t, usage.RateLimits.Primary)
	assert.Equal(t, 12.5, usage.RateLimits.Primary.UsedPercent)
	assert.Equal(t, 300, usage.RateLimits.Primary.WindowDurationMinutes)
	require.NotNil(t, usage.RateLimits.Credits)
	assert.Equal(t, "7", usage.RateLimits.Credits.Balance)
	require.NotNil(t, usage.RateLimits.IndividualLimit)
	assert.Equal(t, 75.0, usage.RateLimits.IndividualLimit.RemainingPercent)
	require.Contains(t, usage.RateLimitsByLimitID, "spark")
	assert.Equal(t, "Codex Spark", usage.RateLimitsByLimitID["spark"].LimitName)
	require.NotNil(t, usage.TokenUsage)
	assert.Equal(t, "12345678901234567890", usage.TokenUsage.Summary.LifetimeTokens)
	assert.Equal(t, "456", usage.TokenUsage.Summary.PeakDailyTokens)
	require.Len(t, usage.TokenUsage.DailyUsageBuckets, 1)
	assert.Equal(t, "42", usage.TokenUsage.DailyUsageBuckets[0].Tokens)
	assert.Contains(t, string(usage.RawRateLimits), "rateLimits")
	assert.Contains(t, string(usage.RawTokenUsage), "lifetimeTokens")
	assert.Contains(t, string(usage.RawAccount), "stub@example.com")

	args := readArgs(t, argsFile)
	assert.Equal(t, []string{"-s", "read-only", "-a", "untrusted", "app-server", "--stdio"}, args)

	envBytes, err := os.ReadFile(envFile)
	require.NoError(t, err)
	assert.Equal(t, codexHome+"\n", string(envBytes))
}

func TestGetAccountUsageDefaultsCodexHome(t *testing.T) {
	home := t.TempDir()
	t.Setenv("HOME", home)
	t.Setenv("CODEX_HOME", "")

	envFile := filepath.Join(t.TempDir(), "env.txt")
	fake := writeFakeCodex(t, `
printf '%s\n' "$CODEX_HOME" > "$CODEXCW_ENV_FILE"
while IFS= read -r line; do
  case "$line" in
    *'"method":"initialized"'*) ;;
    *'"method":"initialize"'*) printf '%s\n' '{"id":1,"result":{}}' ;;
    *'"method":"account/rateLimits/read"'*) printf '%s\n' '{"id":2,"result":{"rateLimits":{"credits":{"hasCredits":true,"unlimited":false,"balance":"1"}}}}' ;;
    *'"method":"account/usage/read"'*) printf '%s\n' '{"id":3,"result":{"summary":{},"dailyUsageBuckets":null}}' ;;
    *'"method":"account/read"'*) printf '%s\n' '{"id":4,"result":{}}' ;;
  esac
done
`)

	_, err := GetAccountUsage(context.Background(), AccountUsageRequest{
		Executable: fake,
		Env:        map[string]string{"CODEXCW_ENV_FILE": envFile},
	})
	require.NoError(t, err)

	envBytes, err := os.ReadFile(envFile)
	require.NoError(t, err)
	assert.Equal(t, filepath.Join(home, ".codex")+"\n", string(envBytes))
}

func TestGetAccountUsageSurfacesRPCError(t *testing.T) {
	fake := writeFakeCodex(t, `
while IFS= read -r line; do
  case "$line" in
    *'"method":"initialized"'*) ;;
    *'"method":"initialize"'*) printf '%s\n' '{"id":1,"result":{}}' ;;
    *'"method":"account/rateLimits/read"'*) printf '%s\n' '{"id":2,"error":{"message":"login required"}}' ;;
  esac
done
`)

	_, err := GetAccountUsage(context.Background(), AccountUsageRequest{Executable: fake})
	require.Error(t, err)
	assert.Contains(t, err.Error(), "login required")
}

func TestGetAccountUsageToleratesOptionalRPCErrors(t *testing.T) {
	fake := writeFakeCodex(t, `
while IFS= read -r line; do
  case "$line" in
    *'"method":"initialized"'*) ;;
    *'"method":"initialize"'*) printf '%s\n' '{"id":1,"result":{}}' ;;
    *'"method":"account/rateLimits/read"'*) printf '%s\n' '{"id":2,"result":{"rateLimits":{"planType":"pro"}}}' ;;
    *'"method":"account/usage/read"'*) printf '%s\n' '{"id":3,"error":{"code":-32601,"message":"method not found"}}' ;;
    *'"method":"account/read"'*) printf '%s\n' '{"id":4,"error":{"code":-32600,"message":"Invalid request"}}' ;;
  esac
done
`)

	usage, err := GetAccountUsage(context.Background(), AccountUsageRequest{Executable: fake})
	require.NoError(t, err)
	require.NotNil(t, usage)
	assert.Equal(t, "pro", usage.RateLimits.PlanType)
	assert.Nil(t, usage.TokenUsage)
	assert.Nil(t, usage.Account)
}

func TestGetAccountUsageFailsOnOptionalReadTransportError(t *testing.T) {
	fake := writeFakeCodex(t, `
while IFS= read -r line; do
  case "$line" in
    *'"method":"initialized"'*) ;;
    *'"method":"initialize"'*) printf '%s\n' '{"id":1,"result":{}}' ;;
    *'"method":"account/rateLimits/read"'*)
      printf '%s\n' '{"id":2,"result":{"rateLimits":{"planType":"pro"}}}'
      exit 0
      ;;
  esac
done
`)

	_, err := GetAccountUsage(context.Background(), AccountUsageRequest{Executable: fake})
	require.Error(t, err)
	assert.Contains(t, err.Error(), "account token usage")
}

func TestGetAccountUsageCustomTimeout(t *testing.T) {
	fake := writeFakeCodex(t, `
while IFS= read -r line; do
  case "$line" in
    *'"method":"initialized"'*) ;;
    *'"method":"initialize"'*) printf '%s\n' '{"id":1,"result":{}}' ;;
    *'"method":"account/rateLimits/read"'*)
      sleep 5
      printf '%s\n' '{"id":2,"result":{"rateLimits":{}}}'
      ;;
  esac
done
`)

	start := time.Now()
	_, err := GetAccountUsage(context.Background(), AccountUsageRequest{
		Executable: fake,
		Timeout:    100 * time.Millisecond,
	})
	require.Error(t, err)
	assert.ErrorIs(t, err, context.DeadlineExceeded)
	assert.Less(t, time.Since(start), 4*time.Second)
}

func TestAccountRateLimitWindowDecodesStringNumbers(t *testing.T) {
	var window AccountRateLimitWindow
	payload := `{"usedPercent":"12.5","windowDurationMins":"300","resetsAt":"1766948068"}`
	require.NoError(t, json.Unmarshal([]byte(payload), &window))
	assert.Equal(t, 12.5, window.UsedPercent)
	assert.Equal(t, 300, window.WindowDurationMinutes)
	assert.Equal(t, int64(1766948068), window.ResetsAt)
}

func TestLiveGetAccountUsageAndFastMode(t *testing.T) {
	if os.Getenv("CODEXCW_LIVE_CODEX") != "1" {
		t.Skip("set CODEXCW_LIVE_CODEX=1 to run against the real codex executable")
	}

	ctx, cancel := context.WithTimeout(context.Background(), 2*time.Minute)
	defer cancel()

	usage, err := GetAccountUsage(ctx, AccountUsageRequest{})
	require.NoError(t, err)
	require.NotNil(t, usage)
	assert.NotEmpty(t, usage.RawRateLimits)

	result, err := New().Run(ctx, Request{
		Prompt:      "Responda exatamente: OK",
		Dir:         t.TempDir(),
		IgnoreRules: true,
		Config: []ConfigOverride{
			{Key: "service_tier", Value: `"priority"`},
		},
	})
	require.NoError(t, err)
	assert.Contains(t, strings.ToUpper(result.FinalMessage), "OK")
}
