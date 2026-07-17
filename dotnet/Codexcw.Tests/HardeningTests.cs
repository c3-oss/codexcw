using System.Diagnostics;
using System.Text;

namespace C3OSS.Codexcw.Tests;

public sealed class UsageParityTests
{
    private static Event DecodeOne(string line) =>
        Assert.Single(new CodexDecoder().Decode(line, "run", "", DateTimeOffset.Now));

    [Fact]
    public void CodexTurnFailedCarriesUsage()
    {
        var @event = DecodeOne(
            """{"type":"turn.failed","error":{"message":"boom"},"usage":{"input_tokens":12966,"cached_input_tokens":8960,"output_tokens":6}}""");

        var payload = @event.TurnFailed!;
        Assert.Equal("boom", payload.Error.Message);
        Assert.Equal(12966, payload.Usage.InputTokens);
        Assert.Equal(8960, payload.Usage.CachedInputTokens);
        Assert.Equal(6, payload.Usage.OutputTokens);
        Assert.Equal(12972, payload.Usage.TotalTokens);
    }

    [Fact]
    public void CodexTotalTokensIsDerivedWhenOmitted()
    {
        var @event = DecodeOne(
            """{"type":"turn.completed","usage":{"input_tokens":12966,"cached_input_tokens":8960,"output_tokens":6,"reasoning_output_tokens":0}}""");

        // cached_input_tokens is a subset of input_tokens on the codex wire
        // and must not be counted twice.
        Assert.Equal(12972, @event.TurnCompleted!.Usage.TotalTokens);
    }

    [Fact]
    public void CodexExplicitTotalTokensIsPreserved()
    {
        var @event = DecodeOne(
            """{"type":"turn.completed","usage":{"input_tokens":10,"output_tokens":3,"total_tokens":999}}""");
        Assert.Equal(999, @event.TurnCompleted!.Usage.TotalTokens);
    }

    [Fact]
    public void ClaudeTotalTokensComesFromModelUsageWhenPresent()
    {
        var events = new ClaudeDecoder().Decode(
            """{"type":"result","is_error":false,"result":"ok","session_id":"s","total_cost_usd":0.5,"usage":{"input_tokens":100,"cache_creation_input_tokens":200,"cache_read_input_tokens":300,"output_tokens":50},"modelUsage":{"claude-sonnet-5":{"inputTokens":100,"outputTokens":50,"cacheReadInputTokens":300,"cacheCreationInputTokens":200},"claude-haiku-4-5-20251001":{"inputTokens":8000,"outputTokens":460,"cacheReadInputTokens":3000,"cacheCreationInputTokens":1000}}}""",
            "run", "s", DateTimeOffset.Now);

        var usage = events[^1].TurnCompleted!.Usage;
        Assert.Equal(100, usage.InputTokens);
        Assert.Equal(50, usage.OutputTokens);
        // The root result.usage sums to 650; the subagent's 12460 tokens only
        // appear in modelUsage, and the full-run total must include them.
        Assert.Equal(650 + 12460, usage.TotalTokens);
    }

    [Fact]
    public void ClaudeTotalTokensFallsBackToResultUsage()
    {
        var events = new ClaudeDecoder().Decode(
            """{"type":"result","is_error":false,"result":"ok","session_id":"s","usage":{"input_tokens":1,"cache_creation_input_tokens":2,"cache_read_input_tokens":3,"output_tokens":4}}""",
            "run", "s", DateTimeOffset.Now);
        Assert.Equal(10, events[^1].TurnCompleted!.Usage.TotalTokens);
    }

    [Fact]
    public void TurnStartedEventsCarryTheirPayload()
    {
        var codex = DecodeOne("""{"type":"turn.started"}""");
        Assert.Equal(EventKind.TurnStarted, codex.Kind);
        Assert.NotNull(codex.TurnStarted);

        var claude = new ClaudeDecoder().Decode(
            """{"type":"system","subtype":"init","session_id":"s"}""",
            "run", "", DateTimeOffset.Now);
        Assert.Equal(2, claude.Count);
        Assert.NotNull(claude[1].TurnStarted);
    }
}

