//! Python bindings for the `codexcw` core, exposed via PyO3.
//!
//! The native module `_codexcw` is synchronous: methods block on a shared
//! multi-threaded tokio runtime while releasing the GIL. Idiomatic ergonomics
//! (the `Request` dataclass, typed exceptions, the `codexcw.aio` async facade)
//! live in the Python package that wraps this module.

use std::collections::HashMap;
use std::sync::{Mutex, OnceLock};
use std::time::{Duration, SystemTime, UNIX_EPOCH};

use pyo3::prelude::*;
use tokio::runtime::Runtime;
use tokio_stream::wrappers::ReceiverStream;
use tokio_stream::StreamExt;

use codexcw::{
    get_account_usage as core_get_account_usage, AccountCredits as CoreAccountCredits,
    AccountRateLimitWindow as CoreAccountRateLimitWindow,
    AccountRateLimits as CoreAccountRateLimits, AccountSpendLimit as CoreAccountSpendLimit,
    AccountTokenUsage as CoreAccountTokenUsage,
    AccountTokenUsageDailyBucket as CoreAccountTokenUsageDailyBucket,
    AccountTokenUsageSummary as CoreAccountTokenUsageSummary, AccountUsage as CoreAccountUsage,
    AccountUsageAccount as CoreAccountUsageAccount, AccountUsageRequest as CoreAccountUsageRequest,
    ApprovalPolicy, ConfigOverride, Event, EventPayload, Item, ManyOptions, Request, RunResult,
    Runner as CoreRunner, SandboxMode, Usage,
};

fn runtime() -> &'static Runtime {
    static RUNTIME: OnceLock<Runtime> = OnceLock::new();
    RUNTIME.get_or_init(|| {
        tokio::runtime::Builder::new_multi_thread()
            .enable_all()
            .build()
            .expect("failed to build tokio runtime")
    })
}

// ---------------------------------------------------------------------------
// Output value types.
// ---------------------------------------------------------------------------

/// Token usage reported by Codex.
#[pyclass(frozen, skip_from_py_object)]
#[derive(Clone, Default)]
pub struct PyUsage {
    #[pyo3(get)]
    input_tokens: i64,
    #[pyo3(get)]
    cached_input_tokens: i64,
    #[pyo3(get)]
    output_tokens: i64,
    #[pyo3(get)]
    reasoning_output_tokens: i64,
    #[pyo3(get)]
    total_tokens: i64,
}

/// One file edit inside a `file_change` item.
#[pyclass(frozen, skip_from_py_object)]
#[derive(Clone)]
pub struct PyFileChange {
    #[pyo3(get)]
    path: String,
    #[pyo3(get)]
    kind: String,
}

/// A typed projection of a Codex item.
#[pyclass(frozen, skip_from_py_object)]
#[derive(Clone)]
pub struct PyItem {
    #[pyo3(get)]
    id: String,
    #[pyo3(get, name = "type")]
    kind: String,
    #[pyo3(get)]
    status: String,
    #[pyo3(get)]
    text: String,
    #[pyo3(get)]
    message: String,
    #[pyo3(get)]
    command: String,
    #[pyo3(get)]
    aggregated_output: String,
    #[pyo3(get)]
    exit_code: Option<i32>,
    #[pyo3(get)]
    raw: String,
    #[pyo3(get)]
    changes: Vec<PyFileChange>,
}

/// One decoded Codex event.
#[pyclass(frozen, skip_from_py_object)]
#[derive(Clone)]
pub struct PyEvent {
    #[pyo3(get, name = "type")]
    kind: String,
    #[pyo3(get)]
    run_id: String,
    #[pyo3(get)]
    thread_id: String,
    #[pyo3(get)]
    raw: String,
    #[pyo3(get)]
    item: Option<PyItem>,
    #[pyo3(get)]
    usage: Option<PyUsage>,
    #[pyo3(get)]
    error: Option<String>,
}

/// Summary of a completed run.
#[pyclass(frozen, skip_from_py_object)]
#[derive(Clone, Default)]
pub struct PyRunResult {
    #[pyo3(get)]
    run_id: String,
    #[pyo3(get)]
    thread_id: String,
    #[pyo3(get)]
    final_message: String,
    #[pyo3(get)]
    usage: PyUsage,
    #[pyo3(get)]
    events: Vec<PyEvent>,
    #[pyo3(get)]
    stderr: String,
    #[pyo3(get)]
    started_at_ms: f64,
    #[pyo3(get)]
    finished_at_ms: f64,
}

