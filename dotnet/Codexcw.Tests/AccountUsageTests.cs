namespace C3OSS.Codexcw.Tests;

public sealed class AccountUsageTests : IDisposable
{
    private readonly FakeAgentDir _dir = new();

    public void Dispose() => _dir.Dispose();

    private const string FullServerBody = """
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
            *'"method":"account/read"'*)
              printf '%s\n' '{"id":4,"result":{"account":{"type":"chatgpt","email":"stub@example.com","planType":"pro"},"requiresOpenaiAuth":false}}'
              ;;
          esac
        done
        """;

    [UnixOnlyFact]
    public async Task ReadsRateLimitsUsageAndAccount()
    {
        var envFile = Path.Combine(_dir.Root, "env.txt");
        var codexHome = Path.Combine(_dir.Root, "codex-home");
        var fake = _dir.WriteFakeCodex(FullServerBody);

        var usage = await CodexAccount.GetAccountUsageAsync(new AccountUsageRequest
        {
            Executable = fake,
            Env = new Dictionary<string, string>
            {
                ["CODEXCW_ARGS_FILE"] = _dir.ArgsFile,
                ["CODEXCW_ENV_FILE"] = envFile,
                ["CODEX_HOME"] = codexHome,
            },
        });

        Assert.NotNull(usage.Account);
        Assert.Equal("stub@example.com", usage.Account!.Email);
        Assert.Equal("pro", usage.Account!.PlanType);
        Assert.Equal("chatgpt", usage.Account!.Type);
        Assert.Equal("pro", usage.RateLimits.PlanType);
        Assert.NotNull(usage.RateLimits.Primary);
        Assert.Equal(12.5, usage.RateLimits.Primary!.UsedPercent);
        Assert.Equal(300, usage.RateLimits.Primary!.WindowDurationMinutes);
        Assert.Equal(1766948068, usage.RateLimits.Primary!.ResetsAt);
        Assert.NotNull(usage.RateLimits.Credits);
        Assert.True(usage.RateLimits.Credits!.HasCredits);
        Assert.Equal("7", usage.RateLimits.Credits!.Balance);
        Assert.NotNull(usage.RateLimits.IndividualLimit);
        Assert.Equal(100, usage.RateLimits.IndividualLimit!.Limit);
        Assert.Equal(75.0, usage.RateLimits.IndividualLimit!.RemainingPercent);
        Assert.Equal(1768000000, usage.RateLimits.IndividualLimit!.ResetsAt);
        Assert.Contains("spark", usage.RateLimitsByLimitId.Keys);
        Assert.Equal("Codex Spark", usage.RateLimitsByLimitId["spark"].LimitName);
        Assert.NotNull(usage.TokenUsage);
        Assert.Equal("12345678901234567890", usage.TokenUsage!.Summary.LifetimeTokens);
        Assert.Equal("456", usage.TokenUsage!.Summary.PeakDailyTokens);
        Assert.Equal("789", usage.TokenUsage!.Summary.LongestRunningTurnSeconds);
        Assert.Equal("3", usage.TokenUsage!.Summary.CurrentStreakDays);
        var bucket = Assert.Single(usage.TokenUsage!.DailyUsageBuckets);
        Assert.Equal("2026-07-07", bucket.StartDate);
        Assert.Equal("42", bucket.Tokens);
        Assert.Contains("rateLimits", usage.RawRateLimits, StringComparison.Ordinal);
        Assert.Contains("lifetimeTokens", usage.RawTokenUsage, StringComparison.Ordinal);
        Assert.Contains("stub@example.com", usage.RawAccount, StringComparison.Ordinal);

        Assert.Equal(
            new[] { "-s", "read-only", "-a", "untrusted", "app-server", "--stdio" },
            _dir.ReadArgs());
        Assert.Equal(codexHome, File.ReadAllText(envFile).Trim());
    }