public sealed class CollabItemTests
{
    [Fact]
    public void CodexCollabToolCallExposesTypedFields()
    {
        var @event = Assert.Single(new CodexDecoder().Decode(
            """{"type":"item.started","item":{"id":"i0","type":"collab_tool_call","tool":"spawn_agent","sender_thread_id":"t-parent","receiver_thread_ids":["t-child"],"agents_states":{},"status":"in_progress"}}""",
            "run", "", DateTimeOffset.Now));

        var item = @event.ItemStarted!.Item;
        Assert.Equal(ItemKind.CollabToolCall, item.Kind);
        Assert.Equal("spawn_agent", item.Tool);
        Assert.Equal("t-parent", item.SenderThreadId);
        Assert.Equal(["t-child"], item.ReceiverThreadIds);
        Assert.Contains("agents_states", item.Raw, StringComparison.Ordinal);
    }

    [Fact]
    public void ClaudeAgentToolIsACollabToolCall()
    {
        // Current Claude Code CLIs call the subagent tool "Agent"; "Task" is
        // the legacy name. This mirrors a real 2.1.x payload.
        var decoder = new ClaudeDecoder();
        var started = decoder.Decode(
            """{"type":"assistant","message":{"id":"msg_1","content":[{"type":"tool_use","id":"toolu_1","name":"Agent","input":{"subagent_type":"Explore","prompt":"reply SUBAGENT_CHILD_OK"}}]},"session_id":"s"}""",
            "run", "s", DateTimeOffset.Now);

        var startedItem = Assert.Single(started).ItemStarted!.Item;
        Assert.Equal(ItemKind.CollabToolCall, startedItem.Kind);
        Assert.Equal("Agent", startedItem.Tool);
        Assert.Equal("in_progress", startedItem.Status);

        var completed = decoder.Decode(
            """{"type":"user","message":{"content":[{"type":"tool_result","tool_use_id":"toolu_1","content":[{"type":"text","text":"SUBAGENT_CHILD_OK"}]}]},"tool_use_result":{"status":"completed","agentType":"Explore","agentId":"agent-123","resolvedModel":"claude-sonnet-5","totalTokens":12460},"session_id":"s"}""",
            "run", "s", DateTimeOffset.Now);

        var completedItem = Assert.Single(completed).ItemCompleted!.Item;
        Assert.Equal(ItemKind.CollabToolCall, completedItem.Kind);
        Assert.Equal("Agent", completedItem.Tool);
        Assert.Equal("completed", completedItem.Status);
        Assert.Equal(["agent-123"], completedItem.ReceiverThreadIds);
        Assert.Equal("SUBAGENT_CHILD_OK", completedItem.AggregatedOutput);
    }

    [Fact]
    public void ClaudeTaskToolKeepsItsToolName()
    {
        var events = new ClaudeDecoder().Decode(
            """{"type":"assistant","message":{"id":"msg_1","content":[{"type":"tool_use","id":"toolu_1","name":"Task","input":{}}]},"session_id":"s"}""",
            "run", "s", DateTimeOffset.Now);
        var item = Assert.Single(events).ItemStarted!.Item;
        Assert.Equal(ItemKind.CollabToolCall, item.Kind);
        Assert.Equal("Task", item.Tool);
    }
}

public sealed class DecoderStrictnessTests
{
    [Theory]
    [InlineData("""{"type":"thread.started","thread_id":123}""")]
    [InlineData("""{"type":"item.completed","item":{"id":"i0","type":"command_execution","exit_code":"7"}}""")]
    [InlineData("""{"type":"item.completed","item":{"id":"i0","type":"file_change","changes":{"path":"a"}}}""")]
    [InlineData("""{"type":"turn.completed","usage":{"input_tokens":"12"}}""")]
    [InlineData("""{"type":"turn.completed","usage":7}""")]
    public void CodexRejectsWrongFieldTypes(string line)
    {
        Assert.Throws<FormatException>(() =>
            new CodexDecoder().Decode(line, "run", "", DateTimeOffset.Now));
    }

