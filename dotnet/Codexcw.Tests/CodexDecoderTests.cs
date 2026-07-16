namespace C3OSS.Codexcw.Tests;

public sealed class CodexDecoderTests
{
    private static Event DecodeOne(string line, string threadId = "")
    {
        var events = new CodexDecoder().Decode(line, "run-1", threadId, DateTimeOffset.Now);
        Assert.Single(events);
        return events[0];
    }

    [Fact]
    public void DecodesThreadStarted()
    {
        var @event = DecodeOne("""{"type":"thread.started","thread_id":"thread-1"}""");
        Assert.Equal(EventKind.ThreadStarted, @event.Kind);
        Assert.Equal("thread-1", @event.ThreadId);
        Assert.Equal("thread-1", @event.ThreadStarted!.ThreadId);
        Assert.Contains("thread.started", @event.Raw, StringComparison.Ordinal);
    }

    [Fact]
    public void DecodesTurnCompletedUsage()
    {
        var @event = DecodeOne(
            """{"type":"turn.completed","usage":{"input_tokens":10,"cached_input_tokens":2,"output_tokens":3,"reasoning_output_tokens":1,"total_tokens":16}}""");
        var usage = @event.TurnCompleted!.Usage;
        Assert.Equal(10, usage.InputTokens);
        Assert.Equal(2, usage.CachedInputTokens);
        Assert.Equal(3, usage.OutputTokens);
        Assert.Equal(1, usage.ReasoningOutputTokens);
        Assert.Equal(16, usage.TotalTokens);
    }

    [Fact]
    public void DecodesItemCompletedWithCommandDetails()
    {
        var @event = DecodeOne(
            """{"type":"item.completed","item":{"id":"item_0","type":"command_execution","command":"false","aggregated_output":"boom\n","exit_code":7,"status":"failed"}}""");
        var item = @event.ItemCompleted!.Item;
        Assert.Equal(ItemKind.CommandExecution, item.Kind);
        Assert.Equal("false", item.Command);
        Assert.Equal("boom\n", item.AggregatedOutput);
        Assert.Equal(7, item.ExitCode);
        Assert.Equal("failed", item.Status);
        Assert.Contains("command_execution", item.Raw, StringComparison.Ordinal);
    }

    [Fact]
    public void DecodesFileChanges()
    {
        var @event = DecodeOne(
            """{"type":"item.completed","item":{"id":"i","type":"file_change","status":"completed","changes":[{"path":"/work/a.txt","kind":"add"}]}}""");
        var item = @event.ItemCompleted!.Item;
        Assert.Equal(ItemKind.FileChange, item.Kind);
        var change = Assert.Single(item.Changes);
        Assert.Equal("/work/a.txt", change.Path);
        Assert.Equal("add", change.Kind);
    }

    [Fact]
    public void MissingTypeThrows()
    {
        var ex = Assert.Throws<FormatException>(() => DecodeOne("""{"thread_id":"x"}"""));
        Assert.Equal("missing event type", ex.Message);
    }

    [Fact]
    public void MissingItemPayloadThrows()
    {
        var ex = Assert.Throws<FormatException>(() => DecodeOne("""{"type":"item.completed"}"""));
        Assert.Equal("missing item payload", ex.Message);
    }

    [Fact]
    public void UnknownEventTypePassesThrough()
    {
        var @event = DecodeOne("""{"type":"burst.7"}""", threadId: "thread-x");
        Assert.Equal(EventKind.Other, @event.Kind);
        Assert.Equal("burst.7", @event.Type);
        Assert.Equal("thread-x", @event.ThreadId);
        Assert.Null(@event.Error);
        Assert.Null(@event.TurnFailed);
    }

    [Fact]
    public void UnknownItemTypePassesThrough()
    {
        var @event = DecodeOne("""{"type":"item.completed","item":{"id":"i","type":"novel_kind"}}""");
        var item = @event.ItemCompleted!.Item;
        Assert.Equal(ItemKind.Other, item.Kind);
        Assert.Equal("novel_kind", item.Type);
    }

    [Fact]
    public void TurnFailedMessageFallsBackToRawError()
    {
        var withMessage = DecodeOne("""{"type":"turn.failed","error":{"message":"turn broke"}}""");
        Assert.Equal("turn broke", withMessage.TurnFailed!.Error.Message);

        var withoutMessage = DecodeOne("""{"type":"turn.failed","error":"total mystery"}""");
        Assert.Equal("\"total mystery\"", withoutMessage.TurnFailed!.Error.Message);
    }

    [Fact]
    public void TopLevelErrorFallsBackToRawError()
    {
        var withMessage = DecodeOne("""{"type":"error","message":"invalid_json_schema: bad model"}""");
        Assert.Equal("invalid_json_schema: bad model", withMessage.Error!.Message);

        var withoutMessage = DecodeOne("""{"type":"error","error":{"code":3}}""");
        Assert.Equal("""{"code":3}""", withoutMessage.Error!.Message);
    }

    [Fact]
    public void MalformedJsonThrows()
    {
        Assert.ThrowsAny<System.Text.Json.JsonException>(() => DecodeOne("not-json"));
    }
}
