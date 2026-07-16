using System.ComponentModel;
using System.Diagnostics;

namespace C3OSS.Codexcw;

/// <summary>Configures a <see cref="Runner"/>. Non-positive sizes keep the defaults.</summary>
public sealed record RunnerOptions
{
    /// <summary>The wrapped agent CLI. The default is <see cref="Agent.Codex"/>.</summary>
    public Agent Agent { get; init; } = Agent.Codex;

    /// <summary>The agent executable path; null uses the agent name. It is the primary test seam.</summary>
    public string? Executable { get; init; }

    /// <summary>Environment variables (KEY=VALUE) appended to every child process.</summary>
    public IReadOnlyList<string> Env { get; init; } = [];

    /// <summary>The per-session event channel buffer. The default is 1024.</summary>
    public int EventBuffer { get; init; }

    /// <summary>The captured stderr tail size in bytes. The default is 1 MiB.</summary>
    public int StderrLimit { get; init; }

    /// <summary>The maximum accepted JSONL line length. The default is 64 MiB.</summary>
    public int ScanMaxBytes { get; init; }

    /// <summary>The default sandbox mode. The default is <see cref="SandboxMode.ReadOnly"/>.</summary>
    public SandboxMode DefaultSandbox { get; init; } = SandboxMode.ReadOnly;

    /// <summary>The default approval policy. The default is <see cref="ApprovalPolicy.Never"/>.</summary>
    public ApprovalPolicy DefaultApproval { get; init; } = ApprovalPolicy.Never;
}

/// <summary>Configures one run.</summary>
public sealed record RunOptions
{
    /// <summary>
    /// Receives every decoded event as it streams from the selected agent.
    /// A handler exception cancels the process.
    /// </summary>
    public Func<Event, CancellationToken, ValueTask>? Handler { get; init; }
}

/// <summary>Starts agent CLI processes and decodes their JSONL event streams.</summary>
public sealed class Runner
{
    internal const int DefaultEventBuffer = 1024;
    internal const int DefaultStderrLimit = 1 << 20;
    internal const int DefaultScanMax = 64 << 20;
    private static readonly TimeSpan WaitDelay = TimeSpan.FromSeconds(1);
    private static long _runCounter;

    private readonly Agent _agent;
    private readonly string _executable;
    private readonly IReadOnlyList<string> _env;
    private readonly int _eventBuffer;
    private readonly int _stderrLimit;
    private readonly int _scanMaxBytes;
    private readonly SandboxMode _defaultSandbox;
    private readonly ApprovalPolicy _defaultApproval;

    /// <summary>Creates a Runner with safe automation defaults.</summary>
    public Runner(RunnerOptions? options = null)
    {
        options ??= new RunnerOptions();
        _agent = options.Agent;
        _executable = string.IsNullOrEmpty(options.Executable) ? _agent.Name() : options.Executable;
        _env = options.Env;
        _eventBuffer = options.EventBuffer > 0 ? options.EventBuffer : DefaultEventBuffer;
        _stderrLimit = options.StderrLimit > 0 ? options.StderrLimit : DefaultStderrLimit;
        _scanMaxBytes = options.ScanMaxBytes > 0 ? options.ScanMaxBytes : DefaultScanMax;
        _defaultSandbox = options.DefaultSandbox;
        _defaultApproval = options.DefaultApproval;
    }

    internal int EventBuffer => _eventBuffer;

    /// <summary>Starts one process, drains its event stream, and waits for completion.</summary>
    public async Task<RunResult> RunAsync(
        Request request,
        RunOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        var session = Start(request, options, cancellationToken);
        await foreach (var _ in session.Events(CancellationToken.None).ConfigureAwait(false))
        {
        }
        return await session.WaitAsync(CancellationToken.None).ConfigureAwait(false);
    }

    /// <summary>Launches one agent process and returns immediately.</summary>
    public Session Start(
        Request request,
        RunOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        var prepared = _agent == Agent.Claude
            ? ClaudeArgs.Prepare(request)
            : CodexArgs.Prepare(request, _defaultSandbox, _defaultApproval);

        var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var session = new Session(NewRunId(), cts, _eventBuffer);
        Process process;
        try
        {
            process = Spawn(prepared, request);
        }
        catch
        {
            DeleteSchemaTemp(prepared.SchemaTempPath);
            cts.Dispose();
            throw;
        }

        cts.Token.Register(() => Session.KillProcessTree(process));

        _ = WriteStdinAsync(process, request);
        _ = session.ForwardEventsAsync();
        _ = CollectAsync(session, process, prepared.SchemaTempPath, options?.Handler);

        return session;
    }

