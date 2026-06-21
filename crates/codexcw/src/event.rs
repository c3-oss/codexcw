//! Decoded Codex events and their typed payloads.

use std::time::SystemTime;

/// Top-level `type` field emitted by `codex exec --json`.
///
/// Unknown values are preserved as [`EventKind::Other`] so new Codex event
/// types stream through without being dropped.
#[derive(Clone, Debug, PartialEq, Eq)]
pub enum EventKind {
    /// `thread.started` — Codex created or resumed a thread.
    ThreadStarted,
    /// `turn.started` — a Codex turn began.
    TurnStarted,
    /// `turn.completed` — a Codex turn finished successfully.
    TurnCompleted,
    /// `turn.failed` — a Codex turn failed.
    TurnFailed,
    /// `item.started` — Codex started a streamed item.
    ItemStarted,
    /// `item.completed` — Codex completed a streamed item.
    ItemCompleted,
    /// `error` — a top-level Codex error.
    Error,
    /// An event type not yet modelled by this crate.
    Other(String),
}

impl EventKind {
    /// Returns the wire string for this event type.
    pub fn as_str(&self) -> &str {
        match self {
            EventKind::ThreadStarted => "thread.started",
            EventKind::TurnStarted => "turn.started",
            EventKind::TurnCompleted => "turn.completed",
            EventKind::TurnFailed => "turn.failed",
            EventKind::ItemStarted => "item.started",
            EventKind::ItemCompleted => "item.completed",
            EventKind::Error => "error",
            EventKind::Other(value) => value,
        }
    }

    pub(crate) fn from_wire(value: &str) -> Self {
        match value {
            "thread.started" => EventKind::ThreadStarted,
            "turn.started" => EventKind::TurnStarted,
            "turn.completed" => EventKind::TurnCompleted,
            "turn.failed" => EventKind::TurnFailed,
            "item.started" => EventKind::ItemStarted,
            "item.completed" => EventKind::ItemCompleted,
            "error" => EventKind::Error,
            other => EventKind::Other(other.to_string()),
        }
    }
}

impl std::fmt::Display for EventKind {
    fn fmt(&self, f: &mut std::fmt::Formatter<'_>) -> std::fmt::Result {
        f.write_str(self.as_str())
    }
}

/// The `item.type` field inside `item.started` and `item.completed`.
///
/// Unknown values are preserved as [`ItemKind::Other`].
#[derive(Clone, Debug, PartialEq, Eq)]
pub enum ItemKind {
    /// `agent_message` — assistant text.
    AgentMessage,
    /// `reasoning` — a Codex reasoning item.
    Reasoning,
    /// `command_execution` — a shell command execution.
    CommandExecution,
    /// `file_change` — file edits made by Codex.
    FileChange,
    /// `mcp_tool_call` — an MCP tool call.
    McpToolCall,
    /// `web_search` — a web search operation.
    WebSearch,
    /// `plan_update` — a Codex plan update.
    PlanUpdate,
    /// `error` — an item-scoped Codex error.
    Error,
    /// An item type not yet modelled by this crate.
    Other(String),
}

impl ItemKind {
    /// Returns the wire string for this item type.
    pub fn as_str(&self) -> &str {
        match self {
            ItemKind::AgentMessage => "agent_message",
            ItemKind::Reasoning => "reasoning",
            ItemKind::CommandExecution => "command_execution",
            ItemKind::FileChange => "file_change",
            ItemKind::McpToolCall => "mcp_tool_call",
            ItemKind::WebSearch => "web_search",
            ItemKind::PlanUpdate => "plan_update",
            ItemKind::Error => "error",
            ItemKind::Other(value) => value,
        }
    }

    pub(crate) fn from_wire(value: &str) -> Self {
        match value {
            "agent_message" => ItemKind::AgentMessage,
            "reasoning" => ItemKind::Reasoning,
            "command_execution" => ItemKind::CommandExecution,
            "file_change" => ItemKind::FileChange,
            "mcp_tool_call" => ItemKind::McpToolCall,
            "web_search" => ItemKind::WebSearch,
            "plan_update" => ItemKind::PlanUpdate,
            "error" => ItemKind::Error,
            other => ItemKind::Other(other.to_string()),
        }
    }
}

impl std::fmt::Display for ItemKind {
    fn fmt(&self, f: &mut std::fmt::Formatter<'_>) -> std::fmt::Result {
        f.write_str(self.as_str())
    }
}