/// A typed run error.
#[pyclass(frozen, skip_from_py_object)]
#[derive(Clone)]
pub struct PyError {
    #[pyo3(get)]
    kind: String,
    #[pyo3(get)]
    message: String,
    #[pyo3(get)]
    code: Option<i32>,
    #[pyo3(get)]
    stderr: Option<String>,
    #[pyo3(get)]
    line: Option<u32>,
}

/// A run result paired with any terminal error.
#[pyclass(frozen, skip_from_py_object)]
#[derive(Clone)]
pub struct PyOutcome {
    #[pyo3(get)]
    result: PyRunResult,
    #[pyo3(get)]
    error: Option<PyError>,
}

/// Codex account limits and credits.
#[pyclass(frozen, skip_from_py_object)]
#[derive(Clone)]
pub struct PyAccountUsage {
    #[pyo3(get)]
    account: Option<PyAccountUsageAccount>,
    #[pyo3(get)]
    token_usage: Option<PyAccountTokenUsage>,
    #[pyo3(get)]
    rate_limits: PyAccountRateLimits,
    #[pyo3(get)]
    rate_limits_by_limit_id: HashMap<String, PyAccountRateLimits>,
    #[pyo3(get)]
    raw_rate_limits: String,
    #[pyo3(get)]
    raw_token_usage: Option<String>,
    #[pyo3(get)]
    raw_account: Option<String>,
}

/// Authenticated account reported by Codex.
#[pyclass(frozen, skip_from_py_object)]
#[derive(Clone)]
pub struct PyAccountUsageAccount {
    #[pyo3(get, name = "type")]
    kind: String,
    #[pyo3(get)]
    email: String,
    #[pyo3(get)]
    plan_type: String,
    #[pyo3(get)]
    requires_openai_auth: bool,
}

/// One Codex rate-limit set.
#[pyclass(frozen, skip_from_py_object)]
#[derive(Clone, Default)]
pub struct PyAccountRateLimits {
    #[pyo3(get)]
    limit_id: String,
    #[pyo3(get)]
    limit_name: String,
    #[pyo3(get)]
    primary: Option<PyAccountRateLimitWindow>,
    #[pyo3(get)]
    secondary: Option<PyAccountRateLimitWindow>,
    #[pyo3(get)]
    credits: Option<PyAccountCredits>,
    #[pyo3(get)]
    individual_limit: Option<PyAccountSpendLimit>,
    #[pyo3(get)]
    plan_type: String,
    #[pyo3(get)]
    rate_limit_reached_type: String,
}

/// One account usage window.
#[pyclass(frozen, skip_from_py_object)]
#[derive(Clone)]
pub struct PyAccountRateLimitWindow {
    #[pyo3(get)]
    used_percent: f64,
    #[pyo3(get)]
    window_duration_mins: i64,
    #[pyo3(get)]
    resets_at: i64,
}

/// Codex credit balance snapshot.
#[pyclass(frozen, skip_from_py_object)]
#[derive(Clone)]
pub struct PyAccountCredits {
    #[pyo3(get)]
    has_credits: bool,
    #[pyo3(get)]
    unlimited: bool,
    #[pyo3(get)]
    balance: Option<String>,
}

/// Individual spend or credit-control limit.
#[pyclass(frozen, skip_from_py_object)]
#[derive(Clone)]
pub struct PyAccountSpendLimit {
    #[pyo3(get)]
    limit: f64,
    #[pyo3(get)]
    used: f64,
    #[pyo3(get)]
    remaining_percent: f64,
    #[pyo3(get)]
    resets_at: i64,
}

/// Account token-usage summary reported by Codex.
#[pyclass(frozen, skip_from_py_object)]
#[derive(Clone)]
pub struct PyAccountTokenUsage {
    #[pyo3(get)]
    summary: PyAccountTokenUsageSummary,
    #[pyo3(get)]
    daily_usage_buckets: Vec<PyAccountTokenUsageDailyBucket>,
}

