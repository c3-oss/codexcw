namespace C3OSS.Codexcw.Tests;

public sealed class GroupTests : IDisposable
{
    private readonly FakeAgentDir _dir = new();

    public void Dispose() => _dir.Dispose();

    [UnixOnlyFact]
    public async Task RunManyMultiplexesEventsAndResults()
    {
        var fake = _dir.WriteFakeCodex("""
            record_args "$@"
            cat >/dev/null
            printf '%s\n' '{"type":"thread.started","thread_id":"thread-many"}'
            printf '%s\n' '{"type":"item.completed","item":{"id":"item_0","type":"agent_message","text":"done"}}'
            printf '%s\n' '{"type":"turn.completed","usage":{"input_tokens":1,"output_tokens":1}}'
            """);

        var group = _dir.NewRunner(fake).RunMany(
            [new Request { Prompt = "a" }, new Request { Prompt = "b" }, new Request { Prompt = "c" }],
            new GroupOptions { MaxConcurrent = 2 });

        var events = new List<RunEvent>();
        await foreach (var @event in group.Events())
        {
            events.Add(@event);
        }

        var results = await group.WaitAsync();
        Assert.Equal(3, results.Count);
        Assert.NotEmpty(events);
        Assert.Equal(9, events.Count);
        Assert.Equal([0, 1, 2], results.Select(r => r.Index).Order().ToArray());
        foreach (var result in results)
        {
            Assert.Null(result.Error);
            Assert.NotNull(result.Result);
            Assert.NotEmpty(result.RunId);
            Assert.Equal("done", result.Result!.FinalMessage);
        }

        var indexes = events.Select(e => e.Index).Distinct().Order().ToArray();
        Assert.Equal([0, 1, 2], indexes);
    }

    [UnixOnlyFact]
    public async Task RunManyThrowsGroupExceptionWithAllResults()
    {
        var fake = _dir.WriteFakeCodex("""
            record_args "$@"
            cat >/dev/null
            printf '%s\n' '{"type":"thread.started","thread_id":"thread-ok"}'
            printf '%s\n' '{"type":"turn.completed","usage":{"input_tokens":1,"output_tokens":1}}'
            """);

        var group = _dir.NewRunner(fake).RunMany([new Request { Prompt = "ok" }, new Request()]);
        await foreach (var _ in group.Events())
        {
        }

        var error = await Assert.ThrowsAsync<GroupException>(() => group.WaitAsync());
        Assert.Equal(2, error.Results.Count);
        Assert.Contains("1 agent run(s) failed", error.Message, StringComparison.Ordinal);
        Assert.Null(error.Results[0].Error);
        Assert.IsType<PromptRequiredException>(error.Results[1].Error);
    }

    [UnixOnlyFact]
    public async Task RunManyBoundsConcurrency()
    {
        var fake = _dir.WriteFakeCodex("""
            cat >/dev/null
            sleep 0.3
            printf '%s\n' '{"type":"thread.started","thread_id":"t"}'
            printf '%s\n' '{"type":"turn.completed","usage":{"input_tokens":1,"output_tokens":1}}'
            """);

        var startedAt = System.Diagnostics.Stopwatch.StartNew();
        var group = _dir.NewRunner(fake).RunMany(
            [new Request { Prompt = "a" }, new Request { Prompt = "b" }, new Request { Prompt = "c" }, new Request { Prompt = "d" }],
            new GroupOptions { MaxConcurrent = 2 });
        var results = await group.WaitAsync();
        startedAt.Stop();

        Assert.Equal(4, results.Count);
        Assert.All(results, r => Assert.Null(r.Error));
        // 4 runs of >=300ms at concurrency 2 need at least two waves.
        Assert.True(startedAt.ElapsedMilliseconds >= 550,
            $"batch finished in {startedAt.ElapsedMilliseconds}ms; concurrency was not bounded");
    }
}