    [Theory]
    [InlineData("""{"type":"result","is_error":"yes","session_id":"s"}""")]
    [InlineData("""{"type":"system","subtype":"init","session_id":42}""")]
    [InlineData("""{"type":"result","is_error":false,"result":7,"session_id":"s"}""")]
    public void ClaudeRejectsWrongFieldTypes(string line)
    {
        Assert.Throws<FormatException>(() =>
            new ClaudeDecoder().Decode(line, "run", "", DateTimeOffset.Now));
    }

    [Fact]
    public void MissingOptionalFieldsKeepDefaults()
    {
        var @event = Assert.Single(new CodexDecoder().Decode(
            """{"type":"turn.completed"}""", "run", "", DateTimeOffset.Now));
        Assert.Equal(0, @event.TurnCompleted!.Usage.TotalTokens);
    }
}

public sealed class EnumValidationTests
{
    [Fact]
    public void RunnerRejectsUnknownAgent()
    {
        Assert.Throws<InvalidRequestException>(() =>
            new Runner(new RunnerOptions { Agent = (Agent)123 }));
    }

    [Fact]
    public void CodexRejectsUnknownSandboxMode()
    {
        var runner = new Runner(new RunnerOptions { Executable = "unused" });
        Assert.Throws<InvalidRequestException>(() =>
            runner.Start(new Request { Prompt = "x", Sandbox = (SandboxMode)123 }));
    }

    [Fact]
    public void CodexRejectsUnknownApprovalPolicy()
    {
        var runner = new Runner(new RunnerOptions { Executable = "unused" });
        Assert.Throws<InvalidRequestException>(() =>
            runner.Start(new Request { Prompt = "x", Approval = (ApprovalPolicy)123 }));
    }

    [Fact]
    public void ClaudeRejectsUnknownPermissionMode()
    {
        var runner = new Runner(new RunnerOptions { Agent = Agent.Claude, Executable = "unused" });
        Assert.Throws<InvalidRequestException>(() =>
            runner.Start(new Request { Prompt = "x", PermissionMode = (PermissionMode)123 }));
    }
}

public sealed class TailBufferStressTests
{
    [Fact]
    public void KeepsTheLastBytesAcrossWraparound()
    {
        var buffer = new TailBuffer(10);
        buffer.Write(Encoding.UTF8.GetBytes("abcdefgh"));
        buffer.Write(Encoding.UTF8.GetBytes("ijk"));
        Assert.Equal("bcdefghijk", buffer.ToString());

        buffer.Write(Encoding.UTF8.GetBytes("0123456789ABCDEFGHIJ"));
        Assert.Equal("ABCDEFGHIJ", buffer.ToString());

        buffer.Write(Encoding.UTF8.GetBytes("xy"));
        Assert.Equal("CDEFGHIJxy", buffer.ToString());
    }

    [Fact]
    public void LargeStderrVolumeStaysCheap()
    {
        const int limit = 1 << 20;
        var buffer = new TailBuffer(limit);
        var chunk = new byte[8 * 1024];
        for (var i = 0; i < chunk.Length; i++)
        {
            chunk[i] = (byte)('a' + (i % 26));
        }

        var watch = Stopwatch.StartNew();
        for (var written = 0L; written < 64L << 20; written += chunk.Length)
        {
            buffer.Write(chunk);
        }
        watch.Stop();

        Assert.Equal(limit, Encoding.UTF8.GetByteCount(buffer.ToString()));
        // The previous concatenating implementation copied ~8 GiB for this
        // workload; the circular buffer copies 64 MiB and finishes fast.
        Assert.True(watch.ElapsedMilliseconds < 2000,
            $"tail buffer took {watch.ElapsedMilliseconds}ms for 64 MiB");
    }
}

