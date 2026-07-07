//! Node.js bindings for the `codexcw` core, exposed via napi-rs.
//!
//! This crate maps the async Rust API to JavaScript classes. Idiomatic
//! ergonomics (the `events()` async iterator, the `run(req, onEvent)` callback,
//! typed errors) are layered on top in the hand-written `index.js` wrapper.

use std::collections::HashMap;
use std::sync::Arc;
use std::time::{SystemTime, UNIX_EPOCH};

use napi_derive::napi;
use tokio::sync::Mutex;
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

// ---------------------------------------------------------------------------
// Plain-object types crossing the FFI boundary.
// ---------------------------------------------------------------------------

/// Token usage reported by Codex.
#[napi(object)]
#[derive(Default)]
pub struct JsUsage {
    pub input_tokens: i64,
    pub cached_input_tokens: i64,
    pub output_tokens: i64,
    pub reasoning_output_tokens: i64,
    pub total_tokens: i64,
}

/// One file edit inside a `file_change` item.
#[napi(object)]
pub struct JsFileChange {
    pub path: String,
    pub kind: String,
}

/// A typed projection of a Codex item.
#[napi(object)]
pub struct JsItem {
    pub id: String,
    #[napi(js_name = "type")]
    pub kind: String,
    pub status: String,
    pub text: String,
    pub message: String,
    pub command: String,
    pub aggregated_output: String,
    pub exit_code: Option<i32>,
    pub raw: String,
    pub changes: Vec<JsFileChange>,
}

/// One decoded Codex event.
#[napi(object)]
pub struct JsEvent {
    #[napi(js_name = "type")]
    pub kind: String,
    pub run_id: String,
    pub thread_id: String,
    pub raw: String,
    pub item: Option<JsItem>,
    pub usage: Option<JsUsage>,
    pub error: Option<String>,
}

/// Summary of a completed run.
#[napi(object)]
#[derive(Default)]
pub struct JsRunResult {
    pub run_id: String,
    pub thread_id: String,
    pub final_message: String,
    pub usage: JsUsage,
    pub events: Vec<JsEvent>,
    pub stderr: String,
    pub started_at_ms: f64,
    pub finished_at_ms: f64,
}

/// A typed run error.
#[napi(object)]
pub struct JsError {
    /// One of: `promptRequired`, `invalidRequest`, `exit`, `decode`, `codex`,
    /// `handler`, `cancelled`, `process`.
    pub kind: String,
    pub message: String,
    pub code: Option<i32>,
    pub stderr: Option<String>,
    pub line: Option<u32>,
}

/// A run result paired with any terminal error.
#[napi(object)]
pub struct JsOutcome {
    pub result: JsRunResult,
    pub error: Option<JsError>,
}

/// Options for reading Codex account usage.
#[napi(object)]
pub struct JsAccountUsageRequest {
    pub executable: Option<String>,
    pub env: Option<HashMap<String, String>>,
}

/// Codex account limits and credits.
#[napi(object)]
pub struct JsAccountUsage {
    pub account: Option<JsAccountUsageAccount>,
    pub token_usage: Option<JsAccountTokenUsage>,
    pub rate_limits: JsAccountRateLimits,
    pub rate_limits_by_limit_id: HashMap<String, JsAccountRateLimits>,
    pub raw_rate_limits: String,
    pub raw_token_usage: Option<String>,
    pub raw_account: Option<String>,
}

/// Authenticated account reported by Codex.
#[napi(object)]
pub struct JsAccountUsageAccount {
    #[napi(js_name = "type")]
    pub kind: String,
    pub email: String,
    pub plan_type: String,
    pub requires_openai_auth: bool,
}

/// One Codex rate-limit set.
#[napi(object)]
pub struct JsAccountRateLimits {
    pub limit_id: String,
    pub limit_name: String,
    pub primary: Option<JsAccountRateLimitWindow>,
    pub secondary: Option<JsAccountRateLimitWindow>,
    pub credits: Option<JsAccountCredits>,
    pub individual_limit: Option<JsAccountSpendLimit>,
    pub plan_type: String,
    pub rate_limit_reached_type: String,
}

/// One account usage window.
#[napi(object)]
pub struct JsAccountRateLimitWindow {
    pub used_percent: f64,
    pub window_duration_mins: i64,
    pub resets_at: i64,
}

/// Codex credit balance snapshot.
#[napi(object)]
pub struct JsAccountCredits {
    pub has_credits: bool,
    pub unlimited: bool,
    pub balance: Option<String>,
}

