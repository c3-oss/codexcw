namespace C3OSS.Codexcw.Tests;

public sealed class BackpressureTests : IDisposable
{
    private static readonly TimeSpan WaitBudget = TimeSpan.FromSeconds(5);
    private readonly FakeAgentDir _dir = new();

    public void Dispose() => _dir.Dispose();

    private string WriteHappyPathFake() => _dir.WriteFakeCodex("""
        record_args "$@"
        cat >/dev/null
        printf '%s\n' '{"type":"thread.started","thread_id":"thread-buffer"}'
        printf '%s\n' '{"type":"turn.started"}'
        printf '%s\n' '{"type":"item.completed","item":{"id":"item_0","type":"agent_message","text":"done"}}'
        printf '%s\n' '{"type":"turn.completed","usage":{"input_tokens":1,"output_tokens":1}}'
        """);

    [UnixOnlyFact]
    public async Task SessionWaitCompletesWhenEventBufferFills()
    {
        var fake = WriteHappyPathFake();
        var session = _dir.NewRunner(fake, eventBuffer: 1).Start(new Request { Prompt = "buffer" });

        var result = await session.WaitAsync().WaitAsync(WaitBudget);

        Assert.Equal(4, result.Events.Count);
        Assert.Equal("done", result.FinalMessage);

        var streamed = new List<EventKind>();
        await foreach (var @event in session.Events())
        {
            streamed.Add(@event.Kind);
        }
        Assert.Equal(
            new[]
            {
                EventKind.ThreadStarted,
                EventKind.TurnStarted,
                EventKind.ItemCompleted,
                EventKind.TurnCompleted,
            },
            streamed);
    }

    [UnixOnlyFact]
    public async Task SessionWaitHandlesClaudeShapedEventsWithoutConsumption()
    {
        var fake = _dir.WriteFakeCodex("""
            record_args "$@"
            cat >/dev/null
            printf '%s\n' '{"type":"system","subtype":"init","session_id":"session-buffer"}'
            printf '%s\n' '{"type":"rate_limit_event","session_id":"session-buffer"}'
            printf '%s\n' '{"type":"result","subtype":"success","result":"done","session_id":"session-buffer"}'
            """);
        var session = _dir.NewRunner(fake, eventBuffer: 1).Start(new Request { Prompt = "buffer" });

        var result = await session.WaitAsync().WaitAsync(WaitBudget);

        Assert.Equal(
            new[] { "system", "rate_limit_event", "result" },
            result.Events.Select(e => e.Type).ToArray());
    }

    [UnixOnlyFact]
    public async Task GroupWaitCompletesWhenEventBufferFills()
    {
        var fake = WriteHappyPathFake();
        var group = _dir.NewRunner(fake).RunMany(
            [new Request { Prompt = "a" }, new Request { Prompt = "b" }],
            new GroupOptions { MaxConcurrent = 2, EventBuffer = 1 });

        var results = await group.WaitAsync().WaitAsync(WaitBudget);

        Assert.Equal(2, results.Count);
        Assert.All(results, r => Assert.Equal(4, r.Result!.Events.Count));

        var eventCount = 0;
        await foreach (var _ in group.Events())
        {
            eventCount++;
        }
        Assert.Equal(8, eventCount);
    }

    [UnixOnlyFact]
    public async Task GroupCancelReleasesForwardingBlockedByBackpressure()
    {
        var waitFifo = Path.Combine(_dir.Root, "wait.fifo");
        var fake = _dir.WriteFakeCodex("""
            record_args "$@"
            cat >/dev/null
            printf '%s\n' '{"type":"thread.started","thread_id":"thread-group-cancel"}'
            printf '%s\n' '{"type":"turn.started"}'
            printf '%s\n' '{"type":"item.completed","item":{"id":"item_0","type":"agent_message","text":"working"}}'
            mkfifo "$CODEXCW_WAIT_FIFO"
            read -r _ < "$CODEXCW_WAIT_FIFO"
            """);
        var runner = new Runner(new RunnerOptions
        {
            Executable = fake,
            Env = [$"CODEXCW_WAIT_FIFO={waitFifo}"],
        });
        var group = runner.RunMany(
            [new Request { Prompt = "cancel" }],
            new GroupOptions { EventBuffer = 1 });

        var enumerator = group.Events().GetAsyncEnumerator();
        Assert.True(await enumerator.MoveNextAsync().AsTask().WaitAsync(WaitBudget),
            "first event must arrive");
        await Task.Delay(100);
        group.Cancel();

        var error = await Assert.ThrowsAsync<GroupException>(() =>
            group.WaitAsync().WaitAsync(WaitBudget));
        Assert.IsType<RunCanceledException>(Assert.Single(error.Results).Error);

        var remaining = 0;
        while (await enumerator.MoveNextAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(2)))
        {
            remaining++;
        }
        await enumerator.DisposeAsync();
        Assert.True(remaining <= 1, $"cancelled forwarder delivered {remaining} queued events");
    }

    [UnixOnlyFact]
    public async Task EventStreamPreservesOrderWithSmallBuffer()
    {
        var fake = _dir.WriteFakeCodex("""
            record_args "$@"
            cat >/dev/null
            printf '%s\n' '{"type":"thread.started","thread_id":"thread-order"}'
            printf '%s\n' '{"type":"turn.started"}'
            printf '%s\n' '{"type":"item.started","item":{"id":"item_0","type":"agent_message"}}'
            printf '%s\n' '{"type":"item.completed","item":{"id":"item_0","type":"agent_message","text":"done"}}'
            printf '%s\n' '{"type":"turn.completed","usage":{"input_tokens":1,"output_tokens":1}}'
            """);
        var session = _dir.NewRunner(fake, eventBuffer: 1).Start(new Request { Prompt = "order" });

        var kinds = new List<EventKind>();
        await foreach (var @event in session.Events())
        {
            kinds.Add(@event.Kind);
        }
        await session.WaitAsync().WaitAsync(WaitBudget);

        Assert.Equal(
            new[]
            {
                EventKind.ThreadStarted,
                EventKind.TurnStarted,
                EventKind.ItemStarted,
                EventKind.ItemCompleted,
                EventKind.TurnCompleted,
            },
            kinds);
    }

    [UnixOnlyFact]
    public async Task SessionWaitHandlesLargeEventBurstWithoutConsumption()
    {
        var fake = _dir.WriteFakeCodex("""
            record_args "$@"
            cat >/dev/null
            i=0
            while [ "$i" -lt 4096 ]; do
              printf '{"type":"burst.%s"}\n' "$i"
              i=$((i + 1))
            done
            """);
        var session = _dir.NewRunner(fake, eventBuffer: 1).Start(new Request { Prompt = "burst" });

        var result = await session.WaitAsync().WaitAsync(WaitBudget);

        Assert.Equal(4096, result.Events.Count);
        Assert.Equal("burst.0", result.Events[0].Type);
        Assert.Equal("burst.4095", result.Events[4095].Type);
    }
}
