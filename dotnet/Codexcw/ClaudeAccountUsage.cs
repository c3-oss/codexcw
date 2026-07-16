using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace C3OSS.Codexcw;

/// <summary>Configures one Claude account usage lookup.</summary>
public sealed record ClaudeAccountUsageRequest
{
    /// <summary>The claude executable path. Defaults to "claude".</summary>
    public string? Executable { get; init; }

    /// <summary>Environment variables appended for the Claude child process.</summary>
    public IReadOnlyDictionary<string, string> Env { get; init; } =
        new Dictionary<string, string>();

    /// <summary>Bounds the lookup. Non-positive values use the 10s default.</summary>
    public TimeSpan Timeout { get; init; }
}

/// <summary>The usage report returned by Claude Code's /usage command.</summary>
public sealed record ClaudeAccountUsage
{
    /// <summary>Claude Code's human-readable usage report.</summary>
    public string Report { get; init; } = "";

    /// <summary>Percentage and reset details parsed from the report.</summary>
    public IReadOnlyList<ClaudeAccountUsageWindow> Windows { get; init; } = [];

    /// <summary>The complete JSON result emitted by Claude Code.</summary>
    public string Raw { get; init; } = "";
}

/// <summary>One window from Claude Code's /usage report.</summary>
/// <param name="Label">The display label reported by Claude Code.</param>
/// <param name="UsedPercent">The percentage consumed in this window.</param>
/// <param name="ResetsAt">Claude Code's human-readable reset time.</param>
public sealed record ClaudeAccountUsageWindow(string Label, double UsedPercent, string ResetsAt);

/// <summary>Claude account usage lookups through Claude Code's /usage command.</summary>
public static partial class ClaudeAccount
{
    private static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(10);

    [GeneratedRegex(@"^([^:\n]+):\s*([0-9]+(?:\.[0-9]+)?)% used(?:\s*·\s*resets\s+(.+))?$", RegexOptions.Multiline)]
    private static partial Regex UsageWindowPattern();

    /// <summary>Reads account usage through Claude Code's /usage command.</summary>
    public static async Task<ClaudeAccountUsage> GetClaudeAccountUsageAsync(
        ClaudeAccountUsageRequest? request = null,
        CancellationToken cancellationToken = default)
    {
        request ??= new ClaudeAccountUsageRequest();
        var executable = (request.Executable ?? "").Trim();
        if (executable.Length == 0)
        {
            executable = Agent.Claude.Name();
        }
        var timeout = request.Timeout > TimeSpan.Zero ? request.Timeout : DefaultTimeout;

        var startInfo = new ProcessStartInfo
        {
            FileName = executable,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        foreach (var arg in new[] { "-p", "--output-format", "json", "--no-session-persistence" })
        {
            startInfo.ArgumentList.Add(arg);
        }
        foreach (var (key, value) in request.Env)
        {
            startInfo.Environment[key] = value;
        }

        using var process = new Process { StartInfo = startInfo };
        try
        {
            process.Start();
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            throw new ProcessException($"read claude account usage: {ex.Message}", ex);
        }

        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutCts.CancelAfter(timeout);
        using var killRegistration = timeoutCts.Token.Register(() => Session.KillProcessTree(process));

        var stderrTail = new TailBuffer(Runner.DefaultStderrLimit);
        var stderrPump = stderrTail.PumpAsync(process.StandardError.BaseStream, CancellationToken.None);
        var stdoutTask = ReadAllAsync(process.StandardOutput.BaseStream);

        var stdin = process.StandardInput.BaseStream;
        try
        {
            await stdin.WriteAsync(Encoding.UTF8.GetBytes("/usage"), CancellationToken.None).ConfigureAwait(false);
        }
        catch (IOException)
        {
        }
        finally
        {
            try
            {
                stdin.Close();
            }
            catch (IOException)
            {
            }
        }

        await process.WaitForExitAsync(CancellationToken.None).ConfigureAwait(false);
        var pumps = Task.WhenAll(stdoutTask, stderrPump);
        if (await Task.WhenAny(pumps, Task.Delay(TimeSpan.FromSeconds(1), CancellationToken.None))
                .ConfigureAwait(false) != pumps)
        {
            try
            {
                process.StandardOutput.BaseStream.Dispose();
                process.StandardError.BaseStream.Dispose();
            }
            catch (IOException)
            {
            }
        }
        var raw = (await stdoutTask.ConfigureAwait(false)).Trim();
        await stderrPump.ConfigureAwait(false);

        cancellationToken.ThrowIfCancellationRequested();
        if (timeoutCts.IsCancellationRequested)
        {
            throw new ProcessException(
                $"read claude account usage: timed out after {timeout.TotalSeconds:0.###}s");
        }
        if (process.ExitCode != 0)
        {
            throw new ExitException(process.ExitCode, stderrTail.ToString());
        }

        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(raw);
        }
        catch (JsonException ex)
        {
            throw new ProcessException($"decode claude account usage: {ex.Message}", ex);
        }
        using (document)
        {
            var root = document.RootElement;
            if (root.GetBool("is_error"))
            {
                var message = root.GetString("result").Trim();
                if (message.Length == 0 &&
                    root.GetElement("errors") is { ValueKind: JsonValueKind.Array } errors)
                {
                    message = string.Join("; ", errors.EnumerateArray()
                        .Where(static e => e.ValueKind == JsonValueKind.String)
                        .Select(static e => e.GetString()));
                }
                if (message.Length == 0)
                {
                    message = "unknown error";
                }
                throw new ProcessException($"claude account usage failed: {message}");
            }

            var report = root.GetString("result");
            return new ClaudeAccountUsage
            {
                Report = report,
                Windows = ParseUsageWindows(report),
                Raw = raw,
            };
        }
    }

    internal static IReadOnlyList<ClaudeAccountUsageWindow> ParseUsageWindows(string report)
    {
        var windows = new List<ClaudeAccountUsageWindow>();
        foreach (Match match in UsageWindowPattern().Matches(report))
        {
            if (!double.TryParse(match.Groups[2].Value, NumberStyles.Float, CultureInfo.InvariantCulture,
                    out var usedPercent))
            {
                continue;
            }
            windows.Add(new ClaudeAccountUsageWindow(
                match.Groups[1].Value.Trim(),
                usedPercent,
                match.Groups[3].Value.Trim()));
        }
        return windows;
    }

    private static async Task<string> ReadAllAsync(Stream stream)
    {
        using var buffer = new MemoryStream();
        try
        {
            await stream.CopyToAsync(buffer, CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException)
        {
            // The pipe was force-closed after the wait delay; keep what we have.
        }
        return Encoding.UTF8.GetString(buffer.GetBuffer().AsSpan(0, (int)buffer.Length));
    }
}