/// Token usage reported by `turn.completed`.
#[derive(Clone, Debug, Default, PartialEq, Eq, serde::Deserialize)]
pub struct Usage {
    /// Number of input tokens consumed.
    #[serde(default)]
    pub input_tokens: i64,
    /// Number of cached input tokens.
    #[serde(default)]
    pub cached_input_tokens: i64,
    /// Number of output tokens produced.
    #[serde(default)]
    pub output_tokens: i64,
    /// Number of reasoning output tokens.
    #[serde(default)]
    pub reasoning_output_tokens: i64,
    /// Total tokens, when Codex reports one.
    #[serde(default)]
    pub total_tokens: i64,
}

/// One `file_change` entry inside a `file_change` item.
#[derive(Clone, Debug, Default, PartialEq, Eq, serde::Deserialize)]
pub struct FileChange {
    /// Absolute or workspace-relative file path.
    #[serde(default)]
    pub path: String,
    /// Change kind reported by Codex.
    #[serde(default)]
    pub kind: String,
}

/// A typed projection of a Codex item payload. [`Item::raw`] stays authoritative.
#[derive(Clone, Debug, Default, PartialEq, Eq)]
pub struct Item {
    /// Codex item id.
    pub id: String,
    /// Codex item type.
    pub kind: ItemKind,
    /// Item status when Codex reports one.
    pub status: String,
    /// Original nested item payload as JSON text.
    pub raw: String,
    /// Assistant text for `agent_message` items.
    pub text: String,
    /// Error text for `error` items.
    pub message: String,
    /// Shell command for `command_execution` items.
    pub command: String,
    /// Combined command output reported by Codex.
    pub aggregated_output: String,
    /// Command exit code when available.
    pub exit_code: Option<i32>,
    /// File edits for `file_change` items.
    pub changes: Vec<FileChange>,
}

impl Default for ItemKind {
    fn default() -> Self {
        ItemKind::Other(String::new())
    }
}

/// A top-level Codex error event payload.
#[derive(Clone, Debug, Default, PartialEq, Eq)]
pub struct CodexErrorEvent {
    /// Human-readable Codex error text.
    pub message: String,
    /// Raw nested error payload as JSON text, when present.
    pub raw: String,
}

/// The common error object used by `turn.failed`.
#[derive(Clone, Debug, Default, PartialEq, Eq)]
pub struct ErrorPayload {
    /// Human-readable error text.
    pub message: String,
    /// Raw nested error payload as JSON text.
    pub raw: String,
}

/// The typed payload carried by an [`Event`], selected by its [`EventKind`].
#[derive(Clone, Debug, PartialEq, Eq)]
pub enum EventPayload {
    /// Payload for `thread.started`.
    ThreadStarted {
        /// Codex thread id.
        thread_id: String,
    },
    /// Payload for `turn.started` (no fields).
    TurnStarted,
    /// Payload for `turn.completed`.
    TurnCompleted {
        /// Token usage for the completed turn.
        usage: Usage,
    },
    /// Payload for `turn.failed`.
    TurnFailed {
        /// Error describing the failed turn.
        error: ErrorPayload,
    },
    /// Payload for `item.started`.
    ItemStarted(Item),
    /// Payload for `item.completed`.
    ItemCompleted(Item),
    /// Payload for a top-level `error`.
    Error(CodexErrorEvent),
    /// No typed payload (unknown event type).
    Other,
}

/// One decoded JSONL event from `codex exec`.
#[derive(Clone, Debug, PartialEq, Eq)]
pub struct Event {
    /// Top-level Codex event type.
    pub kind: EventKind,
    /// Wrapper-assigned run id.
    pub run_id: String,
    /// Codex thread id once known.
    pub thread_id: String,
    /// Local time when the line was decoded.
    pub received_at: SystemTime,
    /// Original JSON event payload as text.
    pub raw: String,
    /// Typed payload selected by [`Event::kind`].
    pub payload: EventPayload,
}

impl Event {
    /// Returns the completed item when this is an `item.completed` event.
    pub fn item_completed(&self) -> Option<&Item> {
        match &self.payload {
            EventPayload::ItemCompleted(item) => Some(item),
            _ => None,
        }
    }

    /// Returns the started item when this is an `item.started` event.
    pub fn item_started(&self) -> Option<&Item> {
        match &self.payload {
            EventPayload::ItemStarted(item) => Some(item),
            _ => None,
        }
    }
}