/// Aggregate account token-usage metrics.
#[pyclass(frozen, skip_from_py_object)]
#[derive(Clone)]
pub struct PyAccountTokenUsageSummary {
    #[pyo3(get)]
    lifetime_tokens: Option<String>,
    #[pyo3(get)]
    peak_daily_tokens: Option<String>,
    #[pyo3(get)]
    longest_running_turn_sec: Option<String>,
    #[pyo3(get)]
    current_streak_days: Option<String>,
    #[pyo3(get)]
    longest_streak_days: Option<String>,
}

/// One daily account token-usage bucket.
#[pyclass(frozen, skip_from_py_object)]
#[derive(Clone)]
pub struct PyAccountTokenUsageDailyBucket {
    #[pyo3(get)]
    start_date: String,
    #[pyo3(get)]
    tokens: String,
}

/// Account usage result paired with any terminal error.
#[pyclass(frozen, skip_from_py_object)]
#[derive(Clone)]
pub struct PyAccountUsageOutcome {
    #[pyo3(get)]
    result: Option<PyAccountUsage>,
    #[pyo3(get)]
    error: Option<PyError>,
}

/// One multiplexed event from `run_many`.
#[pyclass(frozen, skip_from_py_object)]
#[derive(Clone)]
pub struct PyRunEvent {
    #[pyo3(get)]
    run_id: String,
    #[pyo3(get)]
    index: u32,
    #[pyo3(get)]
    event: PyEvent,
}

/// One result from `run_many`.
#[pyclass(frozen, skip_from_py_object)]
#[derive(Clone)]
pub struct PyGroupResult {
    #[pyo3(get)]
    index: u32,
    #[pyo3(get)]
    run_id: String,
    #[pyo3(get)]
    result: Option<PyRunResult>,
    #[pyo3(get)]
    error: Option<PyError>,
}

// ---------------------------------------------------------------------------
// Request extraction.
// ---------------------------------------------------------------------------

#[derive(FromPyObject)]
struct ConfigData {
    key: String,
    value: String,
}

#[derive(FromPyObject)]
struct ReqData {
    prompt: Option<String>,
    stdin: Option<String>,
    dir: Option<String>,
    add_dirs: Option<Vec<String>>,
    images: Option<Vec<String>>,
    model: Option<String>,
    profile: Option<String>,
    sandbox: Option<String>,
    approval: Option<String>,
    permission_mode: Option<String>,
    allowed_tools: Option<Vec<String>>,
    disallowed_tools: Option<Vec<String>>,
    config: Option<Vec<ConfigData>>,
    enable: Option<Vec<String>>,
    disable: Option<Vec<String>>,
    strict_config: Option<bool>,
    persistent: Option<bool>,
    ignore_user_config: Option<bool>,
    ignore_rules: Option<bool>,
    require_git_repo: Option<bool>,
    output_schema_path: Option<String>,
    output_schema: Option<String>,
    output_last_message_path: Option<String>,
    dangerously_bypass_sandbox: Option<bool>,
    dangerously_bypass_hooks: Option<bool>,
    env: Option<HashMap<String, String>>,
    resume_id: Option<String>,
    resume_last: Option<bool>,
    resume_all: Option<bool>,
}

impl ReqData {
    fn into_core(self) -> Result<Request, PyError> {
        let sandbox = match self.sandbox.as_deref() {
            Some(value) => Some(parse_sandbox(value)?),
            None => None,
        };
        let approval = match self.approval.as_deref() {
            Some(value) => Some(parse_approval(value)?),
            None => None,
        };
        Ok(Request {
            prompt: self.prompt.unwrap_or_default(),
            stdin: self.stdin.map(|s| s.into_bytes()),
            dir: self.dir,
            add_dirs: self.add_dirs.unwrap_or_default(),
            images: self.images.unwrap_or_default(),
            model: self.model,
            profile: self.profile,
            sandbox,
            approval,
            permission_mode: self.permission_mode,
            allowed_tools: self.allowed_tools.unwrap_or_default(),
            disallowed_tools: self.disallowed_tools.unwrap_or_default(),
            config: self
                .config
                .unwrap_or_default()
                .into_iter()
                .map(|c| ConfigOverride::new(c.key, c.value))
                .collect(),
            enable: self.enable.unwrap_or_default(),
            disable: self.disable.unwrap_or_default(),
            strict_config: self.strict_config.unwrap_or(false),
            persistent: self.persistent.unwrap_or(false),
            ignore_user_config: self.ignore_user_config.unwrap_or(false),
            ignore_rules: self.ignore_rules.unwrap_or(false),
            require_git_repo: self.require_git_repo.unwrap_or(false),
            output_schema_path: self.output_schema_path,
            output_schema: self.output_schema.map(|s| s.into_bytes()),
            output_last_message_path: self.output_last_message_path,
            dangerously_bypass_sandbox: self.dangerously_bypass_sandbox.unwrap_or(false),
            dangerously_bypass_hooks: self.dangerously_bypass_hooks.unwrap_or(false),
            env: self.env.unwrap_or_default().into_iter().collect(),
            resume_id: self.resume_id,
            resume_last: self.resume_last.unwrap_or(false),
            resume_all: self.resume_all.unwrap_or(false),
        })
    }
}

