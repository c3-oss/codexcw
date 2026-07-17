namespace C3OSS.Codexcw.Tests;

public sealed class RunnerTests : IDisposable
{
    private readonly FakeAgentDir _dir = new();

    public void Dispose() => _dir.Dispose();

    [UnixOnlyFact]
    public async Task RunDecodesEventsAndUsesSafeDefaults()
    {
        var fake = _dir.WriteFakeCodex("""
            record_args "$@"
            cat > "$CODEXCW_STDIN_FILE"
            printf '%s\n' '{"type":"thread.started","thread_id":"thread-1"}'
            printf '%s\n' '{"type":"turn.started"}'
            printf '%s\n' '{"type":"item.completed","item":{"id":"item_0","type":"agent_message","text":"Oi."}}'
            printf '%s\n' '{"type":"turn.completed","usage":{"input_tokens":10,"cached_input_tokens":2,"output_tokens":3,"reasoning_output_tokens":1}}'
            """);

        var result = await _dir.NewRunner(fake).RunAsync(new Request { Prompt = "diga oi" });

        Assert.Equal("thread-1", result.ThreadId);
        Assert.Equal("Oi.", result.FinalMessage);
        Assert.Equal(10, result.Usage.InputTokens);
        Assert.Equal(4, result.Events.Count);
        Assert.All(result.Events, e => Assert.Equal("thread-1", e.ThreadId));

        Assert.Equal("diga oi", File.ReadAllText(_dir.StdinFile));

        var args = _dir.ReadArgs();
        Assert.Contains("exec", args);
        Assert.Contains("--json", args);
        Assert.Contains("--color", args);
        Assert.Contains("never", args);
        Assert.Contains("--skip-git-repo-check", args);
        Assert.Contains("--ephemeral", args);
        Assert.Contains("--sandbox", args);
        Assert.Contains("read-only", args);
        Assert.Contains("approval_policy=\"never\"", args);
        Assert.Equal("-", args[^1]);
    }

    [UnixOnlyFact]
    public async Task CommandExecutionFailureIsAnEventNotRunError()
    {
        var fake = _dir.WriteFakeCodex("""
            record_args "$@"
            cat >/dev/null
            printf '%s\n' '{"type":"thread.started","thread_id":"thread-2"}'
            printf '%s\n' '{"type":"turn.started"}'
            printf '%s\n' '{"type":"item.completed","item":{"id":"item_0","type":"command_execution","command":"false","aggregated_output":"boom\n","exit_code":7,"status":"failed"}}'
            printf '%s\n' '{"type":"item.completed","item":{"id":"item_1","type":"agent_message","text":"Exit 7"}}'
            printf '%s\n' '{"type":"turn.completed","usage":{"input_tokens":1,"output_tokens":1}}'
            """);

        var result = await _dir.NewRunner(fake).RunAsync(new Request { Prompt = "run false" });

        Assert.Equal(5, result.Events.Count);
        var item = result.Events[2].ItemCompleted!.Item;
        Assert.Equal(7, item.ExitCode);
        Assert.Equal("failed", item.Status);
        Assert.Equal("Exit 7", result.FinalMessage);
    }

    [UnixOnlyFact]
    public async Task CollabToolCallItemIsTyped()
    {
        var fake = _dir.WriteFakeCodex("""
            record_args "$@"
            cat >/dev/null
            printf '%s\n' '{"type":"thread.started","thread_id":"thread-3"}'
            printf '%s\n' '{"type":"turn.started"}'
            printf '%s\n' '{"type":"item.started","item":{"id":"item_0","type":"collab_tool_call","tool":"wait","status":"in_progress"}}'
            printf '%s\n' '{"type":"item.completed","item":{"id":"item_0","type":"collab_tool_call","tool":"wait","status":"completed"}}'
            printf '%s\n' '{"type":"item.completed","item":{"id":"item_1","type":"agent_message","text":"red, green, blue"}}'
            printf '%s\n' '{"type":"turn.completed","usage":{"input_tokens":1,"output_tokens":1}}'
            """);

        var result = await _dir.NewRunner(fake).RunAsync(new Request { Prompt = "spawn agents" });

        Assert.Equal(6, result.Events.Count);
        var started = result.Events[2].ItemStarted!.Item;
        Assert.Equal(ItemKind.CollabToolCall, started.Kind);
        Assert.Equal("in_progress", started.Status);
        Assert.Contains("\"tool\":\"wait\"", started.Raw, StringComparison.Ordinal);
        Assert.Equal("red, green, blue", result.FinalMessage);
    }

