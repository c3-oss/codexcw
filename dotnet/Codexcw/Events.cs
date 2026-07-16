namespace C3OSS.Codexcw;

/// <summary>The normalized top-level event kind.</summary>
public enum EventKind
{
    /// <summary>An agent created or resumed a session.</summary>
    ThreadStarted,

    /// <summary>An agent turn began.</summary>
    TurnStarted,

    /// <summary>An agent turn finished successfully.</summary>
    TurnCompleted,

    /// <summary>An agent turn failed.</summary>
    TurnFailed,

    /// <summary>An agent started a streamed item.</summary>
    ItemStarted,

    /// <summary>An agent completed a streamed item.</summary>
    ItemCompleted,

    /// <summary>A top-level agent error.</summary>
    Error,

    /// <summary>An event type this library does not model; <see cref="Event.Type"/> keeps the wire string.</summary>
    Other,
}

/// <summary>The normalized item kind inside item.started and item.completed.</summary>
public enum ItemKind
{
    /// <summary>Assistant text.</summary>
    AgentMessage,

    /// <summary>A Codex reasoning item.</summary>
    Reasoning,

    /// <summary>A shell command execution.</summary>
    CommandExecution,

    /// <summary>File edits made by the agent.</summary>
    FileChange,

    /// <summary>An MCP tool call.</summary>
    McpToolCall,

    /// <summary>A web search operation.</summary>
    WebSearch,

    /// <summary>A plan update.</summary>
    PlanUpdate,

    /// <summary>A generic tool call from the claude agent.</summary>
    ToolCall,

    /// <summary>A multi-agent collab tool call (spawn/wait/send between agent threads).</summary>
    CollabToolCall,

    /// <summary>An item-scoped Codex error.</summary>
    Error,

    /// <summary>An item type this library does not model; <see cref="Item.Type"/> keeps the wire string.</summary>
    Other,
}

/// <summary>One decoded or normalized agent JSONL event.</summary>
public sealed record Event
{
    /// <summary>The normalized top-level event kind.</summary>
    public required EventKind Kind { get; init; }

    /// <summary>The wire event type string, including unmodeled passthrough types.</summary>
    public required string Type { get; init; }

    /// <summary>The wrapper-assigned run id.</summary>
    public required string RunId { get; init; }

    /// <summary>The agent session or thread id once known.</summary>
    public string ThreadId { get; init; } = "";

    /// <summary>The local time when the line was decoded.</summary>
    public DateTimeOffset ReceivedAt { get; init; }

    /// <summary>The original JSON event payload.</summary>
    public required string Raw { get; init; }

    /// <summary>Set for <see cref="EventKind.ThreadStarted"/>.</summary>
    public ThreadStartedPayload? ThreadStarted { get; init; }

    /// <summary>Set for <see cref="EventKind.TurnCompleted"/>.</summary>
    public TurnCompletedPayload? TurnCompleted { get; init; }

    /// <summary>Set for <see cref="EventKind.TurnFailed"/>.</summary>
    public TurnFailedPayload? TurnFailed { get; init; }

    /// <summary>Set for <see cref="EventKind.ItemStarted"/>.</summary>
    public ItemPayload? ItemStarted { get; init; }

    /// <summary>Set for <see cref="EventKind.ItemCompleted"/>.</summary>
    public ItemPayload? ItemCompleted { get; init; }

    /// <summary>Set for <see cref="EventKind.Error"/>.</summary>
    public ErrorPayload? Error { get; init; }
}

/// <summary>Carries the agent session or thread id.</summary>
/// <param name="ThreadId">The agent session or thread identifier.</param>
public sealed record ThreadStartedPayload(string ThreadId);

/// <summary>Carries token usage for the completed turn.</summary>
/// <param name="Usage">The usage reported by the selected agent.</param>
public sealed record TurnCompletedPayload(Usage Usage);

/// <summary>Carries the error payload for a failed agent turn.</summary>
/// <param name="Error">The failed turn description.</param>
/// <param name="Usage">The usage reported before the turn failed.</param>
public sealed record TurnFailedPayload(ErrorPayload Error, Usage Usage);

/// <summary>The common error object used by error events and turn.failed.</summary>
public sealed record ErrorPayload
{
    /// <summary>The human-readable error text.</summary>
    public string Message { get; init; } = "";

    /// <summary>The raw nested error payload when present.</summary>
    public string Raw { get; init; } = "";
}

/// <summary>Wraps one item payload.</summary>
/// <param name="Item">The typed projection of the nested item.</param>
public sealed record ItemPayload(Item Item);

/// <summary>A typed projection of the item payload. <see cref="Raw"/> remains authoritative.</summary>
public sealed record Item
{
    /// <summary>The native or synthesized agent item id.</summary>
    public string Id { get; init; } = "";

    /// <summary>The normalized item kind.</summary>
    public ItemKind Kind { get; init; } = ItemKind.Other;

    /// <summary>The wire item type string, including unmodeled passthrough types.</summary>
    public string Type { get; init; } = "";

    /// <summary>The item status when the agent reports one.</summary>
    public string Status { get; init; } = "";

    /// <summary>The original nested item payload.</summary>
    public string Raw { get; init; } = "";

