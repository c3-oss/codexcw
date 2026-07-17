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
public sealed class Session : IDisposable, IAsyncDisposable
{
    private readonly Channel<Event> _internalChannel;
    private readonly Channel<Event> _publicChannel;
    private readonly TaskCompletionSource<RunResult> _outcome =
        new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly CancellationTokenSource _cts;
    private readonly CancellationToken _token;
    private readonly object _threadIdLock = new();
    private CancellationTokenRegistration _killRegistration;
    private Task _forwarderTask = Task.CompletedTask;
    private string _threadId = "";
    private int _disposed;

    internal Session(string id, CancellationTokenSource cts, int eventBuffer)
    {
        Id = id;
        _cts = cts;
        _token = cts.Token;
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

    internal CancellationToken Token => _token;

    internal ChannelWriter<Event> InternalWriter => _internalChannel.Writer;

    internal void SetKillRegistration(CancellationTokenRegistration registration) =>
        _killRegistration = registration;

    internal void ReleaseKillRegistration() => _killRegistration.Dispose();

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

    /// <summary>
    /// Cancels the run if it is still active and releases its cancellation
    /// resources. Use <see cref="DisposeAsync"/> to also await the run's
    /// internal tasks.
    /// </summary>
    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }
        Cancel();
        _killRegistration.Dispose();
        _cts.Dispose();
    }

    /// <summary>
    /// Cancels the run if it is still active, waits for the process and the
    /// event forwarder to finish, and releases the cancellation resources.
    /// A failed run does not throw here; <see cref="WaitAsync"/> reports it.
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

    internal void StartForwarding() => _forwarderTask = ForwardEventsAsync();

    /// <summary>
    /// Copies events from the never-blocking internal channel into the bounded
    /// public channel, so a slow or absent consumer cannot stall the collector
    /// and cancellation releases a forwarder blocked on backpressure.
    /// </summary>
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
