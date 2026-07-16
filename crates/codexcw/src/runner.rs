//! The [`Runner`] that spawns agent processes and decodes their output.

use std::future::Future;
use std::pin::Pin;
use std::process::{ExitStatus, Stdio};
use std::sync::atomic::{AtomicU64, Ordering};
use std::sync::{Arc, Mutex};
use std::time::{SystemTime, UNIX_EPOCH};

use tokio::io::{AsyncReadExt, AsyncWriteExt};
use tokio::process::{Child, ChildStderr, ChildStdout, Command};
use tokio::sync::mpsc;
use tokio_stream::StreamExt;
use tokio_util::codec::{FramedRead, LinesCodec};
use tokio_util::sync::CancellationToken;

use crate::args::prepare;
use crate::claude::{prepare_claude, ClaudeDecoder};
use crate::decoder::decode_event;
use crate::error::Error;
use crate::event::{Event, EventPayload, ItemKind, Usage};
use crate::request::{ApprovalPolicy, Request, SandboxMode};
use crate::session::{Completion, RunOutcome, RunResult, Session};
use crate::tail::TailBuffer;

const DEFAULT_EVENT_BUFFER: usize = 1024;
pub(crate) const DEFAULT_STDERR_LIMIT: usize = 1 << 20;
const DEFAULT_SCAN_MAX_BYTES: usize = 64 << 20;

static RUN_COUNTER: AtomicU64 = AtomicU64::new(0);

/// The CLI wrapped by a [`Runner`].
#[derive(Clone, Copy, Debug, Default, PartialEq, Eq)]
pub enum Agent {
    /// `codex exec --json` (the default).
    #[default]
    Codex,
    /// `claude -p --output-format stream-json`.
    Claude,
}

impl Agent {
    /// Returns the agent's default executable name.
    pub fn as_str(&self) -> &'static str {
        match self {
            Agent::Codex => "codex",
            Agent::Claude => "claude",
        }
    }
}

impl std::fmt::Display for Agent {
    fn fmt(&self, f: &mut std::fmt::Formatter<'_>) -> std::fmt::Result {
        f.write_str(self.as_str())
    }
}

/// Turns one JSONL line into zero or more decoded events.
enum EventDecoder {
    Codex,
    Claude(ClaudeDecoder),
}

impl EventDecoder {
    fn for_agent(agent: Agent) -> Self {
        match agent {
            Agent::Codex => EventDecoder::Codex,
            Agent::Claude => EventDecoder::Claude(ClaudeDecoder::default()),
        }
    }

    fn decode(
        &mut self,
        line: &[u8],
        run_id: &str,
        thread_id: &str,
        now: std::time::SystemTime,
    ) -> Result<Vec<Event>, String> {
        match self {
            EventDecoder::Codex => decode_event(line, run_id, thread_id, now).map(|e| vec![e]),
            EventDecoder::Claude(decoder) => decoder.decode(line, run_id, thread_id, now),
        }
    }
}

/// An async callback invoked for each streamed event. Returning `Err` cancels
/// the run with a [`Error::Handler`].
pub type Handler =
    Arc<dyn Fn(Event) -> Pin<Box<dyn Future<Output = Result<(), String>> + Send>> + Send + Sync>;

/// Wraps a closure into a [`Handler`].
pub fn handler<F, Fut>(f: F) -> Handler
where
    F: Fn(Event) -> Fut + Send + Sync + 'static,
    Fut: Future<Output = Result<(), String>> + Send + 'static,
{
    Arc::new(move |event| Box::pin(f(event)))
}

/// Per-run options applied to [`Runner::run_opts`] and [`Runner::start_opts`].
#[derive(Default, Clone)]
pub struct RunOptions {
    /// Event handler invoked for every decoded event.
    pub handler: Option<Handler>,
}

impl RunOptions {
    /// Returns options with the given handler set.
    pub fn with_handler(handler: Handler) -> Self {
        RunOptions {
            handler: Some(handler),
        }
    }
}

struct RunnerInner {
    agent: Agent,
    executable: String,
    env: Vec<(String, String)>,
    event_buffer: usize,
    stderr_limit: usize,
    scan_max_bytes: usize,
    default_sandbox: SandboxMode,
    default_approval: ApprovalPolicy,
}

/// Starts agent processes with safe automation defaults.
#[derive(Clone)]
pub struct Runner {
    inner: Arc<RunnerInner>,
}

impl Default for Runner {
    fn default() -> Self {
        Runner::new()
    }
}

impl Runner {
    /// Creates a runner with safe automation defaults.
    pub fn new() -> Self {
        RunnerBuilder::default().build()
    }

