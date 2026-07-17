using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading.Channels;

namespace C3OSS.Codexcw;

/// <summary>Configures one Codex account usage lookup.</summary>
public sealed record AccountUsageRequest
{
    /// <summary>The codex executable path. Defaults to "codex".</summary>
    public string? Executable { get; init; }

    /// <summary>Environment variables appended for the Codex app-server child process.</summary>
    public IReadOnlyDictionary<string, string> Env { get; init; } =
        new Dictionary<string, string>();

    /// <summary>Bounds each account read request. Non-positive values use the 10s default.</summary>
    public TimeSpan Timeout { get; init; }
}

/// <summary>The account limits and credits reported by Codex app-server.</summary>
public sealed record AccountUsage
{
    /// <summary>The authenticated account when Codex reports it.</summary>
    public AccountUsageAccount? Account { get; init; }

    /// <summary>The account token-usage summary when Codex reports it.</summary>
    public AccountTokenUsage? TokenUsage { get; init; }

    /// <summary>The primary account rate-limit payload.</summary>
    public AccountRateLimits RateLimits { get; init; } = new();

    /// <summary>Additional named rate-limit payloads.</summary>
    public IReadOnlyDictionary<string, AccountRateLimits> RateLimitsByLimitId { get; init; } =
        new Dictionary<string, AccountRateLimits>();

    /// <summary>The raw JSON-RPC result for account/rateLimits/read.</summary>
    public string RawRateLimits { get; init; } = "";

    /// <summary>The raw JSON-RPC result for account/usage/read when available.</summary>
    public string RawTokenUsage { get; init; } = "";

    /// <summary>The raw JSON-RPC result for account/read when available.</summary>
    public string RawAccount { get; init; } = "";
}

/// <summary>The authenticated account reported by Codex.</summary>
public sealed record AccountUsageAccount
{
    /// <summary>The account type, such as "chatgpt" or "apikey".</summary>
    public string Type { get; init; } = "";

    /// <summary>The ChatGPT account email when available.</summary>
    public string Email { get; init; } = "";

    /// <summary>The ChatGPT plan type when available.</summary>
    public string PlanType { get; init; } = "";

    /// <summary>True when Codex reports that OpenAI auth is required.</summary>
    public bool RequiresOpenAIAuth { get; init; }
}

/// <summary>One Codex rate-limit set.</summary>
public sealed record AccountRateLimits
{
    /// <summary>The optional machine identifier for this limit.</summary>
    public string LimitId { get; init; } = "";

    /// <summary>The optional display name for this limit.</summary>
    public string LimitName { get; init; } = "";

    /// <summary>The short rolling usage window when available.</summary>
    public AccountRateLimitWindow? Primary { get; init; }

    /// <summary>The longer usage window when available.</summary>
    public AccountRateLimitWindow? Secondary { get; init; }

    /// <summary>The account credit balance when available.</summary>
    public AccountCredits? Credits { get; init; }

    /// <summary>The account spend or credit control limit when available.</summary>
    public AccountSpendLimit? IndividualLimit { get; init; }

    /// <summary>The plan type associated with this limit set.</summary>
    public string PlanType { get; init; } = "";

    /// <summary>Which limit was reached, when Codex reports it.</summary>
    public string RateLimitReachedType { get; init; } = "";
}

/// <summary>One account usage window.</summary>
public sealed record AccountRateLimitWindow
{
    /// <summary>The percentage of the window already used.</summary>
    public double UsedPercent { get; init; }

    /// <summary>The window duration in minutes when available.</summary>
    public int WindowDurationMinutes { get; init; }

    /// <summary>A Unix timestamp in seconds when available.</summary>
    public long ResetsAt { get; init; }
}

/// <summary>The Codex credit balance snapshot.</summary>
public sealed record AccountCredits
{
    /// <summary>Whether the account has a credit bucket.</summary>
    public bool HasCredits { get; init; }

    /// <summary>Whether credits are unlimited.</summary>
    public bool Unlimited { get; init; }

    /// <summary>The remaining credit balance when available.</summary>
    public string Balance { get; init; } = "";
}