    /// <summary>Starts N agent processes with bounded concurrency.</summary>
    public Group RunMany(
        IReadOnlyList<Request> requests,
        GroupOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        options ??= new GroupOptions();
        var maxConcurrent = options.MaxConcurrent > 0 ? options.MaxConcurrent : 4;
        var eventBuffer = options.EventBuffer > 0 ? options.EventBuffer : _eventBuffer;

        var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var group = new Group(cts, eventBuffer);
        _ = group.ForwardEventsAsync();
        _ = RunManyAsync(group, requests, maxConcurrent, options.RunOptions);
        return group;
    }

    private async Task RunManyAsync(
        Group group,
        IReadOnlyList<Request> requests,
        int maxConcurrent,
        RunOptions? runOptions)
    {
        var results = new GroupResult[requests.Count];
        using var semaphore = new SemaphoreSlim(maxConcurrent);
        var runs = new List<Task>(requests.Count);

        for (var i = 0; i < requests.Count; i++)
        {
            if (group.Token.IsCancellationRequested)
            {
                results[i] = new GroupResult { Index = i, Error = new RunCanceledException() };
                continue;
            }

            await semaphore.WaitAsync(CancellationToken.None).ConfigureAwait(false);
            var index = i;
            var request = requests[i];
            runs.Add(Task.Run(async () =>
            {
                try
                {
                    results[index] = await RunOneAsync(group, index, request, runOptions).ConfigureAwait(false);
                }
                finally
                {
                    semaphore.Release();
                }
            }));
        }

        await Task.WhenAll(runs).ConfigureAwait(false);
        group.Complete(results);
    }

    private async Task<GroupResult> RunOneAsync(Group group, int index, Request request, RunOptions? runOptions)
    {
        if (group.Token.IsCancellationRequested)
        {
            return new GroupResult { Index = index, Error = new RunCanceledException() };
        }

        Session session;
        try
        {
            session = Start(request, runOptions, group.Token);
        }
        catch (Exception ex) when (ex is CodexcwException or OperationCanceledException)
        {
            return new GroupResult { Index = index, Error = ex };
        }

        using (session)
        {
            await foreach (var @event in session.Events().ConfigureAwait(false))
            {
                group.InternalWriter.TryWrite(new RunEvent(session.Id, index, @event));
            }

            try
            {
                var result = await session.WaitAsync().ConfigureAwait(false);
                return new GroupResult { Index = index, RunId = session.Id, Result = result };
            }
            catch (RunCanceledException ex)
            {
                return new GroupResult { Index = index, RunId = session.Id, Result = ex.Result, Error = ex };
            }
            catch (CodexcwException ex)
            {
                return new GroupResult { Index = index, RunId = session.Id, Result = ex.Result, Error = ex };
            }
        }
    }

    private Process Spawn(PreparedRun prepared, Request request)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = _executable,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        foreach (var arg in prepared.Args)
        {
            startInfo.ArgumentList.Add(arg);
        }
        if (prepared.WorkingDirectory is not null)
        {
            startInfo.WorkingDirectory = prepared.WorkingDirectory;
        }
        ApplyEnv(startInfo, _env);
        ApplyEnv(startInfo, request.Env);