    /// Returns a builder for customizing the runner.
    pub fn builder() -> RunnerBuilder {
        RunnerBuilder::default()
    }

    #[cfg(test)]
    pub(crate) fn executable(&self) -> &str {
        &self.inner.executable
    }

    #[cfg(test)]
    pub(crate) fn default_sandbox(&self) -> SandboxMode {
        self.inner.default_sandbox
    }

    #[cfg(test)]
    pub(crate) fn default_approval(&self) -> ApprovalPolicy {
        self.inner.default_approval
    }

    pub(crate) fn event_buffer(&self) -> usize {
        self.inner.event_buffer
    }

    /// Launches one agent process and returns immediately.
    pub async fn start(&self, req: Request) -> Result<Session, Error> {
        self.start_opts(req, RunOptions::default()).await
    }

    /// Launches one `codex exec` process with per-run options.
    pub async fn start_opts(&self, req: Request, opts: RunOptions) -> Result<Session, Error> {
        let prepared = match self.inner.agent {
            Agent::Claude => prepare_claude(&req)?,
            Agent::Codex => prepare(
                &req,
                self.inner.default_sandbox,
                self.inner.default_approval,
            )?,
        };

        let mut command = Command::new(&self.inner.executable);
        command.args(&prepared.args);
        if let Some(dir) = &prepared.current_dir {
            command.current_dir(dir);
        }
        command.envs(self.inner.env.iter().map(|(k, v)| (k.clone(), v.clone())));
        command.envs(req.env.iter().map(|(k, v)| (k.clone(), v.clone())));
        command
            .stdin(Stdio::piped())
            .stdout(Stdio::piped())
            .stderr(Stdio::piped())
            .kill_on_drop(true);

        let mut child = command
            .spawn()
            .map_err(|err| Error::Process(err.to_string()))?;

        let stdout = child.stdout.take().expect("stdout is piped");
        let stderr = child.stderr.take().expect("stderr is piped");
        if let Some(mut stdin) = child.stdin.take() {
            let data = prepared.stdin.clone();
            tokio::spawn(async move {
                let _ = stdin.write_all(&data).await;
                let _ = stdin.shutdown().await;
            });
        }

        let run_id = new_run_id();
        let (event_tx, event_rx) = mpsc::unbounded_channel();
        let (tx, rx) = mpsc::channel(self.inner.event_buffer.max(1));
        let cancel = CancellationToken::new();
        let thread_id = Arc::new(Mutex::new(String::new()));
        let completion = Arc::new(Completion::new());

        tokio::spawn(forward_events(event_rx, tx));

        let tail = Arc::new(TailBuffer::new(self.inner.stderr_limit));
        let stderr_task = tokio::spawn(drain_stderr(stderr, tail.clone()));

        tokio::spawn(collect(CollectCtx {
            child,
            stdout,
            stderr_task,
            tail,
            event_tx,
            cancel: cancel.clone(),
            completion: completion.clone(),
            handler: opts.handler,
            run_id: run_id.clone(),
            thread_id: thread_id.clone(),
            scan_max_bytes: self.inner.scan_max_bytes,
            schema_temp: prepared.schema_temp,
            decoder: EventDecoder::for_agent(self.inner.agent),
            agent: self.inner.agent,
        }));

        Ok(Session {
            id: run_id,
            rx: Some(rx),
            thread_id,
            cancel,
            completion,
        })
    }

    /// Starts one process, drains its event stream, and waits for completion.
    pub async fn run(&self, req: Request) -> Result<RunResult, Error> {
        self.run_opts(req, RunOptions::default()).await
    }

    /// Starts one process with per-run options, drains it, and waits.
    pub async fn run_opts(&self, req: Request, opts: RunOptions) -> Result<RunResult, Error> {
        let mut session = self.start_opts(req, opts).await?;
        while session.next_event().await.is_some() {}
        session.wait().await
    }
}

/// Builds a [`Runner`].
pub struct RunnerBuilder {
    agent: Agent,
    executable: Option<String>,
    env: Vec<(String, String)>,
    event_buffer: usize,
    stderr_limit: usize,
    scan_max_bytes: usize,
    default_sandbox: SandboxMode,
    default_approval: ApprovalPolicy,
}

impl Default for RunnerBuilder {
    fn default() -> Self {
        RunnerBuilder {
            agent: Agent::Codex,
            executable: None,
            env: Vec::new(),
            event_buffer: DEFAULT_EVENT_BUFFER,
            stderr_limit: DEFAULT_STDERR_LIMIT,
            scan_max_bytes: DEFAULT_SCAN_MAX_BYTES,
            default_sandbox: SandboxMode::ReadOnly,
            default_approval: ApprovalPolicy::Never,
        }
    }
}