#[derive(FromPyObject)]
struct AccountUsageReqData {
    executable: Option<String>,
    env: Option<HashMap<String, String>>,
    timeout: Option<f64>,
}

impl AccountUsageReqData {
    fn into_core(self) -> CoreAccountUsageRequest {
        CoreAccountUsageRequest {
            executable: self.executable,
            env: self.env.unwrap_or_default().into_iter().collect(),
            timeout: self.timeout.map(Duration::from_secs_f64),
        }
    }
}

// ---------------------------------------------------------------------------
// Conversions.
// ---------------------------------------------------------------------------

fn system_time_ms(time: SystemTime) -> f64 {
    time.duration_since(UNIX_EPOCH)
        .map(|d| d.as_millis() as f64)
        .unwrap_or(0.0)
}

fn parse_sandbox(value: &str) -> Result<SandboxMode, PyError> {
    match value {
        "read-only" => Ok(SandboxMode::ReadOnly),
        "workspace-write" => Ok(SandboxMode::WorkspaceWrite),
        "danger-full-access" => Ok(SandboxMode::DangerFullAccess),
        other => Err(invalid_request(format!("unknown sandbox mode: {other}"))),
    }
}

fn parse_approval(value: &str) -> Result<ApprovalPolicy, PyError> {
    match value {
        "untrusted" => Ok(ApprovalPolicy::Untrusted),
        "on-request" => Ok(ApprovalPolicy::OnRequest),
        "never" => Ok(ApprovalPolicy::Never),
        other => Err(invalid_request(format!("unknown approval policy: {other}"))),
    }
}

fn invalid_request(message: String) -> PyError {
    PyError {
        kind: "invalidRequest".to_string(),
        message,
        code: None,
        stderr: None,
        line: None,
    }
}

fn parse_agent(value: &str) -> Result<codexcw::Agent, PyError> {
    match value {
        "codex" => Ok(codexcw::Agent::Codex),
        "claude" => Ok(codexcw::Agent::Claude),
        other => Err(invalid_request(format!("unknown agent: {other}"))),
    }
}

fn to_py_usage(usage: &Usage) -> PyUsage {
    PyUsage {
        input_tokens: usage.input_tokens,
        cached_input_tokens: usage.cached_input_tokens,
        output_tokens: usage.output_tokens,
        reasoning_output_tokens: usage.reasoning_output_tokens,
        total_tokens: usage.total_tokens,
    }
}

fn to_py_item(item: &Item) -> PyItem {
    PyItem {
        id: item.id.clone(),
        kind: item.kind.as_str().to_string(),
        status: item.status.clone(),
        text: item.text.clone(),
        message: item.message.clone(),
        command: item.command.clone(),
        aggregated_output: item.aggregated_output.clone(),
        exit_code: item.exit_code,
        raw: item.raw.clone(),
        changes: item
            .changes
            .iter()
            .map(|c| PyFileChange {
                path: c.path.clone(),
                kind: c.kind.clone(),
            })
            .collect(),
    }
}

