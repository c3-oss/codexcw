//! Account usage and limits from `codex app-server`.

use std::collections::HashMap;
use std::path::PathBuf;
use std::process::Stdio;
use std::time::Duration;

use serde::Deserialize;
use serde_json::{json, value::RawValue, Value};
use tokio::io::{AsyncBufReadExt, AsyncWriteExt, BufReader, Lines};
use tokio::process::{Child, ChildStdout, Command};
use tokio::time::timeout;

use crate::Error;

const INIT_TIMEOUT: Duration = Duration::from_secs(8);
const REQUEST_TIMEOUT: Duration = Duration::from_secs(3);

/// Configures one Codex account usage lookup.
#[derive(Clone, Debug, Default)]
pub struct AccountUsageRequest {
    /// Codex executable path. Defaults to `codex`.
    pub executable: Option<String>,
    /// Environment variables for the Codex app-server child process.
    pub env: Vec<(String, String)>,
}

/// Account limits and credits reported by Codex app-server.
#[derive(Clone, Debug, Default)]
pub struct AccountUsage {
    /// Authenticated account when Codex reports it.
    pub account: Option<AccountUsageAccount>,
    /// Account token-usage summary when Codex reports it.
    pub token_usage: Option<AccountTokenUsage>,
    /// Primary account rate-limit payload.
    pub rate_limits: AccountRateLimits,
    /// Additional named rate-limit payloads.
    pub rate_limits_by_limit_id: HashMap<String, AccountRateLimits>,
    /// Raw JSON-RPC result for `account/rateLimits/read`.
    pub raw_rate_limits: String,
    /// Raw JSON-RPC result for `account/usage/read` when available.
    pub raw_token_usage: Option<String>,
    /// Raw JSON-RPC result for `account/read` when available.
    pub raw_account: Option<String>,
}

/// Authenticated account reported by Codex.
#[derive(Clone, Debug, Default, Deserialize)]
pub struct AccountUsageAccount {
    /// Account type, such as `chatgpt` or `apikey`.
    #[serde(default, rename = "type", deserialize_with = "deserialize_string")]
    pub kind: String,
    /// ChatGPT account email when available.
    #[serde(default, deserialize_with = "deserialize_string")]
    pub email: String,
    /// ChatGPT plan type when available.
    #[serde(default, alias = "planType", deserialize_with = "deserialize_string")]
    pub plan_type: String,
    /// Whether Codex reports that OpenAI auth is required.
    #[serde(
        default,
        alias = "requiresOpenaiAuth",
        deserialize_with = "deserialize_bool"
    )]
    pub requires_openai_auth: bool,
}

/// One Codex rate-limit set.
#[derive(Clone, Debug, Default, Deserialize)]
pub struct AccountRateLimits {
    /// Optional machine identifier for this limit.
    #[serde(default, alias = "limitId", deserialize_with = "deserialize_string")]
    pub limit_id: String,
    /// Optional display name for this limit.
    #[serde(default, alias = "limitName", deserialize_with = "deserialize_string")]
    pub limit_name: String,
    /// Short rolling usage window when available.
    #[serde(default)]
    pub primary: Option<AccountRateLimitWindow>,
    /// Longer usage window when available.
    #[serde(default)]
    pub secondary: Option<AccountRateLimitWindow>,
    /// Account credit balance when available.
    #[serde(default)]
    pub credits: Option<AccountCredits>,
    /// Account spend or credit control limit when available.
    #[serde(default, alias = "individualLimit")]
    pub individual_limit: Option<AccountSpendLimit>,
    /// Plan type associated with this limit set.
    #[serde(default, alias = "planType", deserialize_with = "deserialize_string")]
    pub plan_type: String,
    /// Which limit was reached when Codex reports it.
    #[serde(
        default,
        alias = "rateLimitReachedType",
        deserialize_with = "deserialize_string"
    )]
    pub rate_limit_reached_type: String,
}

/// One account usage window.
#[derive(Clone, Debug, Default, Deserialize)]
pub struct AccountRateLimitWindow {
    /// Percentage of the window already used.
    #[serde(default, alias = "usedPercent", deserialize_with = "deserialize_f64")]
    pub used_percent: f64,
    /// Window duration in minutes when available.
    #[serde(
        default,
        alias = "windowDurationMins",
        deserialize_with = "deserialize_i64"
    )]
    pub window_duration_mins: i64,
    /// Unix timestamp in seconds when available.
    #[serde(default, alias = "resetsAt", deserialize_with = "deserialize_i64")]
    pub resets_at: i64,
}