impl RunnerBuilder {
    /// Selects the wrapped agent CLI. The default is [`Agent::Codex`].
    pub fn agent(mut self, agent: Agent) -> Self {
        self.agent = agent;
        self
    }

    /// Overrides the agent executable path. The primary test seam.
    pub fn executable(mut self, path: impl Into<String>) -> Self {
        let path = path.into();
        if !path.is_empty() {
            self.executable = Some(path);
        }
        self
    }

    /// Appends an environment variable for every child process.
    pub fn env(mut self, key: impl Into<String>, value: impl Into<String>) -> Self {
        self.env.push((key.into(), value.into()));
        self
    }

    /// Sets the per-session event channel buffer.
    pub fn event_buffer(mut self, n: usize) -> Self {
        if n > 0 {
            self.event_buffer = n;
        }
        self
    }

    /// Sets the captured stderr tail size in bytes.
    pub fn stderr_limit(mut self, n: usize) -> Self {
        self.stderr_limit = n;
        self
    }

    /// Sets the maximum accepted JSONL line length in bytes.
    pub fn scan_max_bytes(mut self, n: usize) -> Self {
        if n > 0 {
            self.scan_max_bytes = n;
        }
        self
    }

    /// Sets the default sandbox mode.
    pub fn default_sandbox(mut self, sandbox: SandboxMode) -> Self {
        self.default_sandbox = sandbox;
        self
    }

    /// Sets the default approval policy.
    pub fn default_approval(mut self, approval: ApprovalPolicy) -> Self {
        self.default_approval = approval;
        self
    }

    /// Builds the runner.
    pub fn build(self) -> Runner {
        Runner {
            inner: Arc::new(RunnerInner {
                agent: self.agent,
                executable: self
                    .executable
                    .unwrap_or_else(|| self.agent.as_str().to_string()),
                env: self.env,
                event_buffer: self.event_buffer,
                stderr_limit: self.stderr_limit,
                scan_max_bytes: self.scan_max_bytes,
                default_sandbox: self.default_sandbox,
                default_approval: self.default_approval,
            }),
        }
    }
}

struct CollectCtx {
    child: Child,
    stdout: ChildStdout,
    stderr_task: tokio::task::JoinHandle<()>,
    tail: Arc<TailBuffer>,
    event_tx: mpsc::UnboundedSender<Event>,
    cancel: CancellationToken,
    completion: Arc<Completion>,
    handler: Option<Handler>,
    run_id: String,
    thread_id: Arc<Mutex<String>>,
    scan_max_bytes: usize,
    schema_temp: Option<tempfile::NamedTempFile>,
    decoder: EventDecoder,
    agent: Agent,
}

pub(crate) async fn drain_stderr(mut stderr: ChildStderr, tail: Arc<TailBuffer>) {
    let mut buf = [0u8; 8192];
    loop {
        match stderr.read(&mut buf).await {
            Ok(0) | Err(_) => break,
            Ok(n) => tail.write(&buf[..n]),
        }
    }
}

async fn forward_events(
    mut source: mpsc::UnboundedReceiver<Event>,
    destination: mpsc::Sender<Event>,
) {
    while let Some(event) = source.recv().await {
        if destination.send(event).await.is_err() {
            break;
        }
    }
}