fn to_py_event(event: &Event) -> PyEvent {
    let mut item = None;
    let mut usage = None;
    let mut error = None;
    match &event.payload {
        EventPayload::ItemStarted(i) | EventPayload::ItemCompleted(i) => item = Some(to_py_item(i)),
        EventPayload::TurnCompleted { usage: u } => usage = Some(to_py_usage(u)),
        EventPayload::TurnFailed { error: e } => error = Some(e.message.clone()),
        EventPayload::Error(e) => error = Some(e.message.clone()),
        _ => {}
    }
    PyEvent {
        kind: event.kind.as_str().to_string(),
        run_id: event.run_id.clone(),
        thread_id: event.thread_id.clone(),
        raw: event.raw.clone(),
        item,
        usage,
        error,
    }
}

fn to_py_result(result: &RunResult) -> PyRunResult {
    PyRunResult {
        run_id: result.run_id.clone(),
        thread_id: result.thread_id.clone(),
        final_message: result.final_message.clone(),
        usage: to_py_usage(&result.usage),
        events: result.events.iter().map(to_py_event).collect(),
        stderr: result.stderr.clone(),
        started_at_ms: system_time_ms(result.started_at),
        finished_at_ms: system_time_ms(result.finished_at),
    }
}

fn to_py_error(error: &codexcw::Error) -> PyError {
    use codexcw::Error::*;
    match error {
        PromptRequired => PyError {
            kind: "promptRequired".to_string(),
            message: error.to_string(),
            code: None,
            stderr: None,
            line: None,
        },
        InvalidRequest(_) => invalid_request(error.to_string()),
        Exit { code, stderr, .. } => PyError {
            kind: "exit".to_string(),
            message: error.to_string(),
            code: Some(*code),
            stderr: Some(stderr.clone()),
            line: None,
        },
        Decode { line, .. } => PyError {
            kind: "decode".to_string(),
            message: error.to_string(),
            code: None,
            stderr: None,
            line: Some(*line as u32),
        },
        Codex { .. } => PyError {
            kind: "codex".to_string(),
            message: error.to_string(),
            code: None,
            stderr: None,
            line: None,
        },
        Handler(_) => PyError {
            kind: "handler".to_string(),
            message: error.to_string(),
            code: None,
            stderr: None,
            line: None,
        },
        Cancelled => PyError {
            kind: "cancelled".to_string(),
            message: error.to_string(),
            code: None,
            stderr: None,
            line: None,
        },
        Process(_) => PyError {
            kind: "process".to_string(),
            message: error.to_string(),
            code: None,
            stderr: None,
            line: None,
        },
    }
}

fn to_py_account_usage_account(account: &CoreAccountUsageAccount) -> PyAccountUsageAccount {
    PyAccountUsageAccount {
        kind: account.kind.clone(),
        email: account.email.clone(),
        plan_type: account.plan_type.clone(),
        requires_openai_auth: account.requires_openai_auth,
    }
}

fn to_py_account_window(window: &CoreAccountRateLimitWindow) -> PyAccountRateLimitWindow {
    PyAccountRateLimitWindow {
        used_percent: window.used_percent,
        window_duration_mins: window.window_duration_mins,
        resets_at: window.resets_at,
    }
}

fn to_py_account_credits(credits: &CoreAccountCredits) -> PyAccountCredits {
    PyAccountCredits {
        has_credits: credits.has_credits,
        unlimited: credits.unlimited,
        balance: credits.balance.clone(),
    }
}

fn to_py_account_spend_limit(limit: &CoreAccountSpendLimit) -> PyAccountSpendLimit {
    PyAccountSpendLimit {
        limit: limit.limit,
        used: limit.used,
        remaining_percent: limit.remaining_percent,
        resets_at: limit.resets_at,
    }
}

fn to_py_account_rate_limits(limits: &CoreAccountRateLimits) -> PyAccountRateLimits {
    PyAccountRateLimits {
        limit_id: limits.limit_id.clone(),
        limit_name: limits.limit_name.clone(),
        primary: limits.primary.as_ref().map(to_py_account_window),
        secondary: limits.secondary.as_ref().map(to_py_account_window),
        credits: limits.credits.as_ref().map(to_py_account_credits),
        individual_limit: limits
            .individual_limit
            .as_ref()
            .map(to_py_account_spend_limit),
        plan_type: limits.plan_type.clone(),
        rate_limit_reached_type: limits.rate_limit_reached_type.clone(),
    }
}

