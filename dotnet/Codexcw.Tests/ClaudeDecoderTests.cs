namespace C3OSS.Codexcw.Tests;

public sealed class ClaudeDecoderTests
{
    private static IReadOnlyList<Event> Decode(ClaudeDecoder decoder, string line, string threadId = "") =>
        decoder.Decode(line, "run-1", threadId, DateTimeOffset.Now);

    [Fact]
    public void InitEmitsThreadStartedAndTurnStarted()
    {
        var events = Decode(new ClaudeDecoder(),
            """{"type":"system","subtype":"init","cwd":"/work","session_id":"sess-1"}""");
        Assert.Equal(2, events.Count);
        Assert.Equal(EventKind.ThreadStarted, events[0].Kind);
        Assert.Equal("sess-1", events[0].ThreadStarted!.ThreadId);
        Assert.Equal("sess-1", events[0].ThreadId);
        Assert.Equal(EventKind.TurnStarted, events[1].Kind);
    }

    [Fact]
    public void NonInitSystemEventPassesThrough()
    {
        var events = Decode(new ClaudeDecoder(), """{"type":"system","subtype":"status","session_id":"s"}""");
        var @event = Assert.Single(events);
        Assert.Equal(EventKind.Other, @event.Kind);
        Assert.Equal("system", @event.Type);
    }

    [Fact]
    public void UnknownTypePassesThroughWithSessionId()
    {
        var events = Decode(new ClaudeDecoder(), """{"type":"rate_limit_event","session_id":"sess-9"}""");
        var @event = Assert.Single(events);
        Assert.Equal(EventKind.Other, @event.Kind);
        Assert.Equal("rate_limit_event", @event.Type);
        Assert.Equal("sess-9", @event.ThreadId);
    }

    [Fact]
    public void AssistantChunksHaveUniqueSyntheticIds()
    {
        var decoder = new ClaudeDecoder();
        var first = Decode(decoder,
            """{"type":"assistant","message":{"id":"msg_same","content":[{"type":"thinking","thinking":"pondering"}]},"session_id":"sess"}""");
        var second = Decode(decoder,
            """{"type":"assistant","message":{"id":"msg_same","content":[{"type":"text","text":"answer"}]},"session_id":"sess"}""");

        Assert.Equal("msg_same_0", first[0].ItemCompleted!.Item.Id);
        Assert.Equal("msg_same_1", second[0].ItemCompleted!.Item.Id);
        Assert.Equal(ItemKind.Reasoning, first[0].ItemCompleted!.Item.Kind);
        Assert.Equal(ItemKind.AgentMessage, second[0].ItemCompleted!.Item.Kind);
        Assert.Equal("pondering", first[0].ItemCompleted!.Item.Text);
    }

    [Fact]
    public void ToolKindsAreMapped()
    {
        var events = Decode(new ClaudeDecoder(),
            """{"type":"assistant","message":{"id":"m","content":[{"type":"tool_use","id":"t1","name":"Bash","input":{"command":"ls -la"}},{"type":"tool_use","id":"t2","name":"mcp__github__get_issue","input":{}},{"type":"tool_use","id":"t3","name":"WebSearch","input":{}},{"type":"tool_use","id":"t4","name":"Read","input":{}},{"type":"tool_use","id":"t5","name":"Task","input":{}},{"type":"tool_use","id":"t6","name":"TodoWrite","input":{}},{"type":"tool_use","id":"t7","name":"Edit","input":{"file_path":"/f"}}]},"session_id":"s"}""");

        var kinds = events.ToDictionary(e => e.ItemStarted!.Item.Id, e => e.ItemStarted!.Item.Kind);
        Assert.Equal(ItemKind.CommandExecution, kinds["t1"]);
        Assert.Equal(ItemKind.McpToolCall, kinds["t2"]);
        Assert.Equal(ItemKind.WebSearch, kinds["t3"]);
        Assert.Equal(ItemKind.ToolCall, kinds["t4"]);
        Assert.Equal(ItemKind.CollabToolCall, kinds["t5"]);
        Assert.Equal(ItemKind.PlanUpdate, kinds["t6"]);
        Assert.Equal(ItemKind.FileChange, kinds["t7"]);
        Assert.All(events, e => Assert.Equal("in_progress", e.ItemStarted!.Item.Status));
        Assert.Equal("ls -la", events[0].ItemStarted!.Item.Command);
    }