    [UnixOnlyFact]
    public async Task ProcessExitErrorCarriesStderrAndLastEvent()
    {
        var fake = _dir.WriteFakeCodex("""
            record_args "$@"
            cat >/dev/null
            printf '%s\n' '{"type":"thread.started","thread_id":"thread-3"}'
            printf '%s\n' '{"type":"turn.started"}'
            printf '%s\n' 'stderr detail' >&2
            exit 1
            """);

        var error = await Assert.ThrowsAsync<ExitException>(() =>
            _dir.NewRunner(fake).RunAsync(new Request { Prompt = "fail" }));

        Assert.Equal(1, error.ExitCode);
        Assert.Contains("stderr detail", error.Stderr, StringComparison.Ordinal);
        Assert.NotNull(error.LastEvent);
        Assert.Equal(EventKind.TurnStarted, error.LastEvent!.Kind);
        Assert.NotNull(error.Result);
        Assert.Equal(2, error.Result!.Events.Count);
    }

    [UnixOnlyFact]
    public async Task SuccessfulExitCapturesStderr()
    {
        var fake = _dir.WriteFakeCodex("""
            record_args "$@"
            cat >/dev/null
            printf '%s\n' 'successful stderr detail' >&2
            printf '%s\n' '{"type":"thread.started","thread_id":"thread-stderr"}'
            printf '%s\n' '{"type":"turn.completed","usage":{"input_tokens":1,"output_tokens":1}}'
            """);

        var result = await _dir.NewRunner(fake).RunAsync(new Request { Prompt = "ok" });
        Assert.Contains("successful stderr detail", result.Stderr, StringComparison.Ordinal);
    }

    [UnixOnlyFact]
    public async Task StderrTailLimitKeepsOnlyTheEnd()
    {
        var fake = _dir.WriteFakeCodex("""
            record_args "$@"
            cat >/dev/null
            printf '%s' '0123456789' >&2
            exit 1
            """);

        var error = await Assert.ThrowsAsync<ExitException>(() =>
            _dir.NewRunner(fake, stderrLimit: 4).RunAsync(new Request { Prompt = "fail" }));
        Assert.Equal("6789", error.Stderr);
        Assert.Equal("6789", error.Result!.Stderr);
    }

    [UnixOnlyFact]
    public async Task LargeStderrOutputPreservesTail()
    {
        var fake = _dir.WriteFakeCodex("""
            record_args "$@"
            cat >/dev/null
            i=0
            while [ "$i" -lt 8192 ]; do
              printf '%s' '0123456789abcdef' >&2
              i=$((i + 1))
            done
            printf '%s\n' 'stderr end marker' >&2
            exit 1
            """);

        var error = await Assert.ThrowsAsync<ExitException>(() =>
            _dir.NewRunner(fake, stderrLimit: 256).RunAsync(new Request { Prompt = "fail" }));
        Assert.True(error.Stderr.Length <= 256);
        Assert.Contains("stderr end marker", error.Stderr, StringComparison.Ordinal);
    }