/// Individual spend or credit-control limit.
#[napi(object)]
pub struct JsAccountSpendLimit {
    pub limit: f64,
    pub used: f64,
    pub remaining_percent: f64,
    pub resets_at: i64,
}

/// Account token-usage summary reported by Codex.
#[napi(object)]
pub struct JsAccountTokenUsage {
    pub summary: JsAccountTokenUsageSummary,
    pub daily_usage_buckets: Vec<JsAccountTokenUsageDailyBucket>,
}

/// Aggregate account token-usage metrics.
#[napi(object)]
pub struct JsAccountTokenUsageSummary {
    pub lifetime_tokens: Option<String>,
    pub peak_daily_tokens: Option<String>,
    pub longest_running_turn_sec: Option<String>,
    pub current_streak_days: Option<String>,
    pub longest_streak_days: Option<String>,
}

/// One daily account token-usage bucket.
#[napi(object)]
pub struct JsAccountTokenUsageDailyBucket {
    pub start_date: String,
    pub tokens: String,
}

/// Account usage result paired with any terminal error.
#[napi(object)]
pub struct JsAccountUsageOutcome {
    pub result: Option<JsAccountUsage>,
    pub error: Option<JsError>,
}

/// One multiplexed event from `runMany`.
#[napi(object)]
pub struct JsRunEvent {
    pub run_id: String,
    pub index: u32,
    pub event: JsEvent,
}

/// One result from `runMany`.
#[napi(object)]
pub struct JsGroupResult {
    pub index: u32,
    pub run_id: String,
    pub result: Option<JsRunResult>,
    pub error: Option<JsError>,
}

/// Options for constructing a [`Runner`].
#[napi(object)]
pub struct JsRunnerOptions {
    pub executable: Option<String>,
    pub env: Option<HashMap<String, String>>,
    pub event_buffer: Option<u32>,
    pub stderr_limit: Option<u32>,
    pub scan_max_bytes: Option<u32>,
    pub default_sandbox: Option<String>,
    pub default_approval: Option<String>,
}

/// Options for `runMany`.
#[napi(object)]
pub struct JsManyOptions {
    pub max_concurrent: Option<u32>,
    pub event_buffer: Option<u32>,
}

/// A Codex run request. All fields are optional except prompt or stdin.
#[napi(object)]
#[derive(Default)]
pub struct JsRequest {
    pub prompt: Option<String>,
    pub stdin: Option<String>,
    pub dir: Option<String>,
    pub add_dirs: Option<Vec<String>>,
    pub images: Option<Vec<String>>,
    pub model: Option<String>,
    pub profile: Option<String>,
    pub sandbox: Option<String>,
    pub approval: Option<String>,
    pub config: Option<Vec<JsConfigOverride>>,
    pub enable: Option<Vec<String>>,
    pub disable: Option<Vec<String>>,
    pub strict_config: Option<bool>,
    pub persistent: Option<bool>,
    pub ignore_user_config: Option<bool>,
    pub ignore_rules: Option<bool>,
    pub require_git_repo: Option<bool>,
    pub output_schema_path: Option<String>,
    pub output_schema: Option<String>,
    pub output_last_message_path: Option<String>,
    pub dangerously_bypass_sandbox: Option<bool>,
    pub dangerously_bypass_hooks: Option<bool>,
    pub env: Option<HashMap<String, String>>,
    pub resume_id: Option<String>,
    pub resume_last: Option<bool>,
    pub resume_all: Option<bool>,
}

/// One `-c key=value` config override.
#[napi(object)]
pub struct JsConfigOverride {
    pub key: String,
    pub value: String,
}

// ---------------------------------------------------------------------------
// Conversions.
// ---------------------------------------------------------------------------

fn system_time_ms(time: SystemTime) -> f64 {
    time.duration_since(UNIX_EPOCH)
        .map(|d| d.as_millis() as f64)
        .unwrap_or(0.0)
}

fn parse_sandbox(value: &str) -> Result<SandboxMode, JsError> {
    match value {
        "read-only" => Ok(SandboxMode::ReadOnly),
        "workspace-write" => Ok(SandboxMode::WorkspaceWrite),
        "danger-full-access" => Ok(SandboxMode::DangerFullAccess),
        other => Err(invalid_request(format!("unknown sandbox mode: {other}"))),
    }
}

