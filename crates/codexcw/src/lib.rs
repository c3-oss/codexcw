//! Run the Codex CLI non-interactively.
//!
//! `codexcw` wraps `codex exec --json`: it spawns Codex processes, decodes the
//! JSONL event stream, and exposes each run as async streams, callbacks,
//! results, and typed errors. Defaults are automation-friendly: JSONL
//! streaming, ephemeral sessions, read-only sandbox, approval policy `never`,
//! color disabled, and the Git repository check skipped.
//!
//! The `codex` executable must be available on `PATH`, authenticated, and new
//! enough to support `codex exec --json`.
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
mod decoder;
mod error;
mod event;
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
pub use error::{Error, GroupError};
pub use event::{
    CodexErrorEvent, ErrorPayload, Event, EventKind, EventPayload, FileChange, Item, ItemKind,
    Usage,
};
pub use group::{Group, GroupResult, ManyOptions, RunEvent};
pub use request::{ApprovalPolicy, ConfigOverride, Request, SandboxMode};
pub use runner::{handler, Handler, RunOptions, Runner, RunnerBuilder};
pub use session::{RunResult, Session};