/// <summary>An individual spend or credit-control limit.</summary>
public sealed record AccountSpendLimit
{
    /// <summary>The configured limit when available.</summary>
    public double Limit { get; init; }

    /// <summary>The consumed amount when available.</summary>
    public double Used { get; init; }

    /// <summary>The remaining percentage when available.</summary>
    public double RemainingPercent { get; init; }

    /// <summary>A Unix timestamp in seconds when available.</summary>
    public long ResetsAt { get; init; }
}

/// <summary>The account token-usage summary reported by Codex.</summary>
public sealed record AccountTokenUsage
{
    /// <summary>Aggregate account token-usage metrics.</summary>
    public AccountTokenUsageSummary Summary { get; init; } = new();

    /// <summary>Per-day token usage when available.</summary>
    public IReadOnlyList<AccountTokenUsageDailyBucket> DailyUsageBuckets { get; init; } = [];
}

/// <summary>Aggregate account token-usage metrics.</summary>
public sealed record AccountTokenUsageSummary
{
    /// <summary>The total lifetime token count when available.</summary>
    public string LifetimeTokens { get; init; } = "";

    /// <summary>The peak daily token count when available.</summary>
    public string PeakDailyTokens { get; init; } = "";

    /// <summary>The longest running turn duration when available.</summary>
    public string LongestRunningTurnSeconds { get; init; } = "";

    /// <summary>The current usage streak length when available.</summary>
    public string CurrentStreakDays { get; init; } = "";

    /// <summary>The longest usage streak length when available.</summary>
    public string LongestStreakDays { get; init; } = "";
}

/// <summary>One daily account token-usage bucket.</summary>
public sealed record AccountTokenUsageDailyBucket
{
    /// <summary>The bucket start date.</summary>
    public string StartDate { get; init; } = "";

    /// <summary>The token count for the bucket.</summary>
    public string Tokens { get; init; } = "";
}

/// <summary>Codex account usage lookups through codex app-server.</summary>
public static class CodexAccount
{
    private static readonly TimeSpan InitTimeout = TimeSpan.FromSeconds(8);
    private static readonly TimeSpan DefaultRequestTimeout = TimeSpan.FromSeconds(10);