fn parse_approval(value: &str) -> Result<ApprovalPolicy, JsError> {
    match value {
        "untrusted" => Ok(ApprovalPolicy::Untrusted),
        "on-request" => Ok(ApprovalPolicy::OnRequest),
        "never" => Ok(ApprovalPolicy::Never),
        other => Err(invalid_request(format!("unknown approval policy: {other}"))),
    }
}

fn invalid_request(message: String) -> JsError {
    JsError {
        kind: "invalidRequest".to_string(),
        message,
        code: None,
        stderr: None,
        line: None,
    }
}

fn to_core_request(req: JsRequest) -> Result<Request, JsError> {
    let sandbox = match req.sandbox.as_deref() {
        Some(value) => Some(parse_sandbox(value)?),
        None => None,
    };
    let approval = match req.approval.as_deref() {
        Some(value) => Some(parse_approval(value)?),
        None => None,
    };
    Ok(Request {
        prompt: req.prompt.unwrap_or_default(),
        stdin: req.stdin.map(|s| s.into_bytes()),
        dir: req.dir,
        add_dirs: req.add_dirs.unwrap_or_default(),
        images: req.images.unwrap_or_default(),
        model: req.model,
        profile: req.profile,
        sandbox,
        approval,
        config: req
            .config
            .unwrap_or_default()
            .into_iter()
            .map(|c| ConfigOverride::new(c.key, c.value))
            .collect(),
        enable: req.enable.unwrap_or_default(),
        disable: req.disable.unwrap_or_default(),
        strict_config: req.strict_config.unwrap_or(false),
        persistent: req.persistent.unwrap_or(false),
        ignore_user_config: req.ignore_user_config.unwrap_or(false),
        ignore_rules: req.ignore_rules.unwrap_or(false),
        require_git_repo: req.require_git_repo.unwrap_or(false),
        output_schema_path: req.output_schema_path,
        output_schema: req.output_schema.map(|s| s.into_bytes()),
        output_last_message_path: req.output_last_message_path,
        dangerously_bypass_sandbox: req.dangerously_bypass_sandbox.unwrap_or(false),
        dangerously_bypass_hooks: req.dangerously_bypass_hooks.unwrap_or(false),
        env: req.env.unwrap_or_default().into_iter().collect(),
        resume_id: req.resume_id,
        resume_last: req.resume_last.unwrap_or(false),
        resume_all: req.resume_all.unwrap_or(false),
    })
}

fn to_core_account_usage_request(req: Option<JsAccountUsageRequest>) -> CoreAccountUsageRequest {
    match req {
        Some(req) => CoreAccountUsageRequest {
            executable: req.executable,
            env: req.env.unwrap_or_default().into_iter().collect(),
        },
        None => CoreAccountUsageRequest::default(),
    }
}

fn to_js_usage(usage: &Usage) -> JsUsage {
    JsUsage {
        input_tokens: usage.input_tokens,
        cached_input_tokens: usage.cached_input_tokens,
        output_tokens: usage.output_tokens,
        reasoning_output_tokens: usage.reasoning_output_tokens,
        total_tokens: usage.total_tokens,
    }
}

fn to_js_item(item: &Item) -> JsItem {
    JsItem {
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
            .map(|c| JsFileChange {
                path: c.path.clone(),
                kind: c.kind.clone(),
            })
            .collect(),
    }
}

fn to_js_event(event: &Event) -> JsEvent {
    let mut item = None;
    let mut usage = None;
    let mut error = None;
    match &event.payload {
        EventPayload::ItemStarted(i) | EventPayload::ItemCompleted(i) => item = Some(to_js_item(i)),
        EventPayload::TurnCompleted { usage: u } => usage = Some(to_js_usage(u)),
        EventPayload::TurnFailed { error: e } => error = Some(e.message.clone()),
        EventPayload::Error(e) => error = Some(e.message.clone()),
        _ => {}
    }
    JsEvent {
        kind: event.kind.as_str().to_string(),
        run_id: event.run_id.clone(),
        thread_id: event.thread_id.clone(),
        raw: event.raw.clone(),
        item,
        usage,
        error,
    }
}

fn to_js_result(result: &RunResult) -> JsRunResult {
    JsRunResult {
        run_id: result.run_id.clone(),
        thread_id: result.thread_id.clone(),
        final_message: result.final_message.clone(),
        usage: to_js_usage(&result.usage),
        events: result.events.iter().map(to_js_event).collect(),
        stderr: result.stderr.clone(),
        started_at_ms: system_time_ms(result.started_at),
        finished_at_ms: system_time_ms(result.finished_at),
    }
}