    [UnixOnlyFact]
    public async Task DefaultsCodexHomeWhenUnset()
    {
        var envFile = Path.Combine(_dir.Root, "env.txt");
        var fake = _dir.WriteFakeCodex("""
            printf '%s\n' "$CODEX_HOME" > "$CODEXCW_ENV_FILE"
            while IFS= read -r line; do
              case "$line" in
                *'"method":"initialized"'*) ;;
                *'"method":"initialize"'*) printf '%s\n' '{"id":1,"result":{}}' ;;
                *'"method":"account/rateLimits/read"'*) printf '%s\n' '{"id":2,"result":{"rateLimits":{"credits":{"hasCredits":true,"unlimited":false,"balance":"1"}}}}' ;;
                *'"method":"account/usage/read"'*) printf '%s\n' '{"id":3,"result":{"summary":{}}}' ;;
                *'"method":"account/read"'*) printf '%s\n' '{"id":4,"result":{}}' ;;
              esac
            done
            """);

        var previous = Environment.GetEnvironmentVariable("CODEX_HOME");
        Environment.SetEnvironmentVariable("CODEX_HOME", null);
        try
        {
            var usage = await CodexAccount.GetAccountUsageAsync(new AccountUsageRequest
            {
                Executable = fake,
                Env = new Dictionary<string, string> { ["CODEXCW_ENV_FILE"] = envFile },
            });
            Assert.Null(usage.Account);
        }
        finally
        {
            Environment.SetEnvironmentVariable("CODEX_HOME", previous);
        }

        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        Assert.Equal(Path.Combine(home, ".codex"), File.ReadAllText(envFile).Trim());
    }

    [UnixOnlyFact]
    public async Task OptionalReadsTolerateRpcErrors()
    {
        var fake = _dir.WriteFakeCodex("""
            while IFS= read -r line; do
              case "$line" in
                *'"method":"initialized"'*) ;;
                *'"method":"initialize"'*) printf '%s\n' '{"id":1,"result":{}}' ;;
                *'"method":"account/rateLimits/read"'*) printf '%s\n' '{"id":2,"result":{"rate_limits":{"plan_type":"free","primary":{"used_percent":"33.5","window_duration_mins":"60","resets_at":"1767000001"}}}}' ;;
                *'"method":"account/usage/read"'*) printf '%s\n' '{"id":3,"error":{"code":-32601,"message":"not supported"}}' ;;
                *'"method":"account/read"'*) printf '%s\n' '{"id":4,"error":{"code":-32600,"message":"nope"}}' ;;
              esac
            done
            """);

        var usage = await CodexAccount.GetAccountUsageAsync(new AccountUsageRequest { Executable = fake });

        Assert.Null(usage.Account);
        Assert.Null(usage.TokenUsage);
        Assert.Equal("", usage.RawTokenUsage);
        Assert.Equal("", usage.RawAccount);
        // snake_case keys and string-encoded numbers parse the same way.
        Assert.Equal("free", usage.RateLimits.PlanType);
        Assert.Equal(33.5, usage.RateLimits.Primary!.UsedPercent);
        Assert.Equal(60, usage.RateLimits.Primary!.WindowDurationMinutes);
        Assert.Equal(1767000001, usage.RateLimits.Primary!.ResetsAt);
    }

    [UnixOnlyFact]
    public async Task RequiredRateLimitsFailureAborts()
    {
        var fake = _dir.WriteFakeCodex("""
            while IFS= read -r line; do
              case "$line" in
                *'"method":"initialized"'*) ;;
                *'"method":"initialize"'*) printf '%s\n' '{"id":1,"result":{}}' ;;
                *'"method":"account/rateLimits/read"'*) printf '%s\n' '{"id":2,"error":{"code":-32000,"message":"account unavailable"}}' ;;
              esac
            done
            """);

        var error = await Assert.ThrowsAsync<ProcessException>(() =>
            CodexAccount.GetAccountUsageAsync(new AccountUsageRequest { Executable = fake }));
        Assert.StartsWith("read codex account rate limits:", error.Message, StringComparison.Ordinal);
        Assert.Contains("account unavailable", error.Message, StringComparison.Ordinal);
    }

    [UnixOnlyFact]
    public async Task TransportFailureCarriesStderr()
    {
        var fake = _dir.WriteFakeCodex("""
            printf '%s\n' 'app-server exploded' >&2
            exit 1
            """);

        var error = await Assert.ThrowsAsync<ProcessException>(() =>
            CodexAccount.GetAccountUsageAsync(new AccountUsageRequest
            {
                Executable = fake,
                Timeout = TimeSpan.FromSeconds(2),
            }));
        Assert.StartsWith("initialize codex app-server:", error.Message, StringComparison.Ordinal);
        Assert.Contains("app-server exploded", error.Message, StringComparison.Ordinal);
    }
}

