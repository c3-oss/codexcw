namespace C3OSS.Codexcw;

/// <summary>Base type for failures reported by this library.</summary>
public abstract class CodexcwException : Exception
{
    private protected CodexcwException(string message)
        : base(message)
    {
    }

    private protected CodexcwException(string message, Exception innerException)
        : base(message, innerException)
    {
    }

    /// <summary>
    /// The run report collected before the failure. Populated by run failures;
    /// null for validation errors raised before a process was started.
    /// </summary>
    public RunResult? Result { get; internal set; }
}

/// <summary>Thrown when a run has neither a prompt nor stdin input.</summary>
public sealed class PromptRequiredException : CodexcwException
{
    /// <summary>Creates the exception with its fixed message.</summary>
    public PromptRequiredException()
        : base("prompt or stdin is required")
    {
    }
}

/// <summary>Thrown when a <see cref="Request"/> fails validation.</summary>
public sealed class InvalidRequestException : CodexcwException
{
    /// <summary>Creates the exception for one validation failure.</summary>
    public InvalidRequestException(string message)
        : base("invalid request: " + message)
    {
    }
}

/// <summary>Reports a non-zero agent process exit.</summary>
public sealed class ExitException : CodexcwException
{
    /// <summary>Creates the exception for one process exit.</summary>
    public ExitException(int exitCode, string stderr, Event? lastEvent = null)
        : base($"agent exited with code {exitCode}")
    {
        ExitCode = exitCode;
        Stderr = stderr;
        LastEvent = lastEvent;
    }

    /// <summary>The process exit code.</summary>
    public int ExitCode { get; }

    /// <summary>The captured stderr tail.</summary>
    public string Stderr { get; }

    /// <summary>The last decoded event before process exit.</summary>
    public Event? LastEvent { get; }
}

/// <summary>Reports malformed JSONL from an agent.</summary>
public sealed class DecodeException : CodexcwException
{
    /// <summary>Creates the exception for one malformed line.</summary>
    public DecodeException(int line, string raw, string message, Exception? innerException = null)
        : base($"decode agent JSONL line {line}: {message}",
            innerException ?? new FormatException(message))
    {
        Line = line;
        Raw = raw;
    }

    /// <summary>The one-based JSONL line number.</summary>
    public int Line { get; }

    /// <summary>The malformed line when available.</summary>
    public string Raw { get; }
}

/// <summary>Reports a top-level error or failed turn event from Codex.</summary>
public sealed class CodexErrorException : CodexcwException
{
    /// <summary>Creates the exception for one Codex error event.</summary>
    public CodexErrorException(Event @event)
        : base(FormatMessage(@event))
    {
        Event = @event;
    }

    /// <summary>The Codex error event.</summary>
    public Event Event { get; }

    private static string FormatMessage(Event @event)
    {
        if (@event.Error is { Message.Length: > 0 } error)
        {
            return "codex error: " + error.Message;
        }
        if (@event.TurnFailed is { Error.Message.Length: > 0 } failed)
        {
            return "codex turn failed: " + failed.Error.Message;
        }
        return "codex error event";
    }
}

/// <summary>Reports a failed turn event from Claude.</summary>
public sealed class ClaudeErrorException : CodexcwException
{
    /// <summary>Creates the exception for one Claude error event.</summary>
    public ClaudeErrorException(Event @event)
        : base(FormatMessage(@event))
    {
        Event = @event;
    }

    /// <summary>The Claude error event.</summary>
    public Event Event { get; }

    private static string FormatMessage(Event @event)
    {
        if (@event.Error is { Message.Length: > 0 } error)
        {
            return "claude error: " + error.Message;
        }
        if (@event.TurnFailed is { Error.Message.Length: > 0 } failed)
        {
            return "claude turn failed: " + failed.Error.Message;
        }
        return "claude error event";
    }
}

/// <summary>Wraps an exception thrown by a run event handler.</summary>
public sealed class HandlerException : CodexcwException
{
    /// <summary>Creates the exception around the handler failure.</summary>
    public HandlerException(Exception innerException)
        : base("agent event handler failed: " + innerException.Message, innerException)
    {
    }
}

/// <summary>Reports an agent process spawn or I/O failure.</summary>
public sealed class ProcessException : CodexcwException
{
    /// <summary>Creates the exception with a failure description.</summary>
    public ProcessException(string message)
        : base(message)
    {
    }

    /// <summary>Creates the exception around an underlying failure.</summary>
    public ProcessException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}

/// <summary>Reports one or more failed runs from <see cref="Runner.RunMany"/>.</summary>
public sealed class GroupException : CodexcwException
{
    /// <summary>Creates the exception for one batch outcome.</summary>
    public GroupException(IReadOnlyList<GroupResult> results)
        : base($"{results.Count(static r => r.Error is not null)} agent run(s) failed")
    {
        Results = results;
    }

    /// <summary>Every run result, including failed runs.</summary>
    public IReadOnlyList<GroupResult> Results { get; }
}

/// <summary>
/// Reports a cancelled run. Derives from <see cref="OperationCanceledException"/>
/// so idiomatic cancellation handling keeps working; the partial run report is
/// available through <see cref="Result"/>.
/// </summary>
public sealed class RunCanceledException : OperationCanceledException
{
    /// <summary>Creates the exception for one cancelled run.</summary>
    public RunCanceledException(RunResult? result = null)
        : base("agent run was cancelled")
    {
        Result = result;
    }

    /// <summary>The run report collected before cancellation, when available.</summary>
    public RunResult? Result { get; internal set; }
}