        var process = new Process { StartInfo = startInfo };
        try
        {
            process.Start();
        }
        catch (Exception ex) when (ex is Win32Exception or InvalidOperationException or PlatformNotSupportedException)
        {
            process.Dispose();
            throw new ProcessException($"start {_agent.Name()}: {ex.Message}", ex);
        }
        return process;
    }

    private static void ApplyEnv(ProcessStartInfo startInfo, IReadOnlyList<string> pairs)
    {
        foreach (var pair in pairs)
        {
            var separator = pair.IndexOf('=', StringComparison.Ordinal);
            if (separator <= 0)
            {
                continue;
            }
            startInfo.Environment[pair[..separator]] = pair[(separator + 1)..];
        }
    }

    private static async Task WriteStdinAsync(Process process, Request request)
    {
        var stdin = process.StandardInput.BaseStream;
        try
        {
            await CodexArgs.WritePromptAsync(request, stdin, CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException)
        {
            // The child exited before reading its stdin; nothing to deliver.
        }
        finally
        {
            try
            {
                stdin.Close();
            }
            catch (IOException)
            {
            }
        }
    }

    private async Task CollectAsync(Session session, Process process, string? schemaTempPath, Func<Event, CancellationToken, ValueTask>? handler)
    {
        var startedAt = DateTimeOffset.Now;
        var events = new List<Event>();
        Event? lastEvent = null;
        var finalMessage = "";
        var usage = new Usage();
        var threadId = "";
        Exception? runError = null;

        var stderrTail = new TailBuffer(_stderrLimit);
        var stderrPump = stderrTail.PumpAsync(process.StandardError.BaseStream, CancellationToken.None);
        var decoder = NewDecoder();
        var stdout = process.StandardOutput.BaseStream;

        var line = 0;
        try
        {
            await foreach (var rawLine in JsonlReader.ReadLinesAsync(stdout, _scanMaxBytes).ConfigureAwait(false))
            {
                line++;
                var raw = rawLine.Trim();
                if (raw.Length == 0)
                {
                    continue;
                }

                IReadOnlyList<Event> decoded;
                try
                {
                    decoded = decoder.Decode(raw, session.Id, threadId, DateTimeOffset.Now);
                }
                catch (Exception ex) when (ex is System.Text.Json.JsonException or FormatException)
                {
                    runError = new DecodeException(line, raw, ex.Message, ex);
                    session.Cancel();
                    break;
                }

                foreach (var decodedEvent in decoded)
                {
                    var @event = decodedEvent;
                    if (@event.ThreadStarted is not null)
                    {
                        threadId = @event.ThreadStarted.ThreadId;
                        @event = @event with { ThreadId = threadId };
                        session.SetThreadId(threadId);
                    }
                    if (@event.ThreadId.Length == 0)
                    {
                        @event = @event with { ThreadId = threadId };
                    }
                    if (@event.ItemCompleted?.Item is { Kind: ItemKind.AgentMessage } message)
                    {
                        finalMessage = message.Text;
                    }
                    if (@event.TurnCompleted is not null)
                    {
                        usage = @event.TurnCompleted.Usage;
                    }
                    if (@event.TurnFailed is not null)
                    {
                        usage = @event.TurnFailed.Usage;
                    }

                    lastEvent = @event;
                    events.Add(@event);
                    session.InternalWriter.TryWrite(@event);

                    if (handler is not null)
                    {
                        try
                        {
                            await handler(@event, session.Token).ConfigureAwait(false);
                        }
                        catch (Exception ex)
                        {
                            runError = new HandlerException(ex);
                            session.Cancel();
                            break;
                        }
                    }
                }
                if (runError is not null)
                {
                    break;
                }
            }
        }
        catch (LineTooLongException ex)
        {
            runError ??= new DecodeException(line + 1, "", ex.Message, ex);
            session.Cancel();
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException)
        {
            // The pipe was force-closed by cancellation or the bounded drain.
        }

        await process.WaitForExitAsync(CancellationToken.None).ConfigureAwait(false);
        var exitCode = process.ExitCode;

        // Equivalent of Go's cmd.WaitDelay: a killed child's descendants can
        // inherit stderr and keep the pipe open; force-close it after 1s.
        var waitDelayExpired = false;
        if (await Task.WhenAny(stderrPump, Task.Delay(WaitDelay)).ConfigureAwait(false) != stderrPump)
        {
            waitDelayExpired = true;
            try
            {
                process.StandardError.BaseStream.Dispose();
            }
            catch (IOException)
            {
            }
            await stderrPump.ConfigureAwait(false);
        }

        DeleteSchemaTemp(schemaTempPath);

        var result = new RunResult
        {
            RunId = session.Id,
            ThreadId = threadId,
            FinalMessage = finalMessage,
            Usage = usage,
            Events = events,
            Stderr = stderrTail.ToString(),
            StartedAt = startedAt,
            FinishedAt = DateTimeOffset.Now,
        };

        runError ??= ClassifyAgentEvent(lastEvent);
        runError ??= ClassifyProcessExit(session, exitCode, result.Stderr, lastEvent, waitDelayExpired);
        AttachResult(runError, result);

        session.InternalWriter.TryComplete();
        session.Complete(result, runError);
        process.Dispose();
    }

    private Exception? ClassifyAgentEvent(Event? lastEvent)
    {
        if (lastEvent is null || (lastEvent.Error is null && lastEvent.TurnFailed is null))
        {
            return null;
        }
        return _agent == Agent.Claude
            ? new ClaudeErrorException(lastEvent)
            : new CodexErrorException(lastEvent);
    }

    private static Exception? ClassifyProcessExit(
        Session session,
        int exitCode,
        string stderr,
        Event? lastEvent,
        bool waitDelayExpired)
    {
        if (exitCode != 0)
        {
            if (session.Token.IsCancellationRequested)
            {
                return new RunCanceledException();
            }
            return new ExitException(exitCode, stderr, lastEvent);
        }
        if (waitDelayExpired)
        {
            return new ProcessException("agent stderr stayed open past the 1s wait delay");
        }
        return null;
    }

    private static void AttachResult(Exception? error, RunResult result)
    {
        switch (error)
        {
            case CodexcwException codexcw:
                codexcw.Result = result;
                break;
            case RunCanceledException cancelled:
                cancelled.Result = result;
                break;
            default:
                break;
        }
    }

    private IEventDecoder NewDecoder() =>
        _agent == Agent.Claude ? new ClaudeDecoder() : new CodexDecoder();

    private static void DeleteSchemaTemp(string? path)
    {
        if (path is null)
        {
            return;
        }
        try
        {
            File.Delete(path);
        }
        catch (IOException)
        {
        }
    }

    private static string NewRunId() =>
        $"run-{(DateTimeOffset.UtcNow - DateTimeOffset.UnixEpoch).Ticks * 100}-{Interlocked.Increment(ref _runCounter)}";
}