fn to_js_error(error: &codexcw::Error) -> JsError {
    use codexcw::Error::*;
    match error {
        PromptRequired => JsError {
            kind: "promptRequired".to_string(),
            message: error.to_string(),
            code: None,
            stderr: None,
            line: None,
        },
        InvalidRequest(_) => invalid_request(error.to_string()),
        Exit {
            code,
            stderr,
            last_event: _,
        } => JsError {
            kind: "exit".to_string(),
            message: error.to_string(),
            code: Some(*code),
            stderr: Some(stderr.clone()),
            line: None,
        },
        Decode { line, .. } => JsError {
            kind: "decode".to_string(),
            message: error.to_string(),
            code: None,
            stderr: None,
            line: Some(*line as u32),
        },
        Codex { .. } => JsError {
            kind: "codex".to_string(),
            message: error.to_string(),
            code: None,
            stderr: None,
            line: None,
        },
        Handler(_) => JsError {
            kind: "handler".to_string(),
            message: error.to_string(),
            code: None,
            stderr: None,
            line: None,
        },
        Cancelled => JsError {
            kind: "cancelled".to_string(),
            message: error.to_string(),
            code: None,
            stderr: None,
            line: None,
        },
        Process(_) => JsError {
            kind: "process".to_string(),
            message: error.to_string(),
            code: None,
            stderr: None,
            line: None,
        },
    }
}

fn to_js_account_usage_account(account: &CoreAccountUsageAccount) -> JsAccountUsageAccount {
    JsAccountUsageAccount {
        kind: account.kind.clone(),
        email: account.email.clone(),
        plan_type: account.plan_type.clone(),
        requires_openai_auth: account.requires_openai_auth,
    }
}

fn to_js_account_window(window: &CoreAccountRateLimitWindow) -> JsAccountRateLimitWindow {
    JsAccountRateLimitWindow {
        used_percent: window.used_percent,
        window_duration_mins: window.window_duration_mins,
        resets_at: window.resets_at,
    }
}

fn to_js_account_credits(credits: &CoreAccountCredits) -> JsAccountCredits {
    JsAccountCredits {
        has_credits: credits.has_credits,
        unlimited: credits.unlimited,
        balance: credits.balance.clone(),
    }
}

fn to_js_account_spend_limit(limit: &CoreAccountSpendLimit) -> JsAccountSpendLimit {
    JsAccountSpendLimit {
        limit: limit.limit,
        used: limit.used,
        remaining_percent: limit.remaining_percent,
        resets_at: limit.resets_at,
    }
}

fn to_js_account_rate_limits(limits: &CoreAccountRateLimits) -> JsAccountRateLimits {
    JsAccountRateLimits {
        limit_id: limits.limit_id.clone(),
        limit_name: limits.limit_name.clone(),
        primary: limits.primary.as_ref().map(to_js_account_window),
        secondary: limits.secondary.as_ref().map(to_js_account_window),
        credits: limits.credits.as_ref().map(to_js_account_credits),
        individual_limit: limits
            .individual_limit
            .as_ref()
            .map(to_js_account_spend_limit),
        plan_type: limits.plan_type.clone(),
        rate_limit_reached_type: limits.rate_limit_reached_type.clone(),
    }
}

fn to_js_account_token_usage_summary(
    summary: &CoreAccountTokenUsageSummary,
) -> JsAccountTokenUsageSummary {
    JsAccountTokenUsageSummary {
        lifetime_tokens: summary.lifetime_tokens.clone(),
        peak_daily_tokens: summary.peak_daily_tokens.clone(),
        longest_running_turn_sec: summary.longest_running_turn_sec.clone(),
        current_streak_days: summary.current_streak_days.clone(),
        longest_streak_days: summary.longest_streak_days.clone(),
    }
}

fn to_js_account_token_usage_bucket(
    bucket: &CoreAccountTokenUsageDailyBucket,
) -> JsAccountTokenUsageDailyBucket {
    JsAccountTokenUsageDailyBucket {
        start_date: bucket.start_date.clone(),
        tokens: bucket.tokens.clone(),
    }
}

fn to_js_account_token_usage(usage: &CoreAccountTokenUsage) -> JsAccountTokenUsage {
    JsAccountTokenUsage {
        summary: to_js_account_token_usage_summary(&usage.summary),
        daily_usage_buckets: usage
            .daily_usage_buckets
            .iter()
            .map(to_js_account_token_usage_bucket)
            .collect(),
    }
}

