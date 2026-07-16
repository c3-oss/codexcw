using System.Diagnostics;
using System.Threading.Channels;

namespace C3OSS.Codexcw;

/// <summary>Summarizes a completed agent invocation.</summary>
public sealed record RunResult
{
    /// <summary>The wrapper-assigned run id.</summary>
    public string RunId { get; init; } = "";

    /// <summary>The selected agent's session or thread id once known.</summary>
    public string ThreadId { get; init; } = "";

    /// <summary>The last completed agent_message text.</summary>
    public string FinalMessage { get; init; } = "";

    /// <summary>The last turn.completed usage payload.</summary>
    public Usage Usage { get; init; } = new();

    /// <summary>Every decoded event retained by the run.</summary>
    public IReadOnlyList<Event> Events { get; init; } = [];

    /// <summary>The captured stderr tail.</summary>
    public string Stderr { get; init; } = "";

    /// <summary>The local time when collection started.</summary>
    public DateTimeOffset StartedAt { get; init; }

    /// <summary>The local time when the process finished.</summary>
    public DateTimeOffset FinishedAt { get; init; }
}

/// <summary>Represents one running agent process.</summary>
public sealed class Session : IDisposable
{
    private readonly Channel<Event> _internalChannel;
    private readonly Channel<Event> _publicChannel;
    private readonly TaskCompletionSource<RunResult> _outcome =
        new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly CancellationTokenSource _cts;
    private readonly object _threadIdLock = new();
    private string _threadId = "";

    internal Session(string id, CancellationTokenSource cts, int eventBuffer)
    {
        Id = id;
        _cts = cts;
        _internalChannel = Channel.CreateUnbounded<Event>(new UnboundedChannelOptions
        {
            SingleReader = true,
            SingleWriter = true,
        });
        _publicChannel = Channel.CreateBounded<Event>(new BoundedChannelOptions(Math.Max(1, eventBuffer))
        {
            FullMode = BoundedChannelFullMode.Wait,
            SingleReader = true,
            SingleWriter = true,
        });
    }

    /// <summary>The wrapper-assigned run id.</summary>
    public string Id { get; }

    /// <summary>The selected agent's id once thread.started has arrived.</summary>
    public string ThreadId
    {
        get
        {
            lock (_threadIdLock)
            {
                return _threadId;
            }
        }
    }

    internal CancellationToken Token => _cts.Token;

    internal ChannelWriter<Event> InternalWriter => _internalChannel.Writer;

    /// <summary>
    /// Streams decoded events until the process exits. Single consumer: the
    /// stream is emptied as it is read, so enumerate it from one place only.
    /// Waiting without consuming the stream is safe — events are buffered
    /// independently of run completion.
    /// </summary>
    public IAsyncEnumerable<Event> Events(CancellationToken cancellationToken = default) =>
        _publicChannel.Reader.ReadAllAsync(cancellationToken);

    /// <summary>Stops the child process and every descendant.</summary>
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
    /// Waits for the process to exit and returns the final result. Failures
    /// throw a <see cref="CodexcwException"/> (or <see cref="RunCanceledException"/>)
    /// carrying the partial <see cref="RunResult"/>. The token abandons this
    /// wait only; it does not cancel the run.
    /// </summary>
    public Task<RunResult> WaitAsync(CancellationToken cancellationToken = default) =>
        cancellationToken.CanBeCanceled
            ? _outcome.Task.WaitAsync(cancellationToken)
            : _outcome.Task;

    /// <summary>Releases the run's cancellation resources.</summary>
    public void Dispose() => _cts.Dispose();

    internal void SetThreadId(string threadId)
    {
        lock (_threadIdLock)
        {
            _threadId = threadId;
        }
    }

    internal void Complete(RunResult result, Exception? error)
    {
        if (error is null)
        {
            _outcome.TrySetResult(result);
        }
        else
        {
            _outcome.TrySetException(error);
        }
    }

    /// <summary>
    /// Copies events from the never-blocking internal channel into the bounded
    /// public channel, so a slow or absent consumer cannot stall the collector
    /// and cancellation releases a forwarder blocked on backpressure.
    /// </summary>
    internal async Task ForwardEventsAsync()
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

    internal static void KillProcessTree(Process process)
    {
        try
        {
            process.Kill(entireProcessTree: true);
        }
        catch (InvalidOperationException)
        {
        }
        catch (SystemException)
        {
        }
    }
}
