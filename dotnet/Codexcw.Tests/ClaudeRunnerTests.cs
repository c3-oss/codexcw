namespace C3OSS.Codexcw.Tests;

public sealed class ClaudeRunnerTests : IDisposable
{
    private readonly FakeAgentDir _dir = new();

    public void Dispose() => _dir.Dispose();

    [UnixOnlyFact]
    public async Task RunNormalizesStreamJsonEvents()
    {
        var result = await _dir.NewRunner(Fixtures.Path("fake_claude.sh"), Agent.Claude)
            .RunAsync(new Request { Prompt = "create hello.txt", Model = ClaudeModels.Haiku });

        Assert.Equal("sess-1", result.ThreadId);
        Assert.Equal("Done.", result.FinalMessage);
        Assert.Equal(18, result.Usage.InputTokens);
        Assert.Equal(45921, result.Usage.CachedInputTokens);
        Assert.Equal(3944, result.Usage.CacheCreationInputTokens);
        Assert.Equal(380, result.Usage.OutputTokens);
        Assert.Equal(0.013562, result.Usage.TotalCostUsd, 8);
        Assert.Contains("claude-haiku-4-5-20251001", result.Usage.ModelUsage.Keys);

        var kinds = result.Events.Select(e => e.Kind).ToArray();
        Assert.Equal(
            new[]
            {
                EventKind.ThreadStarted,
                EventKind.TurnStarted,
                EventKind.ItemCompleted,
                EventKind.ItemStarted,
                EventKind.ItemCompleted,
                EventKind.ItemCompleted,
                EventKind.ItemCompleted,
                EventKind.TurnCompleted,
            },
            kinds);
        Assert.All(result.Events, e => Assert.Equal("sess-1", e.ThreadId));
        Assert.All(result.Events, e => Assert.NotEmpty(e.Raw));

        var fileChange = result.Events[4].ItemCompleted!.Item;
        Assert.Equal(ItemKind.FileChange, fileChange.Kind);
        Assert.Equal("completed", fileChange.Status);
        Assert.Equal("add", Assert.Single(fileChange.Changes).Kind);

        Assert.Equal("create hello.txt", File.ReadAllText(_dir.StdinFile));

        var args = _dir.ReadArgs();
        Assert.Equal("-p", args[0]);
        Assert.Contains("--output-format", args);
        Assert.Contains("stream-json", args);
        Assert.Contains("--verbose", args);
        Assert.Contains("--model", args);
        Assert.Contains("haiku", args);
        Assert.Contains("--no-session-persistence", args);
    }

    [UnixOnlyFact]
    public async Task ErrorResultReturnsClaudeError()
    {
        var runner = new Runner(new RunnerOptions
        {
            Agent = Agent.Claude,
            Executable = Fixtures.Path("fake_claude.sh"),
            Env = ["CODEXCW_CLAUDE_ERROR=1"],
        });

        var error = await Assert.ThrowsAsync<ClaudeErrorException>(() =>
            runner.RunAsync(new Request { Prompt = "hi" }));

        Assert.Contains("claude turn failed", error.Message, StringComparison.Ordinal);
        Assert.Contains("Claude fixture failure", error.Message, StringComparison.Ordinal);
        Assert.NotNull(error.Event.TurnFailed);
        Assert.NotNull(error.Result);
        Assert.Equal(1 + 2 + 3 + 4, error.Result!.Usage.TotalTokens);
    }

    [UnixOnlyFact]
    public async Task StructuredOutputBecomesFinalMessage()
    {
        var fake = _dir.WriteFakeCodex("""
            record_args "$@"
            cat >/dev/null
            printf '%s\n' '{"type":"system","subtype":"init","session_id":"sess-schema"}'
            printf '%s\n' '{"type":"result","subtype":"success","is_error":false,"result":"{\"name\":\"Ada\"}","session_id":"sess-schema","usage":{"input_tokens":9,"output_tokens":205}}'
            """);

        var result = await _dir.NewRunner(fake, Agent.Claude).RunAsync(new Request
        {
            Prompt = "who?",
            OutputSchema = """{"type":"object"}""",
        });

        Assert.Equal("""{"name":"Ada"}""", result.FinalMessage);
        var synthetic = result.Events[^2];
        Assert.NotNull(synthetic.ItemCompleted);
        Assert.Equal(ItemKind.AgentMessage, synthetic.ItemCompleted!.Item.Kind);

        var args = _dir.ReadArgs();
        Assert.Contains("--json-schema", args);
    }

    [UnixOnlyFact]
    public async Task RequestDirBecomesWorkingDirectory()
    {
        var workDir = Directory.CreateTempSubdirectory("codexcw-pwd-").FullName;
        var pwdFile = Path.Combine(_dir.Root, "pwd.txt");
        var fake = _dir.WriteFakeCodex("""
            cat >/dev/null
            pwd > "$CODEXCW_PWD_FILE"
            printf '%s\n' '{"type":"system","subtype":"init","session_id":"sess-dir"}'
            printf '%s\n' '{"type":"result","subtype":"success","is_error":false,"result":"ok","session_id":"sess-dir","usage":{"input_tokens":1,"output_tokens":1}}'
            """);
        var runner = new Runner(new RunnerOptions
        {
            Agent = Agent.Claude,
            Executable = fake,
            Env = [$"CODEXCW_PWD_FILE={pwdFile}"],
        });

        try
        {
            await runner.RunAsync(new Request { Prompt = "where", Dir = workDir });
            var reported = File.ReadAllText(pwdFile).Trim();
            // /tmp may itself be a symlink (macOS); the unique leaf proves cwd.
            Assert.EndsWith(Path.GetFileName(workDir), reported, StringComparison.Ordinal);
        }
        finally
        {
            Directory.Delete(workDir, recursive: true);
        }
    }

    [UnixOnlyFact]
    public async Task UsageReportPathAnswersSlashUsage()
    {
        var usage = await ClaudeAccount.GetClaudeAccountUsageAsync(new ClaudeAccountUsageRequest
        {
            Executable = Fixtures.Path("fake_claude.sh"),
        });

        Assert.Contains("Current session", usage.Report, StringComparison.Ordinal);
        Assert.Equal(2, usage.Windows.Count);
        Assert.Equal("Current session", usage.Windows[0].Label);
        Assert.Equal(13, usage.Windows[0].UsedPercent);
        Assert.StartsWith("Jul 16", usage.Windows[0].ResetsAt, StringComparison.Ordinal);
        Assert.Equal("Current week (all models)", usage.Windows[1].Label);
        Assert.Equal(5, usage.Windows[1].UsedPercent);
        Assert.NotEmpty(usage.Raw);
    }
}