fn to_py_account_token_usage_summary(
    summary: &CoreAccountTokenUsageSummary,
) -> PyAccountTokenUsageSummary {
    PyAccountTokenUsageSummary {
        lifetime_tokens: summary.lifetime_tokens.clone(),
        peak_daily_tokens: summary.peak_daily_tokens.clone(),
        longest_running_turn_sec: summary.longest_running_turn_sec.clone(),
        current_streak_days: summary.current_streak_days.clone(),
        longest_streak_days: summary.longest_streak_days.clone(),
    }
}

fn to_py_account_token_usage_bucket(
    bucket: &CoreAccountTokenUsageDailyBucket,
) -> PyAccountTokenUsageDailyBucket {
    PyAccountTokenUsageDailyBucket {
        start_date: bucket.start_date.clone(),
        tokens: bucket.tokens.clone(),
    }
}

fn to_py_account_token_usage(usage: &CoreAccountTokenUsage) -> PyAccountTokenUsage {
    PyAccountTokenUsage {
        summary: to_py_account_token_usage_summary(&usage.summary),
        daily_usage_buckets: usage
            .daily_usage_buckets
            .iter()
            .map(to_py_account_token_usage_bucket)
            .collect(),
    }
}

fn to_py_account_usage(usage: &CoreAccountUsage) -> PyAccountUsage {
    PyAccountUsage {
        account: usage.account.as_ref().map(to_py_account_usage_account),
        token_usage: usage.token_usage.as_ref().map(to_py_account_token_usage),
        rate_limits: to_py_account_rate_limits(&usage.rate_limits),
        rate_limits_by_limit_id: usage
            .rate_limits_by_limit_id
            .iter()
            .map(|(key, limits)| (key.clone(), to_py_account_rate_limits(limits)))
            .collect(),
        raw_rate_limits: usage.raw_rate_limits.clone(),
        raw_token_usage: usage.raw_token_usage.clone(),
        raw_account: usage.raw_account.clone(),
    }
}

#[pyfunction]
#[pyo3(signature = (req=None))]
fn get_account_usage(py: Python<'_>, req: Option<AccountUsageReqData>) -> PyAccountUsageOutcome {
    let core_req = req.map(AccountUsageReqData::into_core).unwrap_or_default();
    let result = py.detach(|| runtime().block_on(core_get_account_usage(core_req)));
    match result {
        Ok(usage) => PyAccountUsageOutcome {
            result: Some(to_py_account_usage(&usage)),
            error: None,
        },
        Err(error) => PyAccountUsageOutcome {
            result: None,
            error: Some(to_py_error(&error)),
        },
    }
}

// ---------------------------------------------------------------------------
// Session.
// ---------------------------------------------------------------------------

struct LiveSession {
    core: codexcw::Session,
    stream: Mutex<ReceiverStream<Event>>,
}

enum SessionInner {
    Live(LiveSession),
    Failed(PyError),
}

/// A running `codex exec` process. Iterate it for events; call `wait()` for the
/// final outcome.
#[pyclass]
pub struct Session {
    inner: SessionInner,
}

impl Session {
    fn from_core(result: Result<codexcw::Session, codexcw::Error>) -> Self {
        match result {
            Ok(mut core) => {
                let stream = core.events();
                Session {
                    inner: SessionInner::Live(LiveSession {
                        core,
                        stream: Mutex::new(stream),
                    }),
                }
            }
            Err(error) => Session {
                inner: SessionInner::Failed(to_py_error(&error)),
            },
        }
    }
}

