//! A running `codex exec` process and its completion summary.

use std::sync::{Arc, Mutex};
use std::time::SystemTime;

use tokio::sync::mpsc;
use tokio::sync::Notify;
use tokio_stream::wrappers::ReceiverStream;
use tokio_util::sync::CancellationToken;

use crate::error::Error;
use crate::event::{Event, Usage};

/// Summary of a completed `codex exec` invocation.
#[derive(Clone, Debug)]
pub struct RunResult {
    /// Wrapper-assigned run id.
    pub run_id: String,
    /// Codex thread id once known.
    pub thread_id: String,
    /// Last completed `agent_message` text.
    pub final_message: String,
    /// Token usage from the last `turn.completed`.
    pub usage: Usage,
    /// Every decoded event retained by the run.
    pub events: Vec<Event>,
    /// Captured stderr tail.
    pub stderr: String,
    /// Local time when collection started.
    pub started_at: SystemTime,
    /// Local time when the process finished.
    pub finished_at: SystemTime,
}

pub(crate) struct RunOutcome {
    pub report: RunResult,
    pub error: Option<Error>,
}

/// A completion slot that any number of waiters can await.
pub(crate) struct Latch<T> {
    state: Mutex<Option<Arc<T>>>,
    notify: Notify,
}

impl<T> Latch<T> {
    pub(crate) fn new() -> Self {
        Latch {
            state: Mutex::new(None),
            notify: Notify::new(),
        }
    }

    pub(crate) fn set(&self, value: T) {
        *self.state.lock().expect("latch poisoned") = Some(Arc::new(value));
        self.notify.notify_waiters();
    }

    pub(crate) async fn wait(&self) -> Arc<T> {
        loop {
            let notified = self.notify.notified();
            if let Some(value) = self.state.lock().expect("latch poisoned").as_ref() {
                return value.clone();
            }
            notified.await;
        }
    }
}

pub(crate) type Completion = Latch<RunOutcome>;

/// One running `codex exec` process.
pub struct Session {
    pub(crate) id: String,
    pub(crate) rx: Option<mpsc::Receiver<Event>>,
    pub(crate) thread_id: Arc<Mutex<String>>,
    pub(crate) cancel: CancellationToken,
    pub(crate) completion: Arc<Completion>,
}

impl Session {
    /// Returns the wrapper-assigned run id.
    pub fn id(&self) -> &str {
        &self.id
    }

    /// Returns the Codex thread id once `thread.started` has arrived.
    pub fn thread_id(&self) -> String {
        self.thread_id.lock().expect("thread id poisoned").clone()
    }

    /// Stops the child process.
    pub fn cancel(&self) {
        self.cancel.cancel();
    }

    /// Awaits the next decoded event, or `None` once the stream closes.
    ///
    /// Mutually exclusive with [`Session::events`]: whichever is used first
    /// takes ownership of the event stream.
    pub async fn next_event(&mut self) -> Option<Event> {
        match self.rx.as_mut() {
            Some(rx) => rx.recv().await,
            None => None,
        }
    }

    /// Returns the decoded events as an async stream, consuming the channel.
    pub fn events(&mut self) -> ReceiverStream<Event> {
        let rx = self
            .rx
            .take()
            .expect("event stream already taken (events/next_event called twice)");
        ReceiverStream::new(rx)
    }

    /// Waits for the process to exit and returns its result. Idempotent.
    pub async fn wait(&self) -> Result<RunResult, Error> {
        let outcome = self.completion.wait().await;
        match &outcome.error {
            Some(error) => Err(error.clone()),
            None => Ok(outcome.report.clone()),
        }
    }

    /// Waits and returns the report together with any terminal error.
    ///
    /// Unlike [`Session::wait`], the report is always returned even when the
    /// run failed — mirroring the Go API's `(result, err)` pair.
    pub async fn join(&self) -> (RunResult, Option<Error>) {
        let outcome = self.completion.wait().await;
        (outcome.report.clone(), outcome.error.clone())
    }
}