public sealed class GroupCompletionTests
{
    [Fact]
    public async Task RunManyCompletesWhenPreparationThrowsNonCodexcwErrors()
    {
        var runner = new Runner(new RunnerOptions { Agent = Agent.Claude, Executable = "unused" });
        using var group = runner.RunMany(
        [
            new Request
            {
                Prompt = "test",
                OutputSchemaPath = Path.Combine(Path.GetTempPath(), "codexcw-definitely-missing", "schema.json"),
            },
        ]);

        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var error = await Assert.ThrowsAsync<GroupException>(() => group.WaitAsync(timeout.Token));

        var result = Assert.Single(error.Results);
        Assert.IsType<InvalidRequestException>(result.Error);
        Assert.Contains("read output schema", result.Error!.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task RunManySnapshotsTheRequestList()
    {
        var runner = new Runner(new RunnerOptions { Agent = Agent.Claude, Executable = "unused" });
        var missing = Path.Combine(Path.GetTempPath(), "codexcw-definitely-missing", "schema.json");
        var requests = new List<Request>
        {
            new() { Prompt = "a", OutputSchemaPath = missing },
            new() { Prompt = "b", OutputSchemaPath = missing },
        };

        using var group = runner.RunMany(requests, new GroupOptions { MaxConcurrent = 1 });
        requests.Clear();

        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var error = await Assert.ThrowsAsync<GroupException>(() => group.WaitAsync(timeout.Token));
        Assert.Equal(2, error.Results.Count);
        Assert.All(error.Results, r => Assert.NotNull(r.Error));
    }

    [Fact]
    public void StartThrowsBeforeSpawnOnACanceledToken()
    {
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        var runner = new Runner(new RunnerOptions { Executable = "codexcw-does-not-exist" });
        Assert.ThrowsAny<OperationCanceledException>(() =>
            runner.Start(new Request { Prompt = "x" }, null, cts.Token));
    }
}

public sealed class LifecycleTests : IDisposable
{
    private readonly FakeAgentDir _dir = new();

    public void Dispose() => _dir.Dispose();

    private sealed class ThrowingStream : Stream
    {
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public override int Read(byte[] buffer, int offset, int count) =>
            throw new IOException("stdin source failed");

        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) =>
            throw new IOException("stdin source failed");

        public override void Flush()
        {
        }

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }

    private sealed class BlockingStream : Stream
    {
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public override int Read(byte[] buffer, int offset, int count) =>
            ReadAsync(buffer.AsMemory(offset, count)).AsTask().GetAwaiter().GetResult();

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            return 0;
        }

        public override void Flush()
        {
        }

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }

    [UnixOnlyFact]
    public async Task CodexTurnFailedUsageReachesTheResult()
    {
        var fake = _dir.WriteFakeCodex("""
            record_args "$@"
            cat >/dev/null
            printf '%s\n' '{"type":"thread.started","thread_id":"thread-failed"}'
            printf '%s\n' '{"type":"turn.failed","error":{"message":"boom"},"usage":{"input_tokens":12966,"cached_input_tokens":8960,"output_tokens":6}}'
            """);

        var error = await Assert.ThrowsAsync<CodexErrorException>(() =>
            _dir.NewRunner(fake).RunAsync(new Request { Prompt = "x" }));

        Assert.Contains("boom", error.Message, StringComparison.Ordinal);
        Assert.NotNull(error.Result);
        Assert.Equal(12966, error.Result!.Usage.InputTokens);
        Assert.Equal(12972, error.Result!.Usage.TotalTokens);
    }

    [UnixOnlyFact]
    public async Task StdinSourceFailureFailsTheRun()
    {
        var fake = _dir.WriteFakeCodex("""
            record_args "$@"
            cat >/dev/null || true
            printf '%s\n' '{"type":"thread.started","thread_id":"thread-stdin"}'
            printf '%s\n' '{"type":"turn.completed","usage":{"input_tokens":1,"output_tokens":1}}'
            """);

        var error = await Assert.ThrowsAsync<ProcessException>(() =>
            _dir.NewRunner(fake).RunAsync(new Request { Prompt = "x", Stdin = new ThrowingStream() }));

        Assert.Contains("read request stdin", error.Message, StringComparison.Ordinal);
        Assert.NotNull(error.Result);
        Assert.Equal("thread-stdin", error.Result!.ThreadId);
    }