fn to_js_account_usage(usage: &CoreAccountUsage) -> JsAccountUsage {
    JsAccountUsage {
        account: usage.account.as_ref().map(to_js_account_usage_account),
        token_usage: usage.token_usage.as_ref().map(to_js_account_token_usage),
        rate_limits: to_js_account_rate_limits(&usage.rate_limits),
        rate_limits_by_limit_id: usage
            .rate_limits_by_limit_id
            .iter()
            .map(|(key, limits)| (key.clone(), to_js_account_rate_limits(limits)))
            .collect(),
        raw_rate_limits: usage.raw_rate_limits.clone(),
        raw_token_usage: usage.raw_token_usage.clone(),
        raw_account: usage.raw_account.clone(),
    }
}

// ---------------------------------------------------------------------------
// Account usage.
// ---------------------------------------------------------------------------

/// Reads Codex account usage and limits through `codex app-server`.
#[napi]
pub async fn get_account_usage_raw(req: Option<JsAccountUsageRequest>) -> JsAccountUsageOutcome {
    match core_get_account_usage(to_core_account_usage_request(req)).await {
        Ok(usage) => JsAccountUsageOutcome {
            result: Some(to_js_account_usage(&usage)),
            error: None,
        },
        Err(error) => JsAccountUsageOutcome {
            result: None,
            error: Some(to_js_error(&error)),
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
    Live(Arc<LiveSession>),
    Failed(JsError),
}

/// A running `codex exec` process. Prefer the `events()` async iterator and
/// `wait()` exposed by the `index.js` wrapper over these raw methods.
#[napi]
pub struct Session {
    inner: SessionInner,
}

#[napi]
impl Session {
    /// Awaits the next decoded event, or `null` once the stream closes.
    #[napi]
    pub async fn next_event(&self) -> Option<JsEvent> {
        let live = match &self.inner {
            SessionInner::Failed(_) => return None,
            SessionInner::Live(live) => live.clone(),
        };
        let mut stream = live.stream.lock().await;
        stream.next().await.map(|event| to_js_event(&event))
    }

    /// Waits for the process to exit and returns its outcome.
    #[napi]
    pub async fn wait(&self) -> JsOutcome {
        match &self.inner {
            SessionInner::Failed(error) => JsOutcome {
                result: JsRunResult::default(),
                error: Some(clone_js_error(error)),
            },
            SessionInner::Live(live) => {
                let (report, error) = live.core.join().await;
                JsOutcome {
                    result: to_js_result(&report),
                    error: error.as_ref().map(to_js_error),
                }
            }
        }
    }

    /// The wrapper-assigned run id.
    #[napi(getter)]
    pub fn id(&self) -> String {
        match &self.inner {
            SessionInner::Failed(_) => String::new(),
            SessionInner::Live(live) => live.core.id().to_string(),
        }
    }

    /// The Codex thread id once known.
    #[napi]
    pub fn thread_id(&self) -> String {
        match &self.inner {
            SessionInner::Failed(_) => String::new(),
            SessionInner::Live(live) => live.core.thread_id(),
        }
    }

    /// Stops the child process.
    #[napi]
    pub fn cancel(&self) {
        if let SessionInner::Live(live) = &self.inner {
            live.core.cancel();
        }
    }
}

fn clone_js_error(error: &JsError) -> JsError {
    JsError {
        kind: error.kind.clone(),
        message: error.message.clone(),
        code: error.code,
        stderr: error.stderr.clone(),
        line: error.line,
    }
}

fn session_from(result: Result<codexcw::Session, codexcw::Error>) -> Session {
    match result {
        Ok(mut core) => {
            let stream = core.events();
            Session {
                inner: SessionInner::Live(Arc::new(LiveSession {
                    core,
                    stream: Mutex::new(stream),
                })),
            }
        }
        Err(error) => Session {
            inner: SessionInner::Failed(to_js_error(&error)),
        },
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
#[napi]
pub struct Group {
    inner: Arc<LiveGroup>,
}

#[napi]
impl Group {
    /// Awaits the next multiplexed event, or `null` once every run finishes.
    #[napi]
    pub async fn next_event(&self) -> Option<JsRunEvent> {
        let group = self.inner.clone();
        let mut stream = group.stream.lock().await;
        stream.next().await.map(|run_event| JsRunEvent {
            run_id: run_event.run_id,
            index: run_event.index as u32,
            event: to_js_event(&run_event.event),
        })
    }

    /// Returns every run result.
    #[napi]
    pub async fn wait(&self) -> Vec<JsGroupResult> {
        let results = match self.inner.core.wait().await {
            Ok(results) => results,
            Err(group_error) => group_error.results,
        };
        results
            .into_iter()
            .map(|r| JsGroupResult {
                index: r.index as u32,
                run_id: r.run_id,
                result: r.result.as_ref().map(to_js_result),
                error: r.error.as_ref().map(to_js_error),
            })
            .collect()
    }

    /// Stops all active and pending runs.
    #[napi]
    pub fn cancel(&self) {
        self.inner.core.cancel();
    }
}

// ---------------------------------------------------------------------------
// Runner.
// ---------------------------------------------------------------------------

/// Starts `codex exec` processes with safe automation defaults.
#[napi]
pub struct Runner {
    core: CoreRunner,
}

#[napi]
impl Runner {
    /// Creates a runner, optionally overriding defaults. Unknown
    /// `defaultSandbox`/`defaultApproval` strings fall back to the safe
    /// defaults (read-only, never); the TypeScript types constrain them.
    #[napi(constructor)]
    pub fn new(options: Option<JsRunnerOptions>) -> Self {
        let mut builder = CoreRunner::builder();
        if let Some(options) = options {
            if let Some(executable) = options.executable {
                builder = builder.executable(executable);
            }
            for (key, value) in options.env.unwrap_or_default() {
                builder = builder.env(key, value);
            }
            if let Some(n) = options.event_buffer {
                builder = builder.event_buffer(n as usize);
            }
            if let Some(n) = options.stderr_limit {
                builder = builder.stderr_limit(n as usize);
            }
            if let Some(n) = options.scan_max_bytes {
                builder = builder.scan_max_bytes(n as usize);
            }
            if let Some(value) = options.default_sandbox {
                builder =
                    builder.default_sandbox(parse_sandbox(&value).unwrap_or(SandboxMode::ReadOnly));
            }
            if let Some(value) = options.default_approval {
                builder = builder
                    .default_approval(parse_approval(&value).unwrap_or(ApprovalPolicy::Never));
            }
        }
        Runner {
            core: builder.build(),
        }
    }

    /// Launches one process and returns a [`Session`]. Never rejects: request
    /// or spawn failures surface through the session's `wait()` outcome.
    #[napi]
    pub async fn start(&self, req: JsRequest) -> Session {
        match to_core_request(req) {
            Ok(req) => session_from(self.core.start(req).await),
            Err(error) => Session {
                inner: SessionInner::Failed(error),
            },
        }
    }

    /// Runs one process to completion and returns its outcome.
    #[napi]
    pub async fn run_raw(&self, req: JsRequest) -> JsOutcome {
        let req = match to_core_request(req) {
            Ok(req) => req,
            Err(error) => {
                return JsOutcome {
                    result: JsRunResult::default(),
                    error: Some(error),
                }
            }
        };
        let mut session = match self.core.start(req).await {
            Ok(session) => session,
            Err(error) => {
                return JsOutcome {
                    result: JsRunResult::default(),
                    error: Some(to_js_error(&error)),
                }
            }
        };
        while session.next_event().await.is_some() {}
        let (report, error) = session.join().await;
        JsOutcome {
            result: to_js_result(&report),
            error: error.as_ref().map(to_js_error),
        }
    }

    /// Launches many processes with bounded concurrency.
    #[napi]
    pub async fn run_many(&self, reqs: Vec<JsRequest>, options: Option<JsManyOptions>) -> Group {
        let mut requests = Vec::with_capacity(reqs.len());
        for req in reqs {
            // Conversion errors surface as per-run PromptRequired/InvalidRequest
            // failures, mirroring the core. Fall back to an empty request so the
            // core validation produces the matching error.
            requests.push(to_core_request(req).unwrap_or_default());
        }
        let mut many = ManyOptions::default();
        if let Some(options) = options {
            if let Some(n) = options.max_concurrent {
                many.max_concurrent = n as usize;
            }
            if let Some(n) = options.event_buffer {
                many.event_buffer = Some(n as usize);
            }
        }
        let mut core = self.core.run_many(requests, many).await;
        let stream = core.events();
        Group {
            inner: Arc::new(LiveGroup {
                core,
                stream: Mutex::new(stream),
            }),
        }
    }
}