/// Codex credit balance snapshot.
#[derive(Clone, Debug, Default, Deserialize)]
pub struct AccountCredits {
    /// Whether the account has a credit bucket.
    #[serde(default, alias = "hasCredits", deserialize_with = "deserialize_bool")]
    pub has_credits: bool,
    /// Whether credits are unlimited.
    #[serde(default, deserialize_with = "deserialize_bool")]
    pub unlimited: bool,
    /// Remaining credit balance when available.
    #[serde(default, deserialize_with = "deserialize_optional_string")]
    pub balance: Option<String>,
}

/// Individual spend or credit-control limit.
#[derive(Clone, Debug, Default, Deserialize)]
pub struct AccountSpendLimit {
    /// Configured limit when available.
    #[serde(default, deserialize_with = "deserialize_f64")]
    pub limit: f64,
    /// Consumed amount when available.
    #[serde(default, deserialize_with = "deserialize_f64")]
    pub used: f64,
    /// Remaining percentage when available.
    #[serde(
        default,
        alias = "remainingPercent",
        deserialize_with = "deserialize_f64"
    )]
    pub remaining_percent: f64,
    /// Unix timestamp in seconds when available.
    #[serde(default, alias = "resetsAt", deserialize_with = "deserialize_i64")]
    pub resets_at: i64,
}

/// Account token-usage summary reported by Codex.
#[derive(Clone, Debug, Default, Deserialize)]
pub struct AccountTokenUsage {
    /// Aggregate account token-usage metrics.
    #[serde(default)]
    pub summary: AccountTokenUsageSummary,
    /// Per-day token usage when available.
    #[serde(
        default,
        alias = "dailyUsageBuckets",
        deserialize_with = "deserialize_usage_buckets"
    )]
    pub daily_usage_buckets: Vec<AccountTokenUsageDailyBucket>,
}

/// Aggregate account token-usage metrics.
#[derive(Clone, Debug, Default, Deserialize)]
pub struct AccountTokenUsageSummary {
    /// Total lifetime token count when available.
    #[serde(
        default,
        alias = "lifetimeTokens",
        deserialize_with = "deserialize_optional_string"
    )]
    pub lifetime_tokens: Option<String>,
    /// Peak daily token count when available.
    #[serde(
        default,
        alias = "peakDailyTokens",
        deserialize_with = "deserialize_optional_string"
    )]
    pub peak_daily_tokens: Option<String>,
    /// Longest running turn duration when available.
    #[serde(
        default,
        alias = "longestRunningTurnSec",
        deserialize_with = "deserialize_optional_string"
    )]
    pub longest_running_turn_sec: Option<String>,
    /// Current usage streak length when available.
    #[serde(
        default,
        alias = "currentStreakDays",
        deserialize_with = "deserialize_optional_string"
    )]
    pub current_streak_days: Option<String>,
    /// Longest usage streak length when available.
    #[serde(
        default,
        alias = "longestStreakDays",
        deserialize_with = "deserialize_optional_string"
    )]
    pub longest_streak_days: Option<String>,
}

/// One daily account token-usage bucket.
#[derive(Clone, Debug, Default, Deserialize)]
pub struct AccountTokenUsageDailyBucket {
    /// Bucket start date.
    #[serde(default, alias = "startDate", deserialize_with = "deserialize_string")]
    pub start_date: String,
    /// Token count for the bucket.
    #[serde(default, deserialize_with = "deserialize_string")]
    pub tokens: String,
}

#[derive(Debug, Deserialize)]
struct RateLimitsResponse {
    #[serde(default, alias = "rateLimits", alias = "rate_limits")]
    rate_limits: AccountRateLimits,
    #[serde(
        default,
        alias = "rate_limits_by_limit_id",
        alias = "rateLimitsByLimitId",
        deserialize_with = "deserialize_rate_limits_by_limit_id"
    )]
    rate_limits_by_limit_id: HashMap<String, AccountRateLimits>,
}

#[derive(Debug, Deserialize)]
struct AccountResponse {
    #[serde(default)]
    account: Option<AccountUsageAccount>,
    #[serde(
        default,
        alias = "requiresOpenaiAuth",
        deserialize_with = "deserialize_bool"
    )]
    requires_openai_auth: bool,
}

#[derive(Debug, Deserialize)]
struct RpcMessage {
    id: Option<u64>,
    result: Option<Box<RawValue>>,
    error: Option<RpcError>,
}

#[derive(Debug, Deserialize)]
struct RpcError {
    message: Option<String>,
}