    [UnixOnlyFact]
    public async Task CancelReleasesABlockedStdinSource()
    {
        var fake = _dir.WriteFakeCodex("""
            record_args "$@"
            printf '%s\n' '{"type":"thread.started","thread_id":"thread-blocked"}'
            sleep 30
            """);

        var session = _dir.NewRunner(fake).Start(new Request { Prompt = "x", Stdin = new BlockingStream() });
        await foreach (var @event in session.Events())
        {
            if (@event.ThreadStarted is not null)
            {
                break;
            }
        }

        var watch = Stopwatch.StartNew();
        session.Cancel();
        await Assert.ThrowsAsync<RunCanceledException>(() => session.WaitAsync());
        await session.DisposeAsync();
        watch.Stop();

        Assert.True(watch.ElapsedMilliseconds < 5000,
            $"cancellation took {watch.ElapsedMilliseconds}ms");
    }

    [UnixOnlyFact]
    public async Task DisposeCancelsAnActiveRun()
    {
        var fake = _dir.WriteFakeCodex("""
            record_args "$@"
            cat >/dev/null
            printf '%s\n' '{"type":"thread.started","thread_id":"thread-dispose"}'
            sleep 30
            """);

        var session = _dir.NewRunner(fake).Start(new Request { Prompt = "x" });
        await foreach (var @event in session.Events())
        {
            if (@event.ThreadStarted is not null)
            {
                break;
            }
        }

        var watch = Stopwatch.StartNew();
        session.Dispose();
        await Assert.ThrowsAsync<RunCanceledException>(() => session.WaitAsync());
        watch.Stop();

        Assert.True(watch.ElapsedMilliseconds < 5000,
            $"dispose-triggered cancellation took {watch.ElapsedMilliseconds}ms");
    }

    [UnixOnlyFact]
    public async Task DisposeAsyncReleasesAForwarderBlockedOnBackpressure()
    {
        var fake = _dir.WriteFakeCodex("""
            record_args "$@"
            cat >/dev/null
            printf '%s\n' '{"type":"thread.started","thread_id":"thread-full"}'
            i=0
            while [ $i -lt 64 ]; do
              printf '%s\n' '{"type":"item.completed","item":{"id":"item_'"$i"'","type":"agent_message","text":"x"}}'
              i=$((i+1))
            done
            printf '%s\n' '{"type":"turn.completed","usage":{"input_tokens":1,"output_tokens":1}}'
            """);

        var session = _dir.NewRunner(fake, eventBuffer: 1).Start(new Request { Prompt = "x" });
        await session.WaitAsync();

        // The public channel holds one event and the forwarder is blocked on
        // the next write; DisposeAsync must release it and come back.
        var dispose = session.DisposeAsync().AsTask();
        var finished = await Task.WhenAny(dispose, Task.Delay(TimeSpan.FromSeconds(5)));
        Assert.Same(dispose, finished);
        await dispose;
    }

    [UnixOnlyFact]
    public async Task HandlerCancellationDuringRunCancelIsReportedAsCanceled()
    {
        var fake = _dir.WriteFakeCodex("""
            record_args "$@"
            cat >/dev/null
            printf '%s\n' '{"type":"thread.started","thread_id":"thread-handler"}'
            printf '%s\n' '{"type":"turn.completed","usage":{"input_tokens":1,"output_tokens":1}}'
            sleep 30
            """);

        using var cts = new CancellationTokenSource();
        var options = new RunOptions
        {
            Handler = (_, token) =>
            {
                cts.Cancel();
                token.ThrowIfCancellationRequested();
                return ValueTask.CompletedTask;
            },
        };

        await Assert.ThrowsAsync<RunCanceledException>(() =>
            _dir.NewRunner(fake).RunAsync(new Request { Prompt = "x" }, options, cts.Token));
    }
}