#[pymethods]
impl Session {
    fn __iter__(slf: PyRef<'_, Self>) -> PyRef<'_, Self> {
        slf
    }

    fn __next__(&self, py: Python<'_>) -> Option<PyEvent> {
        match &self.inner {
            SessionInner::Failed(_) => None,
            SessionInner::Live(live) => {
                let event = py.detach(|| {
                    let mut stream = live.stream.lock().expect("stream poisoned");
                    runtime().block_on(stream.next())
                });
                event.as_ref().map(to_py_event)
            }
        }
    }

    /// Awaits the next event, or `None` once the stream closes.
    fn next_event(&self, py: Python<'_>) -> Option<PyEvent> {
        self.__next__(py)
    }

    /// Waits for the process to exit and returns its outcome.
    fn wait(&self, py: Python<'_>) -> PyOutcome {
        match &self.inner {
            SessionInner::Failed(error) => PyOutcome {
                result: PyRunResult::default(),
                error: Some(error.clone()),
            },
            SessionInner::Live(live) => {
                let (report, error) = py.detach(|| runtime().block_on(live.core.join()));
                PyOutcome {
                    result: to_py_result(&report),
                    error: error.as_ref().map(to_py_error),
                }
            }
        }
    }

    /// The wrapper-assigned run id.
    #[getter]
    fn id(&self) -> String {
        match &self.inner {
            SessionInner::Failed(_) => String::new(),
            SessionInner::Live(live) => live.core.id().to_string(),
        }
    }

    /// The Codex thread id once known.
    fn thread_id(&self) -> String {
        match &self.inner {
            SessionInner::Failed(_) => String::new(),
            SessionInner::Live(live) => live.core.thread_id(),
        }
    }

    /// Stops the child process.
    fn cancel(&self) {
        if let SessionInner::Live(live) = &self.inner {
            live.core.cancel();
        }
    }
}

// ---------------------------------------------------------------------------
// Group.
// ---------------------------------------------------------------------------

struct LiveGroup {
    core: codexcw::Group,
    stream: Mutex<ReceiverStream<codexcw::RunEvent>>,
}

/// A batch of running `codex exec` processes.
#[pyclass]
pub struct Group {
    inner: LiveGroup,
}

impl Group {
    fn from_core(mut core: codexcw::Group) -> Self {
        let stream = core.events();
        Group {
            inner: LiveGroup {
                core,
                stream: Mutex::new(stream),
            },
        }
    }
}

#[pymethods]
impl Group {
    fn __iter__(slf: PyRef<'_, Self>) -> PyRef<'_, Self> {
        slf
    }

    fn __next__(&self, py: Python<'_>) -> Option<PyRunEvent> {
        let run_event = py.detach(|| {
            let mut stream = self.inner.stream.lock().expect("stream poisoned");
            runtime().block_on(stream.next())
        });
        run_event.map(|re| PyRunEvent {
            run_id: re.run_id,
            index: re.index as u32,
            event: to_py_event(&re.event),
        })
    }

    /// Awaits the next multiplexed event, or `None` once every run finishes.
    fn next_event(&self, py: Python<'_>) -> Option<PyRunEvent> {
        self.__next__(py)
    }

    /// Returns every run result.
    fn wait(&self, py: Python<'_>) -> Vec<PyGroupResult> {
        let results = py.detach(|| match runtime().block_on(self.inner.core.wait()) {
            Ok(results) => results,
            Err(group_error) => group_error.results,
        });
        results
            .into_iter()
            .map(|r| PyGroupResult {
                index: r.index as u32,
                run_id: r.run_id,
                result: r.result.as_ref().map(to_py_result),
                error: r.error.as_ref().map(to_py_error),
            })
            .collect()
    }

    /// Stops all active and pending runs.
    fn cancel(&self) {
        self.inner.core.cancel();
    }
}

// ---------------------------------------------------------------------------
// Runner.
// ---------------------------------------------------------------------------

/// Starts `codex exec` processes with safe automation defaults.
#[pyclass]
pub struct Runner {
    core: CoreRunner,
}

#[pymethods]
impl Runner {
    #[new]
    #[pyo3(signature = (
        executable=None,
        env=None,
        event_buffer=None,
        stderr_limit=None,
        scan_max_bytes=None,
        default_sandbox=None,
        default_approval=None,
        agent=None,
    ))]
    #[allow(clippy::too_many_arguments)]
    fn new(
        executable: Option<String>,
        env: Option<HashMap<String, String>>,
        event_buffer: Option<u32>,
        stderr_limit: Option<u32>,
        scan_max_bytes: Option<u32>,
        default_sandbox: Option<String>,
        default_approval: Option<String>,
        agent: Option<String>,
    ) -> Self {
        let mut builder = CoreRunner::builder();
        if let Some(value) = agent {
            builder = builder.agent(parse_agent(&value).unwrap_or_default());
        }
        if let Some(executable) = executable {
            builder = builder.executable(executable);
        }
        for (key, value) in env.unwrap_or_default() {
            builder = builder.env(key, value);
        }
        if let Some(n) = event_buffer {
            builder = builder.event_buffer(n as usize);
        }
        if let Some(n) = stderr_limit {
            builder = builder.stderr_limit(n as usize);
        }
        if let Some(n) = scan_max_bytes {
            builder = builder.scan_max_bytes(n as usize);
        }
        if let Some(value) = default_sandbox {
            builder =
                builder.default_sandbox(parse_sandbox(&value).unwrap_or(SandboxMode::ReadOnly));
        }
        if let Some(value) = default_approval {
            builder =
                builder.default_approval(parse_approval(&value).unwrap_or(ApprovalPolicy::Never));
        }
        Runner {
            core: builder.build(),
        }
    }

    /// Runs one process to completion and returns its outcome.
    fn run(&self, py: Python<'_>, req: ReqData) -> PyOutcome {
        let core_req = match req.into_core() {
            Ok(req) => req,
            Err(error) => {
                return PyOutcome {
                    result: PyRunResult::default(),
                    error: Some(error),
                }
            }
        };
        let runner = self.core.clone();
        let (report, error): (Option<RunResult>, Option<codexcw::Error>) = py.detach(move || {
            runtime().block_on(async move {
                let mut session = match runner.start(core_req).await {
                    Ok(session) => session,
                    Err(error) => return (None, Some(error)),
                };
                while session.next_event().await.is_some() {}
                let (report, error) = session.join().await;
                (Some(report), error)
            })
        });
        PyOutcome {
            result: report.as_ref().map(to_py_result).unwrap_or_default(),
            error: error.as_ref().map(to_py_error),
        }
    }

    /// Launches one process and returns a [`Session`].
    fn start(&self, py: Python<'_>, req: ReqData) -> Session {
        let core_req = match req.into_core() {
            Ok(req) => req,
            Err(error) => {
                return Session {
                    inner: SessionInner::Failed(error),
                }
            }
        };
        let runner = self.core.clone();
        let result = py.detach(move || runtime().block_on(runner.start(core_req)));
        Session::from_core(result)
    }

    /// Launches many processes with bounded concurrency.
    #[pyo3(signature = (reqs, max_concurrent=None, event_buffer=None))]
    fn run_many(
        &self,
        py: Python<'_>,
        reqs: Vec<ReqData>,
        max_concurrent: Option<u32>,
        event_buffer: Option<u32>,
    ) -> Group {
        let core_reqs: Vec<Request> = reqs
            .into_iter()
            .map(|r| r.into_core().unwrap_or_default())
            .collect();
        let mut many = ManyOptions::default();
        if let Some(n) = max_concurrent {
            many.max_concurrent = n as usize;
        }
        if let Some(n) = event_buffer {
            many.event_buffer = Some(n as usize);
        }
        let runner = self.core.clone();
        let core_group = py.detach(move || runtime().block_on(runner.run_many(core_reqs, many)));
        Group::from_core(core_group)
    }
}