/// Reads Codex account usage and limits through `codex app-server`.
pub async fn get_account_usage(req: AccountUsageRequest) -> Result<AccountUsage, Error> {
    let executable = req
        .executable
        .as_deref()
        .filter(|s| !s.is_empty())
        .unwrap_or("codex");
    let env = account_usage_env(&req.env);

    let mut child = Command::new(executable)
        .args([
            "-s",
            "read-only",
            "-a",
            "untrusted",
            "app-server",
            "--stdio",
        ])
        .envs(env)
        .stdin(Stdio::piped())
        .stdout(Stdio::piped())
        .stderr(Stdio::null())
        .kill_on_drop(true)
        .spawn()
        .map_err(|err| Error::Process(format!("start codex app-server: {err}")))?;

    let mut stdin = child
        .stdin
        .take()
        .ok_or_else(|| Error::Process("open codex app-server stdin".to_string()))?;
    let stdout = child
        .stdout
        .take()
        .ok_or_else(|| Error::Process("open codex app-server stdout".to_string()))?;
    let mut lines = BufReader::new(stdout).lines();
    let mut next_id = 0_u64;

    rpc_request(
        &mut child,
        &mut stdin,
        &mut lines,
        &mut next_id,
        "initialize",
        Some(json!({"clientInfo":{"name":"codexcw","version":"0.1.0"}})),
        INIT_TIMEOUT,
    )
    .await
    .map_err(|err| Error::Process(format!("initialize codex app-server: {err}")))?;
    rpc_notify(&mut stdin, "initialized", json!({})).await?;

    let raw_rate_limits = rpc_request(
        &mut child,
        &mut stdin,
        &mut lines,
        &mut next_id,
        "account/rateLimits/read",
        None,
        REQUEST_TIMEOUT,
    )
    .await
    .map_err(|err| Error::Process(format!("read codex account rate limits: {err}")))?;
    let rate_limits: RateLimitsResponse = serde_json::from_str(raw_rate_limits.get())
        .map_err(|err| Error::Process(format!("decode codex account rate limits: {err}")))?;

    let raw_token_usage = rpc_request(
        &mut child,
        &mut stdin,
        &mut lines,
        &mut next_id,
        "account/usage/read",
        None,
        REQUEST_TIMEOUT,
    )
    .await
    .ok();
    let token_usage = raw_token_usage
        .as_ref()
        .and_then(|raw| serde_json::from_str::<AccountTokenUsage>(raw.get()).ok());

    let raw_account = rpc_request(
        &mut child,
        &mut stdin,
        &mut lines,
        &mut next_id,
        "account/read",
        Some(json!({})),
        REQUEST_TIMEOUT,
    )
    .await
    .ok();
    let account = raw_account
        .as_ref()
        .and_then(|raw| serde_json::from_str::<AccountResponse>(raw.get()).ok())
        .and_then(|response| {
            response.account.map(|mut account| {
                account.requires_openai_auth = response.requires_openai_auth;
                account
            })
        });

    let _ = child.kill().await;
    let _ = child.wait().await;

    Ok(AccountUsage {
        account,
        token_usage,
        rate_limits: rate_limits.rate_limits,
        rate_limits_by_limit_id: rate_limits.rate_limits_by_limit_id,
        raw_rate_limits: raw_rate_limits.get().to_string(),
        raw_token_usage: raw_token_usage.map(|raw| raw.get().to_string()),
        raw_account: raw_account.map(|raw| raw.get().to_string()),
    })
}

async fn rpc_request(
    child: &mut Child,
    stdin: &mut tokio::process::ChildStdin,
    lines: &mut Lines<BufReader<ChildStdout>>,
    next_id: &mut u64,
    method: &str,
    params: Option<Value>,
    duration: Duration,
) -> Result<Box<RawValue>, Error> {
    *next_id += 1;
    let id = *next_id;
    let mut payload = json!({"id": id, "method": method});
    if let Some(params) = params {
        payload["params"] = params;
    }
    write_rpc(stdin, payload).await?;

    loop {
        let line = match timeout(duration, lines.next_line()).await {
            Ok(Ok(Some(line))) => line,
            Ok(Ok(None)) => {
                return Err(Error::Process("codex app-server closed stdout".to_string()))
            }
            Ok(Err(err)) => return Err(Error::Process(format!("read codex app-server: {err}"))),
            Err(_) => {
                let _ = child.kill().await;
                return Err(Error::Process(format!(
                    "codex app-server JSON-RPC timeout waiting for {method}"
                )));
            }
        };
        let message: RpcMessage = serde_json::from_str(&line)
            .map_err(|err| Error::Process(format!("decode codex app-server JSON-RPC: {err}")))?;
        if message.id != Some(id) {
            continue;
        }
        if let Some(error) = message.error {
            return Err(Error::Process(
                error
                    .message
                    .unwrap_or_else(|| "codex app-server JSON-RPC error".to_string()),
            ));
        }
        return message
            .result
            .ok_or_else(|| Error::Process("codex app-server response missing result".to_string()));
    }
}

