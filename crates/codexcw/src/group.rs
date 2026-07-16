//! Batch execution of agent processes with bounded concurrency.

use std::sync::Arc;

use tokio::sync::{mpsc, Semaphore};
use tokio::task::JoinSet;
use tokio_stream::wrappers::ReceiverStream;
use tokio_util::sync::CancellationToken;

use crate::error::{Error, GroupError};
use crate::event::Event;
use crate::request::Request;
use crate::runner::{RunOptions, Runner};
use crate::session::{Latch, RunResult};

const DEFAULT_MAX_CONCURRENT: usize = 4;

/// One event multiplexed from [`Runner::run_many`].
#[derive(Clone, Debug)]
pub struct RunEvent {
    /// Wrapper-assigned run id of the originating run.
    pub run_id: String,
    /// Request index in the `run_many` input slice.
    pub index: usize,
    /// The decoded agent event.
    pub event: Event,
}

/// The result for one request passed to [`Runner::run_many`].
#[derive(Clone, Debug)]
pub struct GroupResult {
    /// Request index in the `run_many` input slice.
    pub index: usize,
    /// Wrapper-assigned run id.
    pub run_id: String,
    /// Run summary when the run started.
    pub result: Option<RunResult>,
    /// Error when the run failed.
    pub error: Option<Error>,
}

/// Options for [`Runner::run_many`].
pub struct ManyOptions {
    /// Maximum number of concurrent processes (default 4).
    pub max_concurrent: usize,
    /// Multiplexed event channel buffer; falls back to the runner default.
    pub event_buffer: Option<usize>,
    /// Per-run options applied to every run in the group.
    pub run: RunOptions,
}

impl Default for ManyOptions {
    fn default() -> Self {
        ManyOptions {
            max_concurrent: DEFAULT_MAX_CONCURRENT,
            event_buffer: None,
            run: RunOptions::default(),
        }
    }
}

/// A batch of running agent processes.
pub struct Group {
    rx: Option<mpsc::Receiver<RunEvent>>,
    cancel: CancellationToken,
    completion: Arc<Latch<Vec<GroupResult>>>,
}

impl Group {
    /// Awaits the next multiplexed event, or `None` once every run finishes.
    pub async fn next_event(&mut self) -> Option<RunEvent> {
        match self.rx.as_mut() {
            Some(rx) => rx.recv().await,
            None => None,
        }
    }

    /// Returns the multiplexed events as an async stream.
    pub fn events(&mut self) -> ReceiverStream<RunEvent> {
        let rx = self
            .rx
            .take()
            .expect("event stream already taken (events/next_event called twice)");
        ReceiverStream::new(rx)
    }

    /// Stops all active and pending runs.
    pub fn cancel(&self) {
        self.cancel.cancel();
    }

    /// Returns every run result. Fails with [`GroupError`] if any run failed.
    pub async fn wait(&self) -> Result<Vec<GroupResult>, GroupError> {
        let results = self.completion.wait().await;
        let results = (*results).clone();
        if results.iter().any(|r| r.error.is_some()) {
            Err(GroupError { results })
        } else {
            Ok(results)
        }
    }
}

impl Runner {
    /// Starts `n` agent processes with bounded concurrency.
    pub async fn run_many(&self, reqs: Vec<Request>, opts: ManyOptions) -> Group {
        let event_buffer = opts.event_buffer.unwrap_or(self.event_buffer()).max(1);
        let max_concurrent = opts.max_concurrent.max(1);
        let (tx, rx) = mpsc::channel(event_buffer);
        let cancel = CancellationToken::new();
        let completion = Arc::new(Latch::new());

        let runner = self.clone();
        let task_cancel = cancel.clone();
        let task_completion = completion.clone();
        let run_opts = opts.run;
        tokio::spawn(async move {
            run_many_inner(
                runner,
                reqs,
                max_concurrent,
                run_opts,
                tx,
                task_cancel,
                task_completion,
            )
            .await;
        });

        Group {
            rx: Some(rx),
            cancel,
            completion,
        }
    }
}

async fn run_many_inner(
    runner: Runner,
    reqs: Vec<Request>,
    max_concurrent: usize,
    run_opts: RunOptions,
    tx: mpsc::Sender<RunEvent>,
    cancel: CancellationToken,
    completion: Arc<Latch<Vec<GroupResult>>>,
) {
    let total = reqs.len();
    let mut results: Vec<Option<GroupResult>> = (0..total).map(|_| None).collect();
    let semaphore = Arc::new(Semaphore::new(max_concurrent));
    let mut set: JoinSet<(usize, GroupResult)> = JoinSet::new();

    for (index, req) in reqs.into_iter().enumerate() {
        if cancel.is_cancelled() {
            results[index] = Some(GroupResult {
                index,
                run_id: String::new(),
                result: None,
                error: Some(Error::Cancelled),
            });
            continue;
        }

        let permit = semaphore
            .clone()
            .acquire_owned()
            .await
            .expect("semaphore closed");
        let runner = runner.clone();
        let tx = tx.clone();
        let cancel = cancel.clone();
        let run_opts = run_opts.clone();
        set.spawn(async move {
            let _permit = permit;
            let result = run_one(runner, index, req, run_opts, tx, cancel).await;
            (index, result)
        });
    }

    drop(tx);

    while let Some(joined) = set.join_next().await {
        if let Ok((index, result)) = joined {
            results[index] = Some(result);
        }
    }

    let results: Vec<GroupResult> = results
        .into_iter()
        .enumerate()
        .map(|(index, slot)| {
            slot.unwrap_or(GroupResult {
                index,
                run_id: String::new(),
                result: None,
                error: Some(Error::Cancelled),
            })
        })
        .collect();

    completion.set(results);
}

async fn run_one(
    runner: Runner,
    index: usize,
    req: Request,
    run_opts: RunOptions,
    tx: mpsc::Sender<RunEvent>,
    cancel: CancellationToken,
) -> GroupResult {
    let mut session = match runner.start_opts(req, run_opts).await {
        Ok(session) => session,
        Err(error) => {
            return GroupResult {
                index,
                run_id: String::new(),
                result: None,
                error: Some(error),
            }
        }
    };
    let run_id = session.id().to_string();

    let mut cancelled = false;
    loop {
        tokio::select! {
            biased;
            _ = cancel.cancelled(), if !cancelled => {
                cancelled = true;
                session.cancel();
            }
            event = session.next_event() => {
                match event {
                    Some(event) => {
                        let _ = tx
                            .send(RunEvent {
                                run_id: run_id.clone(),
                                index,
                                event,
                            })
                            .await;
                    }
                    None => break,
                }
            }
        }
    }

    match session.wait().await {
        Ok(report) => GroupResult {
            index,
            run_id: report.run_id.clone(),
            result: Some(report),
            error: None,
        },
        Err(error) => GroupResult {
            index,
            run_id,
            result: None,
            error: Some(error),
        },
    }
}
