//! The [`Request`] describing one `codex exec` invocation and its enums.

/// Sandbox policy passed to `codex exec`.
#[derive(Clone, Copy, Debug, PartialEq, Eq)]
pub enum SandboxMode {
    /// Inspect files without write access.
    ReadOnly,
    /// Write inside the configured workspace.
    WorkspaceWrite,
    /// Remove sandbox filesystem restrictions.
    DangerFullAccess,
}

impl SandboxMode {
    /// Returns the wire string for this sandbox mode.
    pub fn as_str(&self) -> &'static str {
        match self {
            SandboxMode::ReadOnly => "read-only",
            SandboxMode::WorkspaceWrite => "workspace-write",
            SandboxMode::DangerFullAccess => "danger-full-access",
        }
    }
}

impl std::fmt::Display for SandboxMode {
    fn fmt(&self, f: &mut std::fmt::Formatter<'_>) -> std::fmt::Result {
        f.write_str(self.as_str())
    }
}

/// Approval policy passed to Codex through a config override.
#[derive(Clone, Copy, Debug, PartialEq, Eq)]
pub enum ApprovalPolicy {
    /// Ask before commands outside Codex's trusted set.
    Untrusted,
    /// Work in the sandbox and request approval on demand.
    OnRequest,
    /// Never prompt for interactive approval.
    Never,
}

impl ApprovalPolicy {
    /// Returns the wire string for this approval policy.
    pub fn as_str(&self) -> &'static str {
        match self {
            ApprovalPolicy::Untrusted => "untrusted",
            ApprovalPolicy::OnRequest => "on-request",
            ApprovalPolicy::Never => "never",
        }
    }
}

impl std::fmt::Display for ApprovalPolicy {
    fn fmt(&self, f: &mut std::fmt::Formatter<'_>) -> std::fmt::Result {
        f.write_str(self.as_str())
    }
}

/// Model aliases accepted by the claude agent's `--model` flag.
pub mod claude_model {
    /// The latest Claude Haiku model.
    pub const HAIKU: &str = "haiku";
    /// The latest Claude Sonnet model.
    pub const SONNET: &str = "sonnet";
    /// The latest Claude Opus model.
    pub const OPUS: &str = "opus";
}

/// Permission modes accepted by the claude agent's `--permission-mode` flag.
pub mod permission_mode {
    /// Auto-approve file edits inside the workspace.
    pub const ACCEPT_EDITS: &str = "acceptEdits";
    /// Skip all permission checks.
    pub const BYPASS_PERMISSIONS: &str = "bypassPermissions";
    /// Keep Claude in read-only planning mode.
    pub const PLAN: &str = "plan";
    /// Deny any action that would prompt for approval.
    pub const DONT_ASK: &str = "dontAsk";
}

/// One `-c key=value` config override.
#[derive(Clone, Debug, Default, PartialEq, Eq)]
pub struct ConfigOverride {
    /// Config path before the equals sign.
    pub key: String,
    /// Config value after the equals sign.
    pub value: String,
}

impl ConfigOverride {
    /// Builds an override from a key and value.
    pub fn new(key: impl Into<String>, value: impl Into<String>) -> Self {
        ConfigOverride {
            key: key.into(),
            value: value.into(),
        }
    }

    /// Returns the exact `key=value` argument expected by `codex -c`.
    pub fn as_arg(&self) -> String {
        if self.key.is_empty() {
            self.value.clone()
        } else {
            format!("{}={}", self.key, self.value)
        }
    }
}

/// Describes one `codex exec` invocation.
///
/// All fields are optional except prompt or stdin. Build with
/// [`Request::new`] and the chaining setters, or with a struct literal and
/// `..Default::default()`.
#[derive(Clone, Debug, Default)]
pub struct Request {
    /// User instruction sent to Codex.
    pub prompt: String,
    /// Additional prompt input, or extra context when `prompt` is set.
    pub stdin: Option<Vec<u8>>,
    /// Working directory passed as `--cd`.
    pub dir: Option<String>,
    /// Additional directories Codex may access.
    pub add_dirs: Vec<String>,
    /// Images attached to the initial prompt.
    pub images: Vec<String>,
    /// Model override for this run.
    pub model: Option<String>,
    /// Codex config profile.
    pub profile: Option<String>,
    /// Sandbox policy override (codex agent only).
    pub sandbox: Option<SandboxMode>,
    /// Approval policy override (codex agent only).
    pub approval: Option<ApprovalPolicy>,
    /// Claude permission mode (claude agent only). See [`permission_mode`].
    pub permission_mode: Option<String>,
    /// Tool patterns Claude may use without prompting (claude agent only).
    pub allowed_tools: Vec<String>,
    /// Tool patterns denied to Claude (claude agent only).
    pub disallowed_tools: Vec<String>,
    /// Raw `-c` config overrides.
    pub config: Vec<ConfigOverride>,
    /// Feature flags passed with `--enable`.
    pub enable: Vec<String>,
    /// Feature flags passed with `--disable`.
    pub disable: Vec<String>,
    /// Reject unrecognized config fields.
    pub strict_config: bool,
    /// Keep Codex rollout files on disk.
    pub persistent: bool,
    /// Skip `CODEX_HOME/config.toml`.
    pub ignore_user_config: bool,
    /// Skip user and project execpolicy `.rules` files.
    pub ignore_rules: bool,
    /// Let Codex enforce its Git repository check.
    pub require_git_repo: bool,
    /// Path to a JSON Schema file for the final response.
    pub output_schema_path: Option<String>,
    /// Inline JSON Schema, written to a temporary file for the run.
    pub output_schema: Option<Vec<u8>>,
    /// Ask Codex to write the final message to this file.
    pub output_last_message_path: Option<String>,
    /// Pass Codex's full sandbox bypass flag.
    pub dangerously_bypass_sandbox: bool,
    /// Run enabled hooks without persisted trust.
    pub dangerously_bypass_hooks: bool,
    /// Environment variables for the Codex child process.
    pub env: Vec<(String, String)>,
    /// Resume a specific Codex thread id.
    pub resume_id: Option<String>,
    /// Resume the most recent Codex thread.
    pub resume_last: bool,
    /// Disable Codex's cwd filtering while resuming.
    pub resume_all: bool,
}

impl Request {
    /// Creates a request with the given prompt and otherwise-default fields.
    pub fn new(prompt: impl Into<String>) -> Self {
        Request {
            prompt: prompt.into(),
            ..Default::default()
        }
    }

    /// Sets the stdin payload.
    pub fn stdin(mut self, stdin: impl Into<Vec<u8>>) -> Self {
        self.stdin = Some(stdin.into());
        self
    }

    /// Sets the working directory (`--cd`).
    pub fn dir(mut self, dir: impl Into<String>) -> Self {
        self.dir = Some(dir.into());
        self
    }

    /// Sets the model override.
    pub fn model(mut self, model: impl Into<String>) -> Self {
        self.model = Some(model.into());
        self
    }

    /// Sets the sandbox policy.
    pub fn sandbox(mut self, sandbox: SandboxMode) -> Self {
        self.sandbox = Some(sandbox);
        self
    }

    /// Sets the approval policy.
    pub fn approval(mut self, approval: ApprovalPolicy) -> Self {
        self.approval = Some(approval);
        self
    }

    pub(crate) fn is_resume(&self) -> bool {
        self.resume_id.as_deref().is_some_and(|id| !id.is_empty()) || self.resume_last
    }
}
