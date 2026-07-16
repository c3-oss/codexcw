//! Typed errors returned by the runner.

use crate::event::{Event, EventPayload};
use crate::group::GroupResult;

/// An error from a single agent run.
#[derive(Debug, Clone, thiserror::Error)]
pub enum Error {
    /// A non-resume run was started without prompt or stdin input.
    #[error("codexcw: prompt or stdin is required")]
    PromptRequired,

    /// Request validation failed.
    #[error("codexcw: invalid request: {0}")]
    InvalidRequest(String),

    /// The agent process exited with a non-zero status.
    #[error("agent exited with code {code}")]
    Exit {
        /// Process exit code.
        code: i32,
        /// Captured stderr tail.
        stderr: String,
        /// Last decoded event before the process exited.
        last_event: Option<Box<Event>>,
    },

    /// The agent process emitted malformed JSONL.
    #[error("decode agent JSONL line {line}: {message}")]
    Decode {
        /// One-based JSONL line number.
        line: usize,
        /// Malformed line when available.
        raw: Option<Vec<u8>>,
        /// Underlying decode message.
        message: String,
    },

    /// Codex reported a top-level `error` or a failed turn.
    #[error("{message}")]
    Codex {
        /// Formatted Codex error message.
        message: String,
        /// The Codex error event.
        event: Box<Event>,
    },

    /// Claude reported a failed result.
    #[error("{message}")]
    Claude {
        /// Formatted Claude error message.
        message: String,
        /// The Claude error event.
        event: Box<Event>,
    },

    /// An event handler returned an error, cancelling the run.
    #[error("agent event handler failed: {0}")]
    Handler(String),

    /// The run was cancelled.
    #[error("agent run cancelled")]
    Cancelled,

    /// The agent process could not be spawned or its I/O failed.
    #[error("agent process error: {0}")]
    Process(String),
}

impl Error {
    pub(crate) fn invalid(message: impl Into<String>) -> Self {
        Error::InvalidRequest(message.into())
    }

    pub(crate) fn codex_from_event(event: &Event) -> Self {
        Error::Codex {
            message: codex_message(event),
            event: Box::new(event.clone()),
        }
    }

    pub(crate) fn claude_from_event(event: &Event) -> Self {
        Error::Claude {
            message: claude_message(event),
            event: Box::new(event.clone()),
        }
    }
}

fn codex_message(event: &Event) -> String {
    match &event.payload {
        EventPayload::Error(err) if !err.message.is_empty() => {
            format!("codex error: {}", err.message)
        }
        EventPayload::TurnFailed { error, .. } if !error.message.is_empty() => {
            format!("codex turn failed: {}", error.message)
        }
        _ => "codex error event".to_string(),
    }
}

fn claude_message(event: &Event) -> String {
    match &event.payload {
        EventPayload::Error(err) if !err.message.is_empty() => {
            format!("claude error: {}", err.message)
        }
        EventPayload::TurnFailed { error, .. } if !error.message.is_empty() => {
            format!("claude turn failed: {}", error.message)
        }
        _ => "claude error event".to_string(),
    }
}

/// One or more runs failed during [`crate::Group::wait`].
#[derive(Debug, Clone)]
pub struct GroupError {
    /// Every run result, including the failed ones.
    pub results: Vec<GroupResult>,
}

impl std::fmt::Display for GroupError {
    fn fmt(&self, f: &mut std::fmt::Formatter<'_>) -> std::fmt::Result {
        let failed = self.results.iter().filter(|r| r.error.is_some()).count();
        write!(f, "{failed} agent run(s) failed")
    }
}

impl std::error::Error for GroupError {}