    /// <summary>Reads Codex account usage and limits through codex app-server.</summary>
    public static async Task<AccountUsage> GetAccountUsageAsync(
        AccountUsageRequest? request = null,
        CancellationToken cancellationToken = default)
    {
        request ??= new AccountUsageRequest();
        var executable = (request.Executable ?? "").Trim();
        if (executable.Length == 0)
        {
            executable = Agent.Codex.Name();
        }
        var requestTimeout = request.Timeout > TimeSpan.Zero ? request.Timeout : DefaultRequestTimeout;

        var startInfo = new ProcessStartInfo
        {
            FileName = executable,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        foreach (var arg in new[] { "-s", "read-only", "-a", "untrusted", "app-server", "--stdio" })
        {
            startInfo.ArgumentList.Add(arg);
        }
        ApplyAccountUsageEnv(startInfo, request.Env);

        using var process = new Process { StartInfo = startInfo };
        try
        {
            process.Start();
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            throw new ProcessException($"start codex app-server: {ex.Message}", ex);
        }

        var stderr = new TailBuffer(Runner.DefaultStderrLimit);
        var stderrPump = stderr.PumpAsync(process.StandardError.BaseStream, CancellationToken.None);
        try
        {
            var client = new JsonRpcClient(
                process.StandardInput.BaseStream,
                process.StandardOutput.BaseStream);

            var initParams = new JsonObject
            {
                ["clientInfo"] = new JsonObject
                {
                    ["name"] = "codexcw",
                    ["version"] = "0.1.0",
                },
            };
            await RequiredRequest(client, "initialize", initParams, InitTimeout,
                "initialize codex app-server", stderr, cancellationToken).ConfigureAwait(false);
            await client.NotifyAsync("initialized", new JsonObject(), cancellationToken).ConfigureAwait(false);

            var rawRateLimits = await RequiredRequest(client, "account/rateLimits/read", null, requestTimeout,
                "read codex account rate limits", stderr, cancellationToken).ConfigureAwait(false);
            AccountRateLimits rateLimits;
            IReadOnlyDictionary<string, AccountRateLimits> byLimitId;
            try
            {
                (rateLimits, byLimitId) = ParseRateLimitsResponse(rawRateLimits);
            }
            catch (JsonException ex)
            {
                throw new ProcessException($"decode codex account rate limits: {ex.Message}", ex);
            }

            var usage = new AccountUsage
            {
                RateLimits = rateLimits,
                RateLimitsByLimitId = byLimitId,
                RawRateLimits = rawRateLimits,
            };

            // account/usage/read and account/read are optional: a JSON-RPC
            // error response means the data is absent, while transport
            // failures abort.
            try
            {
                var rawTokenUsage = await Request(client, "account/usage/read", null, requestTimeout,
                    "read codex account token usage", stderr, cancellationToken).ConfigureAwait(false);
                try
                {
                    usage = usage with
                    {
                        TokenUsage = ParseTokenUsage(rawTokenUsage),
                        RawTokenUsage = rawTokenUsage,
                    };
                }
                catch (JsonException ex)
                {
                    throw new ProcessException($"decode codex account token usage: {ex.Message}", ex);
                }
            }
            catch (JsonRpcErrorException)
            {
            }

            try
            {
                var rawAccount = await Request(client, "account/read", new JsonObject(), requestTimeout,
                    "read codex account", stderr, cancellationToken).ConfigureAwait(false);
                try
                {
                    usage = usage with
                    {
                        Account = ParseAccountResponse(rawAccount),
                        RawAccount = rawAccount,
                    };
                }
                catch (JsonException ex)
                {
                    throw new ProcessException($"decode codex account: {ex.Message}", ex);
                }
            }
            catch (JsonRpcErrorException)
            {
            }

            return usage;
        }
        finally
        {
            Session.KillProcessTree(process);
            try
            {
                await process.WaitForExitAsync(CancellationToken.None)
                    .WaitAsync(TimeSpan.FromSeconds(1), CancellationToken.None).ConfigureAwait(false);
            }
            catch (TimeoutException)
            {
            }
            try
            {
                process.StandardError.BaseStream.Dispose();
            }
            catch (IOException)
            {
            }
            await stderrPump.ConfigureAwait(false);
        }
    }

    private static async Task<string> RequiredRequest(
        JsonRpcClient client,
        string method,
        JsonObject? parameters,
        TimeSpan timeout,
        string context,
        TailBuffer stderr,
        CancellationToken cancellationToken)
    {
        try
        {
            return await Request(client, method, parameters, timeout, context, stderr, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (JsonRpcErrorException ex)
        {
            throw new ProcessException($"{context}: {ex.Message}{StderrSuffix(stderr)}", ex);
        }
    }

    private static async Task<string> Request(
        JsonRpcClient client,
        string method,
        JsonObject? parameters,
        TimeSpan timeout,
        string context,
        TailBuffer stderr,
        CancellationToken cancellationToken)
    {
        try
        {
            return await client.RequestAsync(method, parameters, timeout, cancellationToken).ConfigureAwait(false);
        }
        catch (JsonRpcErrorException)
        {
            throw;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            throw new ProcessException($"{context}: {ex.Message}{StderrSuffix(stderr)}", ex);
        }
    }

    private static string StderrSuffix(TailBuffer stderr)
    {
        var text = stderr.ToString().Trim();
        return text.Length == 0 ? "" : ": " + text;
    }

    private static void ApplyAccountUsageEnv(ProcessStartInfo startInfo, IReadOnlyDictionary<string, string> overrides)
    {
        foreach (var (key, value) in overrides)
        {
            startInfo.Environment[key] = value;
        }
        if (!overrides.TryGetValue("CODEX_HOME", out var codexHome))
        {
            codexHome = Environment.GetEnvironmentVariable("CODEX_HOME");
        }
        if (string.IsNullOrWhiteSpace(codexHome))
        {
            var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            if (home.Length > 0)
            {
                startInfo.Environment["CODEX_HOME"] = Path.Combine(home, ".codex");
            }
        }
    }

    private static (AccountRateLimits RateLimits, IReadOnlyDictionary<string, AccountRateLimits> ByLimitId)
        ParseRateLimitsResponse(string raw)
    {
        using var document = JsonDocument.Parse(raw);
        var root = document.RootElement;
        var rateLimits = root.GetObject("rateLimits") ?? root.GetObject("rate_limits");
        var byLimitId = root.GetObject("rateLimitsByLimitId") ?? root.GetObject("rate_limits_by_limit_id");

        var parsedByLimitId = new Dictionary<string, AccountRateLimits>();
        if (byLimitId is { } map)
        {
            foreach (var entry in map.EnumerateObject())
            {
                parsedByLimitId[entry.Name] = ParseRateLimits(entry.Value);
            }
        }
        return (rateLimits is { } limits ? ParseRateLimits(limits) : new AccountRateLimits(), parsedByLimitId);
    }

    private static AccountRateLimits ParseRateLimits(JsonElement element) => new()
    {
        LimitId = element.GetDualString("limitId", "limit_id"),
        LimitName = element.GetDualString("limitName", "limit_name"),
        Primary = element.GetObject("primary") is { } primary ? ParseWindow(primary) : null,
        Secondary = element.GetObject("secondary") is { } secondary ? ParseWindow(secondary) : null,
        Credits = element.GetObject("credits") is { } credits ? ParseCredits(credits) : null,
        IndividualLimit = (element.GetObject("individualLimit") ?? element.GetObject("individual_limit")) is { } limit
            ? ParseSpendLimit(limit)
            : null,
        PlanType = element.GetDualString("planType", "plan_type"),
        RateLimitReachedType = element.GetDualString("rateLimitReachedType", "rate_limit_reached_type"),
    };

    private static AccountRateLimitWindow ParseWindow(JsonElement element) => new()
    {
        UsedPercent = element.GetDualDouble("usedPercent", "used_percent"),
        WindowDurationMinutes = (int)element.GetDualLong("windowDurationMins", "window_duration_mins"),
        ResetsAt = element.GetDualLong("resetsAt", "resets_at"),
    };

    private static AccountCredits ParseCredits(JsonElement element) => new()
    {
        HasCredits = element.GetDualBool("hasCredits", "has_credits"),
        Unlimited = element.GetBool("unlimited"),
        Balance = element.GetElement("balance")?.ScalarString() ?? "",
    };

    private static AccountSpendLimit ParseSpendLimit(JsonElement element) => new()
    {
        Limit = element.GetDouble("limit"),
        Used = element.GetDouble("used"),
        RemainingPercent = element.GetDualDouble("remainingPercent", "remaining_percent"),
        ResetsAt = element.GetDualLong("resetsAt", "resets_at"),
    };

    private static AccountUsageAccount? ParseAccountResponse(string raw)
    {
        using var document = JsonDocument.Parse(raw);
        var root = document.RootElement;
        if (root.GetObject("account") is not { } account)
        {
            return null;
        }
        return new AccountUsageAccount
        {
            Type = account.GetString("type"),
            Email = account.GetString("email"),
            PlanType = account.GetDualString("planType", "plan_type"),
            RequiresOpenAIAuth = root.GetDualBool("requiresOpenaiAuth", "requires_openai_auth"),
        };
    }

    private static AccountTokenUsage ParseTokenUsage(string raw)
    {
        using var document = JsonDocument.Parse(raw);
        var root = document.RootElement;
        var summary = root.GetObject("summary");
        var buckets = new List<AccountTokenUsageDailyBucket>();
        if ((root.GetElement("dailyUsageBuckets") ?? root.GetElement("daily_usage_buckets"))
            is { ValueKind: JsonValueKind.Array } array)
        {
            foreach (var bucket in array.EnumerateArray())
            {
                buckets.Add(new AccountTokenUsageDailyBucket
                {
                    StartDate = bucket.GetDualString("startDate", "start_date"),
                    Tokens = bucket.GetElement("tokens")?.ScalarString() ?? "",
                });
            }
        }
        return new AccountTokenUsage
        {
            Summary = new AccountTokenUsageSummary
            {
                LifetimeTokens = summary?.GetDualScalarString("lifetimeTokens", "lifetime_tokens") ?? "",
                PeakDailyTokens = summary?.GetDualScalarString("peakDailyTokens", "peak_daily_tokens") ?? "",
                LongestRunningTurnSeconds =
                    summary?.GetDualScalarString("longestRunningTurnSec", "longest_running_turn_sec") ?? "",
                CurrentStreakDays = summary?.GetDualScalarString("currentStreakDays", "current_streak_days") ?? "",
                LongestStreakDays = summary?.GetDualScalarString("longestStreakDays", "longest_streak_days") ?? "",
            },
            DailyUsageBuckets = buckets,
        };
    }
}

/// <summary>A JSON-RPC error response from codex app-server.</summary>
internal sealed class JsonRpcErrorException(int code, string message)
    : Exception(message.Length > 0 ? message : $"codex app-server JSON-RPC error {code}")
{
    public int Code { get; } = code;
}

internal sealed class JsonRpcClient
{
    private readonly Stream _stdin;
    private readonly Channel<string> _lines;
    private int _nextId;

    public JsonRpcClient(Stream stdin, Stream stdout)
    {
        _stdin = stdin;
        // Buffered so trailing server chatter rarely blocks the reader once
        // the caller stops receiving.
        _lines = Channel.CreateBounded<string>(new BoundedChannelOptions(16)
        {
            FullMode = BoundedChannelFullMode.Wait,
            SingleReader = true,
            SingleWriter = true,
        });
        _ = ReadLoopAsync(stdout);
    }

    private async Task ReadLoopAsync(Stream stdout)
    {
        try
        {
            await foreach (var line in JsonlReader.ReadLinesAsync(stdout, Runner.DefaultScanMax).ConfigureAwait(false))
            {
                if (line.Trim().Length > 0)
                {
                    await _lines.Writer.WriteAsync(line).ConfigureAwait(false);
                }
            }
            _lines.Writer.TryComplete();
        }
        catch (Exception ex)
        {
            _lines.Writer.TryComplete(ex);
        }
    }

    public async Task<string> RequestAsync(
        string method,
        JsonObject? parameters,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        var id = ++_nextId;
        var payload = new JsonObject
        {
            ["id"] = id,
            ["method"] = method,
        };
        if (parameters is not null)
        {
            payload["params"] = parameters;
        }
        await WriteLineAsync(payload, cancellationToken).ConfigureAwait(false);

        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutCts.CancelAfter(timeout);
        while (true)
        {
            string line;
            try
            {
                line = await _lines.Reader.ReadAsync(timeoutCts.Token).ConfigureAwait(false);
            }
            catch (ChannelClosedException ex)
            {
                throw new ProcessException(
                    ex.InnerException is null
                        ? "codex app-server closed stdout"
                        : ex.InnerException.Message,
                    ex);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                throw new TimeoutException($"timed out after {timeout.TotalSeconds:0.###}s waiting for {method}");
            }

            using var document = JsonDocument.Parse(line.Trim());
            var root = document.RootElement;
            // Skip notifications and server-initiated requests.
            if (root.GetString("method").Length > 0)
            {
                continue;
            }
            if (root.GetElement("id") is not { ValueKind: JsonValueKind.Number } replyId ||
                replyId.GetInt32() != id)
            {
                continue;
            }
            if (root.GetElement("error") is { } error)
            {
                throw new JsonRpcErrorException(
                    (int)error.GetLong("code"),
                    error.GetString("message"));
            }
            if (root.GetElement("result") is not { } result)
            {
                throw new ProcessException("codex app-server JSON-RPC response missing result");
            }
            return result.GetRawText();
        }
    }

    public Task NotifyAsync(string method, JsonObject parameters, CancellationToken cancellationToken)
    {
        var payload = new JsonObject
        {
            ["method"] = method,
            ["params"] = parameters,
        };
        return WriteLineAsync(payload, cancellationToken);
    }

    private async Task WriteLineAsync(JsonObject payload, CancellationToken cancellationToken)
    {
        var data = Encoding.UTF8.GetBytes(payload.ToJsonString() + "\n");
        await _stdin.WriteAsync(data, cancellationToken).ConfigureAwait(false);
        await _stdin.FlushAsync(cancellationToken).ConfigureAwait(false);
    }
}