    [Fact]
    public void ToolResultCompletesPendingItem()
    {
        var decoder = new ClaudeDecoder();
        Decode(decoder,
            """{"type":"assistant","message":{"id":"m","content":[{"type":"tool_use","id":"t1","name":"Bash","input":{"command":"false"}}]},"session_id":"s"}""");
        var events = Decode(decoder,
            """{"type":"user","message":{"content":[{"type":"tool_result","tool_use_id":"t1","content":"Exit code 7","is_error":true}]},"session_id":"s","tool_use_result":"Error: Exit code 7"}""");

        var item = Assert.Single(events).ItemCompleted!.Item;
        Assert.Equal("failed", item.Status);
        Assert.Equal("Exit code 7", item.AggregatedOutput);
        Assert.Equal(7, item.ExitCode);
    }

    [Fact]
    public void SuccessfulCommandDefaultsExitCodeToZero()
    {
        var decoder = new ClaudeDecoder();
        Decode(decoder,
            """{"type":"assistant","message":{"id":"m","content":[{"type":"tool_use","id":"t1","name":"Bash","input":{"command":"printf ok"}}]},"session_id":"s"}""");
        var events = Decode(decoder,
            """{"type":"user","message":{"content":[{"type":"tool_result","tool_use_id":"t1","content":"ok","is_error":false}]},"session_id":"s","tool_use_result":"ok"}""");

        Assert.Equal(0, Assert.Single(events).ItemCompleted!.Item.ExitCode);
    }

    [Fact]
    public void UnknownToolResultIsPassthrough()
    {
        var events = Decode(new ClaudeDecoder(),
            """{"type":"user","message":{"content":[{"type":"tool_result","tool_use_id":"missing","content":"?"}]},"session_id":"s"}""");
        Assert.Equal(EventKind.Other, Assert.Single(events).Kind);
    }

    [Fact]
    public void FileChangeKindComesFromToolUseResult()
    {
        var decoder = new ClaudeDecoder();
        Decode(decoder,
            """{"type":"assistant","message":{"id":"m","content":[{"type":"tool_use","id":"t1","name":"Edit","input":{"file_path":"/work/hello.txt"}}]},"session_id":"s"}""");
        var events = Decode(decoder,
            """{"type":"user","message":{"content":[{"type":"tool_result","tool_use_id":"t1","content":"File created successfully"}]},"session_id":"s","tool_use_result":{"type":"create","filePath":"/work/hello.txt"}}""");

        var item = Assert.Single(events).ItemCompleted!.Item;
        var change = Assert.Single(item.Changes);
        Assert.Equal("add", change.Kind);
        Assert.Equal("/work/hello.txt", change.Path);
    }

    [Fact]
    public void ResultEmitsSyntheticMessageWhenTextIsNew()
    {
        var events = Decode(new ClaudeDecoder(),
            """{"type":"result","subtype":"success","is_error":false,"result":"{\"name\":\"Ada\"}","session_id":"s","usage":{"input_tokens":9,"output_tokens":205}}""");
        Assert.Equal(2, events.Count);
        Assert.Equal(ItemKind.AgentMessage, events[0].ItemCompleted!.Item.Kind);
        Assert.Equal("result", events[0].ItemCompleted!.Item.Id);
        Assert.Equal("""{"name":"Ada"}""", events[0].ItemCompleted!.Item.Text);
        Assert.Equal(EventKind.TurnCompleted, events[1].Kind);
    }

    [Fact]
    public void ResultSuppressesDuplicateFinalText()
    {
        var decoder = new ClaudeDecoder();
        Decode(decoder,
            """{"type":"assistant","message":{"id":"m","content":[{"type":"text","text":"Done."}]},"session_id":"s"}""");
        var events = Decode(decoder,
            """{"type":"result","subtype":"success","is_error":false,"result":"Done.","session_id":"s","usage":{"input_tokens":1,"output_tokens":1}}""");

        var @event = Assert.Single(events);
        Assert.Equal(EventKind.TurnCompleted, @event.Kind);
    }