async fn rpc_notify(
    stdin: &mut tokio::process::ChildStdin,
    method: &str,
    params: Value,
) -> Result<(), Error> {
    write_rpc(stdin, json!({"method": method, "params": params})).await
}

async fn write_rpc(stdin: &mut tokio::process::ChildStdin, value: Value) -> Result<(), Error> {
    let mut data = serde_json::to_vec(&value)
        .map_err(|err| Error::Process(format!("encode codex app-server JSON-RPC: {err}")))?;
    data.push(b'\n');
    stdin
        .write_all(&data)
        .await
        .map_err(|err| Error::Process(format!("write codex app-server: {err}")))
}

fn account_usage_env(overrides: &[(String, String)]) -> HashMap<String, String> {
    let mut env: HashMap<String, String> = std::env::vars().collect();
    for (key, value) in overrides {
        env.insert(key.clone(), value.clone());
    }
    if env
        .get("CODEX_HOME")
        .map(|value| value.trim().is_empty())
        .unwrap_or(true)
    {
        if let Some(home) = default_home_dir() {
            env.insert(
                "CODEX_HOME".to_string(),
                home.join(".codex").to_string_lossy().to_string(),
            );
        }
    }
    env
}

fn default_home_dir() -> Option<PathBuf> {
    std::env::var_os("HOME")
        .filter(|value| !value.is_empty())
        .map(PathBuf::from)
        .or_else(|| {
            std::env::var_os("USERPROFILE")
                .filter(|value| !value.is_empty())
                .map(PathBuf::from)
        })
}

fn deserialize_optional_string<'de, D>(deserializer: D) -> Result<Option<String>, D::Error>
where
    D: serde::Deserializer<'de>,
{
    let value = Option::<Value>::deserialize(deserializer)?;
    Ok(value.and_then(|value| match value {
        Value::String(text) => Some(text),
        Value::Number(number) => Some(number.to_string()),
        _ => None,
    }))
}

fn deserialize_string<'de, D>(deserializer: D) -> Result<String, D::Error>
where
    D: serde::Deserializer<'de>,
{
    Ok(deserialize_optional_string(deserializer)?.unwrap_or_default())
}

fn deserialize_bool<'de, D>(deserializer: D) -> Result<bool, D::Error>
where
    D: serde::Deserializer<'de>,
{
    let value = Option::<Value>::deserialize(deserializer)?;
    Ok(match value {
        Some(Value::Bool(value)) => value,
        Some(Value::String(text)) => matches!(text.trim(), "true" | "1"),
        Some(Value::Number(number)) => number.as_i64().unwrap_or_default() != 0,
        _ => false,
    })
}

fn deserialize_rate_limits_by_limit_id<'de, D>(
    deserializer: D,
) -> Result<HashMap<String, AccountRateLimits>, D::Error>
where
    D: serde::Deserializer<'de>,
{
    Ok(
        Option::<HashMap<String, AccountRateLimits>>::deserialize(deserializer)?
            .unwrap_or_default(),
    )
}

fn deserialize_usage_buckets<'de, D>(
    deserializer: D,
) -> Result<Vec<AccountTokenUsageDailyBucket>, D::Error>
where
    D: serde::Deserializer<'de>,
{
    Ok(Option::<Vec<AccountTokenUsageDailyBucket>>::deserialize(deserializer)?.unwrap_or_default())
}

fn deserialize_f64<'de, D>(deserializer: D) -> Result<f64, D::Error>
where
    D: serde::Deserializer<'de>,
{
    let value = Option::<Value>::deserialize(deserializer)?;
    Ok(match value {
        Some(Value::Number(number)) => number.as_f64().unwrap_or_default(),
        Some(Value::String(text)) => text.trim().parse().unwrap_or_default(),
        _ => 0.0,
    })
}

fn deserialize_i64<'de, D>(deserializer: D) -> Result<i64, D::Error>
where
    D: serde::Deserializer<'de>,
{
    let value = Option::<Value>::deserialize(deserializer)?;
    Ok(match value {
        Some(Value::Number(number)) => number.as_i64().unwrap_or_default(),
        Some(Value::String(text)) => text.trim().parse().unwrap_or_default(),
        _ => 0,
    })
}
