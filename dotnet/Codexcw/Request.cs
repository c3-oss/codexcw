namespace C3OSS.Codexcw;

/// <summary>Identifies the CLI wrapped by a <see cref="Runner"/>.</summary>
public enum Agent
{
    /// <summary>Wraps codex exec --json.</summary>
    Codex,

    /// <summary>Wraps claude -p --output-format stream-json.</summary>
    Claude,
}

/// <summary>The sandbox policy passed to codex exec.</summary>
public enum SandboxMode
{
    /// <summary>Lets Codex inspect files without write access.</summary>
    ReadOnly,

    /// <summary>Lets Codex write inside the configured workspace.</summary>
    WorkspaceWrite,

    /// <summary>Removes Codex sandbox filesystem restrictions.</summary>
    DangerFullAccess,
}

/// <summary>The Codex approval behavior applied through config overrides.</summary>
public enum ApprovalPolicy
{
    /// <summary>Asks before commands outside Codex's trusted set.</summary>
    Untrusted,

    /// <summary>Lets Codex work in the sandbox and request approval.</summary>
    OnRequest,

    /// <summary>Prevents interactive approval prompts.</summary>
    Never,
}

/// <summary>The permission behavior of a claude agent run.</summary>
public enum PermissionMode
{
    /// <summary>Auto-approves file edits inside the workspace.</summary>
    AcceptEdits,

    /// <summary>Lets Claude choose when to request permission.</summary>
    Auto,

    /// <summary>Skips all permission checks.</summary>
    BypassPermissions,

    /// <summary>Requires explicit permission for tool use.</summary>
    Manual,

    /// <summary>Keeps Claude in read-only planning mode.</summary>
    Plan,

    /// <summary>Denies any action that would prompt for approval.</summary>
    DontAsk,
}

/// <summary>Model aliases accepted by the claude agent.</summary>
public static class ClaudeModels
{
    /// <summary>Selects the latest Claude Haiku model.</summary>
    public const string Haiku = "haiku";

    /// <summary>Selects the latest Claude Sonnet model.</summary>
    public const string Sonnet = "sonnet";

    /// <summary>Selects the latest Claude Opus model.</summary>
    public const string Opus = "opus";
}

/// <summary>One -c key=value config override.</summary>
/// <param name="Key">The config path before the equals sign.</param>
/// <param name="Value">The config value after the equals sign.</param>
public readonly record struct ConfigOverride(string Key, string Value)
{
    /// <summary>Returns the exact key=value argument expected by codex -c.</summary>
    public override string ToString() =>
        string.IsNullOrEmpty(Key) ? Value : Key + "=" + Value;
}

/// <summary>Describes one selected-agent invocation.</summary>
public sealed record Request
{
    /// <summary>The user instruction sent to the selected agent.</summary>
    public string Prompt { get; init; } = "";

    /// <summary>
    /// Additional prompt input when <see cref="Prompt"/> is empty, or extra
    /// context when <see cref="Prompt"/> is set.
    /// </summary>
    public Stream? Stdin { get; init; }

    /// <summary>The working directory passed to codex exec as -C.</summary>
    public string? Dir { get; init; }

    /// <summary>Additional directories the selected agent may access.</summary>
    public IReadOnlyList<string> AddDirs { get; init; } = [];

    /// <summary>Images attached to the initial Codex prompt.</summary>
    public IReadOnlyList<string> Images { get; init; } = [];

    /// <summary>Overrides the selected agent's model for this run.</summary>
    public string? Model { get; init; }

    /// <summary>Selects a Codex config profile.</summary>
    public string? Profile { get; init; }

    /// <summary>The Codex sandbox policy (codex agent only).</summary>
    public SandboxMode? Sandbox { get; init; }

    /// <summary>The Codex approval policy applied through -c (codex agent only).</summary>
    public ApprovalPolicy? Approval { get; init; }

    /// <summary>The Claude permission mode (claude agent only).</summary>
    public PermissionMode? PermissionMode { get; init; }

    /// <summary>Tool patterns Claude may use without prompting (claude agent only).</summary>
    public IReadOnlyList<string> AllowedTools { get; init; } = [];

    /// <summary>Tool patterns denied to Claude (claude agent only).</summary>
    public IReadOnlyList<string> DisallowedTools { get; init; } = [];

    /// <summary>Raw Codex -c config overrides.</summary>
    public IReadOnlyList<ConfigOverride> Config { get; init; } = [];

    /// <summary>Feature flags passed with --enable.</summary>
    public IReadOnlyList<string> Enable { get; init; } = [];

    /// <summary>Feature flags passed with --disable.</summary>
    public IReadOnlyList<string> Disable { get; init; } = [];

    /// <summary>Makes Codex reject unrecognized config fields.</summary>
    public bool StrictConfig { get; init; }

    /// <summary>Keeps the selected agent's session data on disk.</summary>
    public bool Persistent { get; init; }

    /// <summary>Skips CODEX_HOME/config.toml.</summary>
    public bool IgnoreUserConfig { get; init; }

    /// <summary>Skips user and project execpolicy .rules files.</summary>
    public bool IgnoreRules { get; init; }

    /// <summary>Lets Codex enforce its Git repository check.</summary>
    public bool RequireGitRepo { get; init; }

    /// <summary>A JSON Schema file path for the final response.</summary>
    public string? OutputSchemaPath { get; init; }

    /// <summary>Inline JSON Schema text written to a temporary file for the run.</summary>
    public string? OutputSchema { get; init; }

    /// <summary>Asks Codex to write the final message to this file.</summary>
    public string? OutputLastMessagePath { get; init; }

    /// <summary>Passes Codex's full bypass flag.</summary>
    public bool DangerouslyBypassSandbox { get; init; }

    /// <summary>Runs enabled hooks without persisted trust.</summary>
    public bool DangerouslyBypassHooks { get; init; }

    /// <summary>Environment variables (KEY=VALUE) appended for the agent process.</summary>
    public IReadOnlyList<string> Env { get; init; } = [];

    /// <summary>Resumes a specific agent session or thread id.</summary>
    public string? ResumeId { get; init; }

    /// <summary>Resumes the selected agent's most recent session.</summary>
    public bool ResumeLast { get; init; }

    /// <summary>Disables Codex's cwd filtering while resuming.</summary>
    public bool ResumeAll { get; init; }
}

internal static class Wire
{
    public static string Name(this Agent agent) => agent switch
    {
        Agent.Claude => "claude",
        _ => "codex",
    };

    public static string ToWire(this SandboxMode mode) => mode switch
    {
        SandboxMode.WorkspaceWrite => "workspace-write",
        SandboxMode.DangerFullAccess => "danger-full-access",
        _ => "read-only",
    };

    public static string ToWire(this ApprovalPolicy policy) => policy switch
    {
        ApprovalPolicy.Untrusted => "untrusted",
        ApprovalPolicy.OnRequest => "on-request",
        _ => "never",
    };

    public static string ToWire(this PermissionMode mode) => mode switch
    {
        PermissionMode.AcceptEdits => "acceptEdits",
        PermissionMode.Auto => "auto",
        PermissionMode.BypassPermissions => "bypassPermissions",
        PermissionMode.Manual => "manual",
        PermissionMode.Plan => "plan",
        _ => "dontAsk",
    };
}