/// The native `_codexcw` extension module.
#[pymodule]
fn _codexcw(m: &Bound<'_, PyModule>) -> PyResult<()> {
    m.add_class::<Runner>()?;
    m.add_class::<Session>()?;
    m.add_class::<Group>()?;
    m.add_class::<PyEvent>()?;
    m.add_class::<PyItem>()?;
    m.add_class::<PyUsage>()?;
    m.add_class::<PyFileChange>()?;
    m.add_class::<PyRunResult>()?;
    m.add_class::<PyOutcome>()?;
    m.add_class::<PyError>()?;
    m.add_class::<PyAccountUsage>()?;
    m.add_class::<PyAccountUsageAccount>()?;
    m.add_class::<PyAccountRateLimits>()?;
    m.add_class::<PyAccountRateLimitWindow>()?;
    m.add_class::<PyAccountCredits>()?;
    m.add_class::<PyAccountSpendLimit>()?;
    m.add_class::<PyAccountTokenUsage>()?;
    m.add_class::<PyAccountTokenUsageSummary>()?;
    m.add_class::<PyAccountTokenUsageDailyBucket>()?;
    m.add_class::<PyAccountUsageOutcome>()?;
    m.add_class::<PyRunEvent>()?;
    m.add_class::<PyGroupResult>()?;
    m.add_function(wrap_pyfunction!(get_account_usage, m)?)?;
    Ok(())
}