    [Fact]
    public void ErrorResultBecomesTurnFailedWithUsage()
    {
        var events = Decode(new ClaudeDecoder(),
            """{"type":"result","subtype":"success","is_error":true,"result":"There is an issue with the selected model","session_id":"s","total_cost_usd":0.02,"usage":{"input_tokens":3,"cache_creation_input_tokens":5,"cache_read_input_tokens":7,"output_tokens":11}}""");

        var @event = Assert.Single(events);
        Assert.Equal(EventKind.TurnFailed, @event.Kind);
        Assert.Equal("There is an issue with the selected model", @event.TurnFailed!.Error.Message);
        Assert.Equal(26, @event.TurnFailed!.Usage.TotalTokens);
        Assert.Equal(0.02, @event.TurnFailed!.Usage.TotalCostUsd);
    }

    [Fact]
    public void EmptyErrorResultUsesFixedMessage()
    {
        var events = Decode(new ClaudeDecoder(),
            """{"type":"result","is_error":true,"session_id":"s"}""");
        Assert.Equal("claude run failed", Assert.Single(events).TurnFailed!.Error.Message);
    }

    [Fact]
    public void ResultUsageMapsModelUsageFromCamelCase()
    {
        var events = Decode(new ClaudeDecoder(),
            """{"type":"result","is_error":false,"result":"ok","session_id":"s","total_cost_usd":0.17833875,"usage":{"input_tokens":18,"cache_creation_input_tokens":3750,"cache_read_input_tokens":45921,"output_tokens":380},"modelUsage":{"claude-haiku-4-5-20251001":{"inputTokens":18,"outputTokens":380,"cacheReadInputTokens":45921,"cacheCreationInputTokens":3750,"webSearchRequests":2,"costUSD":0.17833875,"contextWindow":200000,"maxOutputTokens":64000}}}""");

        var usage = events[^1].TurnCompleted!.Usage;
        Assert.Equal(18, usage.InputTokens);
        Assert.Equal(45921, usage.CachedInputTokens);
        Assert.Equal(3750, usage.CacheCreationInputTokens);
        Assert.Equal(380, usage.OutputTokens);
        Assert.Equal(50069, usage.TotalTokens);
        Assert.Equal(0.17833875, usage.TotalCostUsd, 8);

        var model = usage.ModelUsage["claude-haiku-4-5-20251001"];
        Assert.Equal(18, model.InputTokens);
        Assert.Equal(380, model.OutputTokens);
        Assert.Equal(45921, model.CacheReadInputTokens);
        Assert.Equal(3750, model.CacheCreationInputTokens);
        Assert.Equal(2, model.WebSearchRequests);
        Assert.Equal(0.17833875, model.CostUsd, 8);
        Assert.Equal(200000, model.ContextWindow);
        Assert.Equal(64000, model.MaxOutputTokens);
    }

    [Theory]
    [InlineData("Error: Exit code 7", 7)]
    [InlineData("error: exit CODE -3 seen", -3)]
    [InlineData("done cleanly", null)]
    [InlineData("exit code oops", null)]
    public void ExitCodeIsScannedFromText(string text, int? expected)
    {
        Assert.Equal(expected, ClaudeDecoder.ExitCodeFromText(text));
    }

    [Fact]
    public void ExitCodeFieldBeatsTextScan()
    {
        using var content = System.Text.Json.JsonDocument.Parse("""{"exitCode":5}""");
        Assert.Equal(5, ClaudeDecoder.CommandExitCode(content.RootElement, null));

        using var snake = System.Text.Json.JsonDocument.Parse("""{"exit_code":2,"exitCode":5}""");
        Assert.Equal(2, ClaudeDecoder.CommandExitCode(snake.RootElement, null));
    }

    [Fact]
    public void MissingTypeThrows()
    {
        Assert.Throws<FormatException>(() =>
            Decode(new ClaudeDecoder(), """{"session_id":"s"}"""));
    }
}
