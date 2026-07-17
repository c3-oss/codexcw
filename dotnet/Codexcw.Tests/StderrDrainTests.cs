using System.Diagnostics;

namespace C3OSS.Codexcw.Tests;

public sealed class StderrDrainTests : IDisposable
{
    private readonly FakeAgentDir _dir = new();

    public void Dispose() => _dir.Dispose();

    [UnixOnlyFact]
    public async Task FastExitStderrIsReliable()
    {
        var fake = _dir.WriteFakeCodex("""
            record_args "$@"
            cat >/dev/null
            printf '%s\n' 'fast stderr detail' >&2
            exit 1
            """);
        var runner = _dir.NewRunner(fake);

        for (var iteration = 0; iteration < 100; iteration++)
        {
            var error = await Assert.ThrowsAsync<ExitException>(() =>
                runner.RunAsync(new Request { Prompt = "fail" }));
            Assert.Contains("fast stderr detail", error.Stderr, StringComparison.Ordinal);
            Assert.Contains("fast stderr detail", error.Result!.Stderr, StringComparison.Ordinal);
        }
    }

    [UnixOnlyFact]
    public async Task DescendantHoldingStderrIsBounded()
    {
        var fake = _dir.WriteFakeCodex("""
            record_args "$@"
            cat >/dev/null
            sleep 3 >/dev/null &
            printf '%s\n' 'parent stderr detail' >&2
            printf '%s\n' '{"type":"thread.started","thread_id":"thread-descendant"}'
            printf '%s\n' '{"type":"turn.completed","usage":{"input_tokens":1,"output_tokens":1}}'
            """);

        var error = await Assert.ThrowsAsync<ProcessException>(() =>
            _dir.NewRunner(fake).RunAsync(new Request { Prompt = "ok" }));
        Assert.Contains("wait delay", error.Message, StringComparison.Ordinal);
        Assert.NotNull(error.Result);
        Assert.Contains("parent stderr detail", error.Result!.Stderr, StringComparison.Ordinal);
    }

    [UnixOnlyFact]
    public async Task CancellationCapturesStderr()
    {
        var fake = _dir.WriteFakeCodex("""
            record_args "$@"
            cat >/dev/null
            printf '%s\n' 'cancelled stderr detail' >&2
            printf '%s\n' '{"type":"thread.started","thread_id":"thread-cancel"}'
            while :; do :; done
            """);

        var session = _dir.NewRunner(fake).Start(new Request { Prompt = "cancel" });
        await foreach (var @event in session.Events())
        {
            Assert.Equal(EventKind.ThreadStarted, @event.Kind);
            break;
        }
        session.Cancel();

        var error = await Assert.ThrowsAsync<RunCanceledException>(() => session.WaitAsync());
        Assert.NotNull(error.Result);
        Assert.Contains("cancelled stderr detail", error.Result!.Stderr, StringComparison.Ordinal);
    }

    [UnixOnlyFact]
    public async Task CancellationWithDescendantHoldingStderrIsBounded()
    {
        var fake = _dir.WriteFakeCodex("""
            record_args "$@"
            cat >/dev/null
            sleep 3 >/dev/null &
            printf '%s\n' 'cancelled descendant stderr detail' >&2
            printf '%s\n' '{"type":"thread.started","thread_id":"thread-cancel-descendant"}'
            while :; do :; done
            """);

        var session = _dir.NewRunner(fake).Start(new Request { Prompt = "cancel" });
        await foreach (var @event in session.Events())
        {
            Assert.Equal(EventKind.ThreadStarted, @event.Kind);
            break;
        }

        var stopwatch = Stopwatch.StartNew();
        session.Cancel();
        var error = await Assert.ThrowsAsync<RunCanceledException>(() => session.WaitAsync());
        stopwatch.Stop();

        Assert.True(stopwatch.ElapsedMilliseconds < 2500,
            $"cancellation took {stopwatch.ElapsedMilliseconds}ms");
        Assert.Contains("cancelled descendant stderr detail", error.Result!.Stderr, StringComparison.Ordinal);
    }
}
