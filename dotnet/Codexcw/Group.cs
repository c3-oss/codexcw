using System.Threading.Channels;

namespace C3OSS.Codexcw;

/// <summary>One event multiplexed from <see cref="Runner.RunMany"/>.</summary>
/// <param name="RunId">The wrapper-assigned run id.</param>
/// <param name="Index">The request index in the RunMany input list.</param>
/// <param name="Event">The decoded agent event.</param>
public readonly record struct RunEvent(string RunId, int Index, Event Event);

/// <summary>The result for one request passed to <see cref="Runner.RunMany"/>.</summary>
public sealed record GroupResult
{
    /// <summary>The request index in the RunMany input list.</summary>
    public int Index { get; init; }

    /// <summary>The wrapper-assigned run id when the run started.</summary>
    public string RunId { get; init; } = "";

    /// <summary>The run report; on failure it carries the events collected so far.</summary>
    public RunResult? Result { get; init; }

    /// <summary>The failure for this run when it did not complete cleanly.</summary>
    public Exception? Error { get; init; }
}

/// <summary>Configures <see cref="Runner.RunMany"/>. Non-positive sizes keep the defaults.</summary>
public sealed record GroupOptions
{
    /// <summary>How many agent processes run at once. The default is 4.</summary>
    public int MaxConcurrent { get; init; }

    /// <summary>The multiplexed event channel buffer. The default is the runner's event buffer.</summary>
    public int EventBuffer { get; init; }

    /// <summary>Run options applied to each run in the group.</summary>
    public RunOptions? RunOptions { get; init; }
}

/// <summary>Represents a batch of running agent processes.</summary>
public sealed class Group : IDisposable, IAsyncDisposable
{
    private readonly Channel<RunEvent> _internalChannel;
    private readonly Channel<RunEvent> _publicChannel;
    private readonly TaskCompletionSource<IReadOnlyList<GroupResult>> _outcome =
        new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly CancellationTokenSource _cts;
    private readonly CancellationToken _token;
    private Task _forwarderTask = Task.CompletedTask;
    private int _disposed;

    internal Group(CancellationTokenSource cts, int eventBuffer)
    {
        _cts = cts;
        _token = cts.Token;
        _internalChannel = Channel.CreateUnbounded<RunEvent>(new UnboundedChannelOptions
        {
            SingleReader = true,
        });
        _publicChannel = Channel.CreateBounded<RunEvent>(new BoundedChannelOptions(Math.Max(1, eventBuffer))
        {
            FullMode = BoundedChannelFullMode.Wait,
            SingleReader = true,
            SingleWriter = true,
        });
    }

    internal CancellationToken Token => _token;

    internal ChannelWriter<RunEvent> InternalWriter => _internalChannel.Writer;

    /// <summary>
    /// Streams multiplexed events until every run has finished. Single
    /// consumer; waiting without consuming the stream is safe.
    /// </summary>
    public IAsyncEnumerable<RunEvent> Events(CancellationToken cancellationToken = default) =>
        _publicChannel.Reader.ReadAllAsync(cancellationToken);

    /// <summary>Stops all active and pending runs.</summary>
    public void Cancel()
    {
        try
        {
            _cts.Cancel();
        }
        catch (ObjectDisposedException)
        {
        }
    }

    /// <summary>
    /// Returns every run result once all runs have finished. If any run
    /// failed, throws <see cref="GroupException"/> carrying all the results.
    /// The token abandons this wait only; it does not cancel the runs.
    /// </summary>
    public async Task<IReadOnlyList<GroupResult>> WaitAsync(CancellationToken cancellationToken = default)
    {
        var task = cancellationToken.CanBeCanceled
            ? _outcome.Task.WaitAsync(cancellationToken)
            : _outcome.Task;
        var results = await task.ConfigureAwait(false);
        foreach (var result in results)
        {
            if (result.Error is not null)
            {
                throw new GroupException(results);
            }
        }
        return results;
    }

    /// <summary>
    /// Cancels the batch if it is still active and releases its cancellation
    /// resources. Use <see cref="DisposeAsync"/> to also await the batch's
    /// internal tasks.
    /// </summary>
    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }
        Cancel();
        _cts.Dispose();
    }

    /// <summary>
    /// Cancels the batch if it is still active, waits for every run and the
    /// event forwarder to finish, and releases the cancellation resources.
    /// Failed runs do not throw here; <see cref="WaitAsync"/> reports them.
    /// </summary>
    public async ValueTask DisposeAsync()
    {
        Cancel();
        try
        {
            await _outcome.Task.ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is CodexcwException or OperationCanceledException)
        {
        }
        await _forwarderTask.ConfigureAwait(false);
        Dispose();
    }

    internal void StartForwarding() => _forwarderTask = ForwardEventsAsync();

    private async Task ForwardEventsAsync()
    {
        try
        {
            while (await _internalChannel.Reader.WaitToReadAsync(Token).ConfigureAwait(false))
            {
                while (_internalChannel.Reader.TryRead(out var @event))
                {
                    await _publicChannel.Writer.WriteAsync(@event, Token).ConfigureAwait(false);
                }
            }
        }
        catch (OperationCanceledException)
        {
        }
        finally
        {
            _publicChannel.Writer.TryComplete();
        }
    }

    internal void Complete(IReadOnlyList<GroupResult> results)
    {
        _internalChannel.Writer.TryComplete();
        _outcome.TrySetResult(results);
    }

    internal void Fault(Exception error)
    {
        _internalChannel.Writer.TryComplete();
        _outcome.TrySetException(
            new ProcessException($"run group orchestration failed: {error.Message}", error));
    }
}
