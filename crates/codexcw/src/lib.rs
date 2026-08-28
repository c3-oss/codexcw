//! Run agent CLIs non-interactively.
//!
//! `codexcw` runs Codex, Claude Code, or Grok Build non-interactively: it spawns the
//! selected agent CLI, decodes the JSONL event stream, and exposes each run as
//! async streams, callbacks, results, and typed errors. The Codex agent (the
//! default) wraps `codex exec --json` with automation-friendly defaults: JSONL
//! streaming, ephemeral sessions, read-only sandbox, approval policy `never`,
//! color disabled, and the Git repository check skipped. Selecting
//! [`Agent::Claude`] on the builder wraps Claude Code
//! (`claude -p --output-format stream-json`), normalizing its events into the
//! same [`Event`] model with the original agent JSON kept in `raw`.
//! Selecting [`Agent::Grok`] wraps Grok Build with
//! `streaming-messages-json` and the same normalized model.
//!
//! The selected agent's executable must be available on `PATH` and
//! authenticated and new enough for the wrapped JSONL mode.
//!
//! # Example
//!
//! ```no_run
//! use codexcw::{Request, Runner};
//!
//! # async fn run() -> Result<(), codexcw::Error> {
//! let runner = Runner::new();
//! let result = runner.run(Request::new("diga oi")).await?;
//! println!("{}", result.final_message);
//! # Ok(())
//! # }
//! ```
//!
//! # Streaming
//!
//! ```no_run
//! use codexcw::{EventPayload, ItemKind, Request, Runner};
//! use tokio_stream::StreamExt;
//!
//! # async fn run() -> Result<(), codexcw::Error> {
//! let runner = Runner::new();
//! let mut session = runner.start(Request::new("resuma este repo")).await?;
//! let mut events = session.events();
//! while let Some(event) = events.next().await {
//!     if let EventPayload::ItemCompleted(item) = &event.payload {
//!         if item.kind == ItemKind::AgentMessage {
//!             println!("{}", item.text);
//!         }
//!     }
//! }
//! let result = session.wait().await?;
//! println!("{}", result.final_message);
//! # Ok(())
//! # }
//! ```

#![warn(missing_docs)]

pub(crate) const DEFAULT_EXECUTABLE: &str = "codex";

mod account_usage;
mod args;
mod claude;
mod claude_account_usage;
mod decoder;
mod error;
mod event;
mod grok;
mod group;
mod request;
mod runner;
mod session;
mod tail;

pub use account_usage::{
    get_account_usage, AccountCredits, AccountRateLimitWindow, AccountRateLimits,
    AccountSpendLimit, AccountTokenUsage, AccountTokenUsageDailyBucket, AccountTokenUsageSummary,
    AccountUsage, AccountUsageAccount, AccountUsageRequest,
};
pub use claude_account_usage::{
    get_claude_account_usage, ClaudeAccountUsage, ClaudeAccountUsageRequest,
    ClaudeAccountUsageWindow,
};
pub use error::{Error, GroupError};
pub use event::{
    CodexErrorEvent, ErrorPayload, Event, EventKind, EventPayload, FileChange, Item, ItemKind,
    ModelUsage, Usage,
};
pub use group::{Group, GroupResult, ManyOptions, RunEvent};
pub use request::{
    claude_model, permission_mode, ApprovalPolicy, ConfigOverride, Request, SandboxMode,
};
pub use runner::{handler, Agent, Handler, RunOptions, Runner, RunnerBuilder};
pub use session::{RunResult, Session};
