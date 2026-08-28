using System.Text;

namespace C3OSS.Codexcw.Tests;

public sealed class GrokRunnerTests : IDisposable
{
    private readonly FakeAgentDir _dir = new();

    public void Dispose() => _dir.Dispose();

    [UnixOnlyFact]
    public async Task RunNormalizesStreamingMessagesAndUsesPromptFile()
    {
        await using var stdin = new MemoryStream(Encoding.UTF8.GetBytes("extra context"));
        var result = await _dir.NewRunner(Fixtures.Path("fake_grok.sh"), Agent.Grok)
            .RunAsync(new Request
            {
                Prompt = "inspect",
                Stdin = stdin,
                AllowedTools = ["Bash(git *)"],
            });

        Assert.Equal("grok-session", result.ThreadId);
        Assert.Equal("Done.", result.FinalMessage);
        Assert.Equal(11, result.Usage.InputTokens);
        Assert.Equal(5, result.Usage.CachedInputTokens);
        Assert.Equal(7, result.Usage.OutputTokens);
        Assert.Equal(23, result.Usage.TotalTokens);
        Assert.Equal(0, result.Usage.ReasoningOutputTokens);
        Assert.Equal(0.01, result.Usage.TotalCostUsd, 8);
        Assert.All(result.Events, @event => Assert.NotEmpty(@event.Raw));

        var command = result.Events
            .Where(static @event => @event.ItemCompleted?.Item.Kind == ItemKind.CommandExecution)
            .Select(static @event => @event.ItemCompleted!.Item)
            .Single();
        Assert.Equal("printf hi", command.Command);
        Assert.Equal("hi\n", command.AggregatedOutput);
        Assert.Equal(0, command.ExitCode);

        var file = result.Events
            .Where(static @event => @event.ItemCompleted?.Item.Kind == ItemKind.FileChange)
            .Select(static @event => @event.ItemCompleted!.Item)
            .Single();
        var change = Assert.Single(file.Changes);
        Assert.Equal("src/Program.cs", change.Path);
        Assert.Equal("update", change.Kind);

        Assert.Equal(
            "inspect\n\n<stdin>\nextra context\n</stdin>\n",
            File.ReadAllText(_dir.StdinFile));
        var args = _dir.ReadArgs();
        foreach (var expected in new[]
        {
            "--no-auto-update",
            "--output-format", "streaming-messages-json",
            "--verbatim",
            "--prompt-file",
            "--sandbox", "read-only",
            "--permission-mode", "dontAsk",
            "--allow", "Bash(git *)",
        })
        {
            Assert.Contains(expected, args);
        }
        var promptIndex = Array.IndexOf(args, "--prompt-file");
        Assert.NotEqual(-1, promptIndex);
        Assert.False(File.Exists(args[promptIndex + 1]));
    }

    [UnixOnlyFact]
    public async Task ErrorResultReturnsGrokError()
    {
        var runner = new Runner(new RunnerOptions
        {
            Agent = Agent.Grok,
            Executable = Fixtures.Path("fake_grok.sh"),
            Env = ["CODEXCW_GROK_ERROR=1"],
        });

        var error = await Assert.ThrowsAsync<GrokErrorException>(() =>
            runner.RunAsync(new Request { Prompt = "fail" }));

        Assert.Contains("maximum number of turns", error.Message, StringComparison.Ordinal);
        Assert.NotNull(error.Event.TurnFailed);
        Assert.NotNull(error.Result);
    }

    [GatedFact("CODEXCW_LIVE_GROK")]
    public async Task LiveRun()
    {
        var result = await new Runner(new RunnerOptions { Agent = Agent.Grok })
            .RunAsync(new Request { Prompt = "Reply with exactly: CODEXCW_GROK_OK" });

        Assert.Equal("CODEXCW_GROK_OK", result.FinalMessage.Trim());
        Assert.NotEmpty(result.ThreadId);
    }
}

public sealed class GrokArgsTests
{
    private static PreparedRun Prepare(Request request) =>
        GrokArgs.Prepare(request, SandboxMode.ReadOnly, ApprovalPolicy.Never);