    /// <summary>Assistant text for agent_message items.</summary>
    public string Text { get; init; } = "";

    /// <summary>Error text for error items.</summary>
    public string Message { get; init; } = "";

    /// <summary>The shell command for command_execution items.</summary>
    public string Command { get; init; } = "";

    /// <summary>Combined command output reported by the agent.</summary>
    public string AggregatedOutput { get; init; } = "";

    /// <summary>The command exit code when available.</summary>
    public int? ExitCode { get; init; }

    /// <summary>File edits for file_change items.</summary>
    public IReadOnlyList<FileChange> Changes { get; init; } = [];
}

/// <summary>Describes one file_change entry.</summary>
/// <param name="Path">The absolute or workspace-relative file path.</param>
/// <param name="Kind">The normalized change kind reported by the agent.</param>
public sealed record FileChange(string Path, string Kind);

/// <summary>Usage information reported for a turn.</summary>
public sealed record Usage
{
    /// <summary>The number of input tokens consumed.</summary>
    public long InputTokens { get; init; }

    /// <summary>The number of cached input tokens.</summary>
    public long CachedInputTokens { get; init; }

    /// <summary>The number of tokens written to Claude's cache.</summary>
    public long CacheCreationInputTokens { get; init; }

    /// <summary>The number of output tokens produced.</summary>
    public long OutputTokens { get; init; }

    /// <summary>The number of reasoning output tokens.</summary>
    public long ReasoningOutputTokens { get; init; }

    /// <summary>The reported or derived total token count.</summary>
    public long TotalTokens { get; init; }

    /// <summary>The total Claude run cost in US dollars.</summary>
    public double TotalCostUsd { get; init; }

    /// <summary>Claude usage grouped by model identifier.</summary>
    public IReadOnlyDictionary<string, ModelUsage> ModelUsage { get; init; } =
        new Dictionary<string, ModelUsage>();
}

/// <summary>Claude usage and cost information for one model.</summary>
public sealed record ModelUsage
{
    /// <summary>The number of input tokens consumed.</summary>
    public long InputTokens { get; init; }

    /// <summary>The number of output tokens produced.</summary>
    public long OutputTokens { get; init; }

    /// <summary>The number of tokens read from cache.</summary>
    public long CacheReadInputTokens { get; init; }

    /// <summary>The number of tokens written to cache.</summary>
    public long CacheCreationInputTokens { get; init; }

    /// <summary>The number of web search requests.</summary>
    public long WebSearchRequests { get; init; }

    /// <summary>The model cost in US dollars.</summary>
    public double CostUsd { get; init; }

    /// <summary>The model context-window size.</summary>
    public long ContextWindow { get; init; }

    /// <summary>The model maximum output-token count.</summary>
    public long MaxOutputTokens { get; init; }
}

internal static class EventTypes
{
    public const string ThreadStarted = "thread.started";
    public const string TurnStarted = "turn.started";
    public const string TurnCompleted = "turn.completed";
    public const string TurnFailed = "turn.failed";
    public const string ItemStarted = "item.started";
    public const string ItemCompleted = "item.completed";
    public const string Error = "error";

    public static EventKind KindOf(string type) => type switch
    {
        ThreadStarted => EventKind.ThreadStarted,
        TurnStarted => EventKind.TurnStarted,
        TurnCompleted => EventKind.TurnCompleted,
        TurnFailed => EventKind.TurnFailed,
        ItemStarted => EventKind.ItemStarted,
        ItemCompleted => EventKind.ItemCompleted,
        Error => EventKind.Error,
        _ => EventKind.Other,
    };
}

internal static class ItemTypes
{
    public const string AgentMessage = "agent_message";
    public const string Reasoning = "reasoning";
    public const string CommandExecution = "command_execution";
    public const string FileChange = "file_change";
    public const string McpToolCall = "mcp_tool_call";
    public const string WebSearch = "web_search";
    public const string PlanUpdate = "plan_update";
    public const string ToolCall = "tool_call";
    public const string CollabToolCall = "collab_tool_call";
    public const string Error = "error";

    public static ItemKind KindOf(string type) => type switch
    {
        AgentMessage => ItemKind.AgentMessage,
        Reasoning => ItemKind.Reasoning,
        CommandExecution => ItemKind.CommandExecution,
        FileChange => ItemKind.FileChange,
        McpToolCall => ItemKind.McpToolCall,
        WebSearch => ItemKind.WebSearch,
        PlanUpdate => ItemKind.PlanUpdate,
        ToolCall => ItemKind.ToolCall,
        CollabToolCall => ItemKind.CollabToolCall,
        Error => ItemKind.Error,
        _ => ItemKind.Other,
    };

    public static string TypeOf(ItemKind kind) => kind switch
    {
        ItemKind.AgentMessage => AgentMessage,
        ItemKind.Reasoning => Reasoning,
        ItemKind.CommandExecution => CommandExecution,
        ItemKind.FileChange => FileChange,
        ItemKind.McpToolCall => McpToolCall,
        ItemKind.WebSearch => WebSearch,
        ItemKind.PlanUpdate => PlanUpdate,
        ItemKind.ToolCall => ToolCall,
        ItemKind.CollabToolCall => CollabToolCall,
        ItemKind.Error => Error,
        _ => "",
    };
}