async fn collect(ctx: CollectCtx) {
    let CollectCtx {
        mut child,
        stdout,
        stderr_task,
        tail,
        event_tx,
        cancel,
        completion,
        handler,
        run_id,
        thread_id,
        scan_max_bytes,
        schema_temp,
        mut decoder,
        agent,
    } = ctx;

    let started_at = SystemTime::now();
    let mut events: Vec<Event> = Vec::new();
    let mut last_event: Option<Event> = None;
    let mut final_message = String::new();
    let mut usage = Usage::default();
    let mut current_thread = String::new();
    let mut run_err: Option<Error> = None;

    let mut lines = FramedRead::new(stdout, LinesCodec::new_with_max_length(scan_max_bytes));
    let mut line_no = 0usize;

    'lines: loop {
        let next = tokio::select! {
            biased;
            _ = cancel.cancelled() => None,
            line = lines.next() => line,
        };
        let Some(line_result) = next else {
            if cancel.is_cancelled() && run_err.is_none() {
                run_err = Some(Error::Cancelled);
            }
            break;
        };

        let raw_line = match line_result {
            Ok(line) => line,
            Err(err) => {
                line_no += 1;
                run_err = Some(Error::Decode {
                    line: line_no,
                    raw: None,
                    message: err.to_string(),
                });
                cancel.cancel();
                break;
            }
        };
        line_no += 1;
        let trimmed = raw_line.trim();
        if trimmed.is_empty() {
            continue;
        }

        let decoded = match decoder.decode(
            trimmed.as_bytes(),
            &run_id,
            &current_thread,
            SystemTime::now(),
        ) {
            Ok(events) => events,
            Err(message) => {
                run_err = Some(Error::Decode {
                    line: line_no,
                    raw: Some(trimmed.as_bytes().to_vec()),
                    message,
                });
                cancel.cancel();
                break;
            }
        };

        for mut event in decoded {
            if let EventPayload::ThreadStarted { thread_id: tid } = &event.payload {
                current_thread = tid.clone();
                event.thread_id = current_thread.clone();
                *thread_id.lock().expect("thread id poisoned") = current_thread.clone();
            }
            if event.thread_id.is_empty() {
                event.thread_id = current_thread.clone();
            }
            if let EventPayload::ItemCompleted(item) = &event.payload {
                if item.kind == ItemKind::AgentMessage {
                    final_message = item.text.clone();
                }
            }
            if let EventPayload::TurnCompleted { usage: turn_usage } = &event.payload {
                usage = turn_usage.clone();
            }
            if let EventPayload::TurnFailed {
                usage: turn_usage, ..
            } = &event.payload
            {
                usage = turn_usage.clone();
            }

            last_event = Some(event.clone());
            events.push(event.clone());

            let _ = event_tx.send(event.clone());

            if let Some(handler) = &handler {
                if let Err(message) = handler(event.clone()).await {
                    run_err = Some(Error::Handler(message));
                    cancel.cancel();
                    break 'lines;
                }
            }
        }
    }

    if cancel.is_cancelled() {
        let _ = child.start_kill();
    }
    let wait_result = child.wait().await;
    let _ = stderr_task.await;
    let stderr = tail.snapshot();
    drop(schema_temp);
    let finished_at = SystemTime::now();

    let report = RunResult {
        run_id,
        thread_id: current_thread,
        final_message,
        usage,
        events,
        stderr,
        started_at,
        finished_at,
    };

    if run_err.is_none() {
        run_err = classify_agent_event(agent, last_event.as_ref());
    }
    if run_err.is_none() {
        run_err = classify_process_error(&wait_result, &report.stderr, last_event.as_ref());
    }

    completion.set(RunOutcome {
        report,
        error: run_err,
    });
}

fn classify_process_error(
    wait: &std::io::Result<ExitStatus>,
    stderr: &str,
    last_event: Option<&Event>,
) -> Option<Error> {
    match wait {
        Ok(status) if status.success() => None,
        Ok(status) => Some(Error::Exit {
            code: status.code().unwrap_or(-1),
            stderr: stderr.to_string(),
            last_event: last_event.cloned().map(Box::new),
        }),
        Err(err) => Some(Error::Process(err.to_string())),
    }
}

fn classify_agent_event(agent: Agent, last_event: Option<&Event>) -> Option<Error> {
    let event = last_event?;
    match &event.payload {
        EventPayload::Error(_) | EventPayload::TurnFailed { .. } => Some(match agent {
            Agent::Codex => Error::codex_from_event(event),
            Agent::Claude => Error::claude_from_event(event),
        }),
        _ => None,
    }
}

fn new_run_id() -> String {
    let nanos = SystemTime::now()
        .duration_since(UNIX_EPOCH)
        .map(|d| d.as_nanos())
        .unwrap_or(0);
    let counter = RUN_COUNTER.fetch_add(1, Ordering::Relaxed) + 1;
    format!("run-{nanos}-{counter}")
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn builder_sets_fields() {
        let runner = Runner::builder()
            .executable("codex-test")
            .env("A", "B")
            .event_buffer(2)
            .default_sandbox(SandboxMode::WorkspaceWrite)
            .default_approval(ApprovalPolicy::OnRequest)
            .build();
        assert_eq!(runner.executable(), "codex-test");
        assert_eq!(runner.event_buffer(), 2);
        assert_eq!(runner.default_sandbox(), SandboxMode::WorkspaceWrite);
        assert_eq!(runner.default_approval(), ApprovalPolicy::OnRequest);
    }

    #[test]
    fn run_id_is_unique() {
        assert_ne!(new_run_id(), new_run_id());
    }
}