    [UnixOnlyFact]
    public async Task CodexEventErrorPrecedesExitError()
    {
        var fake = _dir.WriteFakeCodex("""
            record_args "$@"
            cat >/dev/null
            printf '%s\n' '{"type":"thread.started","thread_id":"thread-3"}'
            printf '%s\n' '{"type":"turn.started"}'
            printf '%s\n' '{"type":"error","message":"invalid_json_schema: bad model"}'
            printf '%s\n' 'stderr detail' >&2
            exit 1
            """);

        var error = await Assert.ThrowsAsync<CodexErrorException>(() =>
            _dir.NewRunner(fake).RunAsync(new Request { Prompt = "fail" }));
        Assert.Equal(EventKind.Error, error.Event.Kind);
        Assert.Contains("invalid_json_schema: bad model", error.Message, StringComparison.Ordinal);
    }

    [UnixOnlyFact]
    public async Task CodexTurnFailedReturnsCodexError()
    {
        var fake = _dir.WriteFakeCodex("""
            record_args "$@"
            cat >/dev/null
            printf '%s\n' '{"type":"thread.started","thread_id":"thread-failed"}'
            printf '%s\n' '{"type":"turn.failed","error":{"message":"turn broke"}}'
            """);

        var error = await Assert.ThrowsAsync<CodexErrorException>(() =>
            _dir.NewRunner(fake).RunAsync(new Request { Prompt = "fail" }));
        Assert.Contains("turn broke", error.Message, StringComparison.Ordinal);
    }

    [UnixOnlyFact]
    public async Task DecodeErrorReportsLineAndRaw()
    {
        var fake = _dir.WriteFakeCodex("""
            record_args "$@"
            cat >/dev/null
            printf '%s\n' 'not-json'
            """);

        var error = await Assert.ThrowsAsync<DecodeException>(() =>
            _dir.NewRunner(fake).RunAsync(new Request { Prompt = "decode" }));
        Assert.Equal(1, error.Line);
        Assert.Equal("not-json", error.Raw);
        Assert.NotNull(error.Result);
    }

    [UnixOnlyFact]
    public async Task HandlerErrorCancelsRun()
    {
        var fake = _dir.WriteFakeCodex("""
            record_args "$@"
            cat >/dev/null
            printf '%s\n' '{"type":"thread.started","thread_id":"thread-4"}'
            printf '%s\n' '{"type":"turn.started"}'
            sleep 5
            """);

        var cause = new InvalidOperationException("stop");
        var options = new RunOptions
        {
            Handler = (@event, _) =>
                @event.Kind == EventKind.TurnStarted ? throw cause : ValueTask.CompletedTask,
        };

        var error = await Assert.ThrowsAsync<HandlerException>(() =>
            _dir.NewRunner(fake).RunAsync(new Request { Prompt = "handler" }, options));
        Assert.Same(cause, error.InnerException);
        Assert.NotNull(error.Result);
    }

    [UnixOnlyFact]
    public async Task HandlerReceivesEveryEvent()
    {
        var fake = _dir.WriteFakeCodex("""
            record_args "$@"
            cat >/dev/null
            printf '%s\n' '{"type":"thread.started","thread_id":"thread-h"}'
            printf '%s\n' '{"type":"turn.completed","usage":{"input_tokens":1,"output_tokens":1}}'
            """);

        var seen = new List<EventKind>();
        var options = new RunOptions
        {
            Handler = (@event, _) =>
            {
                lock (seen)
                {
                    seen.Add(@event.Kind);
                }
                return ValueTask.CompletedTask;
            },
        };

        await _dir.NewRunner(fake).RunAsync(new Request { Prompt = "handler" }, options);
        Assert.Equal([EventKind.ThreadStarted, EventKind.TurnCompleted], seen);
    }

    [UnixOnlyFact]
    public async Task MissingExecutableThrowsProcessException()
    {
        var runner = new Runner(new RunnerOptions
        {
            Executable = Path.Combine(_dir.Root, "does-not-exist"),
        });
        await Assert.ThrowsAsync<ProcessException>(() =>
            runner.RunAsync(new Request { Prompt = "hi" }));
    }
}