public sealed class ClaudeAccountUsageTests : IDisposable
{
    private readonly FakeAgentDir _dir = new();

    public void Dispose() => _dir.Dispose();

    [Fact]
    public void ParsesUsageWindowsFromReport()
    {
        var windows = ClaudeAccount.ParseUsageWindows(
            "You are currently using your subscription\n\n" +
            "Current session: 13% used · resets Jul 16 at 3:50pm (America/Sao_Paulo)\n" +
            "Current week (all models): 5.5% used · resets Jul 18 at 9am (America/Sao_Paulo)\n" +
            "No percent here\n" +
            "Weird: notanumber% used");

        Assert.Equal(2, windows.Count);
        Assert.Equal("Current session", windows[0].Label);
        Assert.Equal(13, windows[0].UsedPercent);
        Assert.Equal("Jul 16 at 3:50pm (America/Sao_Paulo)", windows[0].ResetsAt);
        Assert.Equal(5.5, windows[1].UsedPercent);
    }

    [UnixOnlyFact]
    public async Task ErrorReportThrows()
    {
        var fake = _dir.WriteFakeCodex("""
            cat >/dev/null
            printf '%s\n' '{"type":"result","is_error":true,"result":"","errors":["no auth","expired"]}'
            """);

        var error = await Assert.ThrowsAsync<ProcessException>(() =>
            ClaudeAccount.GetClaudeAccountUsageAsync(new ClaudeAccountUsageRequest { Executable = fake }));
        Assert.Equal("claude account usage failed: no auth; expired", error.Message);
    }

    [UnixOnlyFact]
    public async Task NonZeroExitBecomesExitException()
    {
        var fake = _dir.WriteFakeCodex("""
            cat >/dev/null
            printf '%s\n' 'claude broke' >&2
            exit 3
            """);

        var error = await Assert.ThrowsAsync<ExitException>(() =>
            ClaudeAccount.GetClaudeAccountUsageAsync(new ClaudeAccountUsageRequest { Executable = fake }));
        Assert.Equal(3, error.ExitCode);
        Assert.Contains("claude broke", error.Stderr, StringComparison.Ordinal);
    }
}

public sealed class LiveTests
{
    [GatedFact("CODEXCW_LIVE_CODEX")]
    public async Task LiveGetAccountUsage()
    {
        var usage = await CodexAccount.GetAccountUsageAsync();
        Assert.NotEmpty(usage.RawRateLimits);
    }

    [GatedFact("CODEXCW_LIVE_TEST")]
    public async Task LiveClaudeRunNormalizesBashExitAndUsage()
    {
        var workDir = Directory.CreateTempSubdirectory("codexcw-live-").FullName;
        try
        {
            var runner = new Runner(new RunnerOptions { Agent = Agent.Claude });
            var result = await runner.RunAsync(new Request
            {
                Prompt = "Use Bash exactly once to run: sh -c 'exit 7'. Then briefly state the exit code.",
                Dir = workDir,
                Model = ClaudeModels.Haiku,
                DangerouslyBypassSandbox = true,
            }, cancellationToken: new CancellationTokenSource(TimeSpan.FromMinutes(1)).Token);

            Assert.True(result.Usage.TotalTokens > 0);
            Assert.True(result.Usage.TotalCostUsd > 0);
            Assert.NotEmpty(result.Usage.ModelUsage);

            var command = result.Events.FirstOrDefault(e =>
                e.ItemCompleted?.Item.Kind == ItemKind.CommandExecution);
            Assert.NotNull(command);
            Assert.Equal("failed", command!.ItemCompleted!.Item.Status);
            Assert.Equal(7, command.ItemCompleted!.Item.ExitCode);
        }
        finally
        {
            Directory.Delete(workDir, recursive: true);
        }
    }

    [GatedFact("CODEXCW_LIVE_TEST")]
    public async Task LiveGetClaudeAccountUsage()
    {
        var usage = await ClaudeAccount.GetClaudeAccountUsageAsync(new ClaudeAccountUsageRequest
        {
            Timeout = TimeSpan.FromSeconds(60),
        });
        Assert.NotEmpty(usage.Report);
    }
}