    [Fact]
    public async Task AdvancedArgsAndBufferedPrompt()
    {
        await using var stdin = new MemoryStream(Encoding.UTF8.GetBytes("extra"));
        var prepared = Prepare(new Request
        {
            Prompt = "prompt",
            Stdin = stdin,
            Dir = "/work",
            Model = "grok-code-fast-1",
            Profile = "reviewer",
            Sandbox = SandboxMode.WorkspaceWrite,
            PermissionMode = PermissionMode.AcceptEdits,
            AllowedTools = ["Bash(git *)"],
            DisallowedTools = ["WebSearch"],
            OutputSchema = """{"type":"object"}""",
            ResumeId = "session-9",
        });
        try
        {
            Assert.False(prepared.WritePrompt);
            Assert.NotNull(prepared.PromptTempPath);
            Assert.Equal(
                "prompt\n\n<stdin>\nextra\n</stdin>\n",
                File.ReadAllText(prepared.PromptTempPath));

            foreach (var expected in new[]
            {
                "--cwd", "/work",
                "--model", "grok-code-fast-1",
                "--agent", "reviewer",
                "--sandbox", "workspace",
                "--permission-mode", "acceptEdits",
                "--allow", "Bash(git *)",
                "--deny", "WebSearch",
                "--json-schema", """{"type":"object"}""",
                "--resume", "session-9",
            })
            {
                Assert.Contains(expected, prepared.Args);
            }
        }
        finally
        {
            File.Delete(prepared.PromptTempPath!);
        }
    }

    [Fact]
    public void ResumeUsesSavedSandboxUnlessExplicit()
    {
        var prepared = Prepare(new Request { Prompt = "continue", ResumeLast = true });
        try
        {
            Assert.DoesNotContain("--sandbox", prepared.Args);
            Assert.Contains("--continue", prepared.Args);
        }
        finally
        {
            File.Delete(prepared.PromptTempPath!);
        }
    }

    [Fact]
    public void ValidationRejectsUnsupportedAndConflictingFields()
    {
        Assert.Throws<PromptRequiredException>(() => Prepare(new Request()));
        Assert.Throws<InvalidRequestException>(() => Prepare(new Request
        {
            Prompt = "x",
            AddDirs = ["/other"],
        }));
        Assert.Throws<InvalidRequestException>(() => Prepare(new Request
        {
            Prompt = "x",
            Approval = ApprovalPolicy.Never,
            PermissionMode = PermissionMode.DontAsk,
        }));
        Assert.Throws<InvalidRequestException>(() => Prepare(new Request
        {
            Prompt = "x",
            ResumeId = "id",
            ResumeLast = true,
        }));
    }
}

public sealed class GrokDecoderTests
{
    [Fact]
    public void ToolKindsAndNestedResultsAreMapped()
    {
        var decoder = new ClaudeDecoder(Agent.Grok);
        var started = decoder.Decode(
            """{"type":"assistant","message":{"id":"m","content":[{"type":"tool_use","id":"cmd","name":"run_terminal_command","input":{"command":"true"}},{"type":"tool_use","id":"edit","name":"search_replace","input":{"target_file":"a.cs"}},{"type":"tool_use","id":"sub","name":"spawn_subagent","input":{}},{"type":"tool_use","id":"todo","name":"todo_write","input":{}},{"type":"tool_use","id":"mcp","name":"use_tool","input":{}},{"type":"server_tool_use","id":"web","name":"web_search","input":{}}]}}""",
            "run",
            "session",
            DateTimeOffset.Now);

        Assert.Equal(ItemKind.CommandExecution, started[0].ItemStarted!.Item.Kind);
        Assert.Equal(ItemKind.FileChange, started[1].ItemStarted!.Item.Kind);
        Assert.Equal(ItemKind.CollabToolCall, started[2].ItemStarted!.Item.Kind);
        Assert.Equal(ItemKind.PlanUpdate, started[3].ItemStarted!.Item.Kind);
        Assert.Equal(ItemKind.McpToolCall, started[4].ItemStarted!.Item.Kind);
        Assert.Equal(ItemKind.WebSearch, started[5].ItemStarted!.Item.Kind);

        var completed = decoder.Decode(
            """{"type":"user","message":{"content":[{"type":"tool_result","tool_use_id":"cmd","content":"{\"output\":[104,105,10],\"exit_code\":0}"},{"type":"tool_result","tool_use_id":"sub","content":"{\"task_id\":\"agent-7\"}"}]}}""",
            "run",
            "session",
            DateTimeOffset.Now);
        Assert.Equal("hi\n", completed[0].ItemCompleted!.Item.AggregatedOutput);
        Assert.Equal(0, completed[0].ItemCompleted!.Item.ExitCode);
        Assert.Equal(
            "agent-7",
            Assert.Single(completed[1].ItemCompleted!.Item.ReceiverThreadIds));
    }

    [Fact]
    public void ErrorArrayBecomesTurnFailureMessage()
    {
        var @event = Assert.Single(new ClaudeDecoder(Agent.Grok).Decode(
            """{"type":"result","is_error":true,"errors":["Reached the maximum number of turns"]}""",
            "run",
            "session",
            DateTimeOffset.Now));

        Assert.Equal("Reached the maximum number of turns", @event.TurnFailed!.Error.Message);
    }
}
