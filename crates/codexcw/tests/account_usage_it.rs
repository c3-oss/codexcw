//! Account-usage tests driven by a fake `codex app-server`.

#![cfg(unix)]

mod common;

use std::time::Duration;

use codexcw::{get_account_usage, AccountUsageRequest, ConfigOverride, Error, Request, Runner};
use common::{read_args, write_fake_codex};

#[tokio::test(flavor = "multi_thread", worker_threads = 2)]
async fn account_usage_reads_rate_limits_and_account() {
    let fake = write_fake_codex(
        r#"record_args "$@"
printf '%s\n' "$CODEX_HOME" > "$CODEXCW_STDIN_FILE"
while IFS= read -r line; do
  case "$line" in
    *'"method":"initialized"'*) ;;
    *'"method":"initialize"'*) printf '%s\n' '{"id":1,"result":{}}' ;;
    *'"method":"account/rateLimits/read"'*)
      printf '%s\n' '{"id":2,"method":"loginChatGptComplete","params":{}}'
      printf '%s\n' '{"id":2,"result":{"rateLimits":{"limitId":null,"limitName":null,"planType":"pro","rateLimitReachedType":null,"primary":{"usedPercent":12.5,"windowDurationMins":300,"resetsAt":1766948068},"secondary":{"usedPercent":43,"windowDurationMins":10080,"resetsAt":1767407914},"credits":{"hasCredits":true,"unlimited":false,"balance":"7"},"individualLimit":{"limit":"100","used":25,"remainingPercent":"75","resetsAt":"1768000000"}},"rateLimitsByLimitId":{"spark":{"limitName":"Codex Spark","primary":{"usedPercent":8,"windowDurationMins":300,"resetsAt":1767000000}}}}}'
      ;;
    *'"method":"account/usage/read"'*)
      printf '%s\n' '{"id":3,"result":{"summary":{"lifetimeTokens":"12345678901234567890","peakDailyTokens":456,"longestRunningTurnSec":"789","currentStreakDays":3,"longestStreakDays":"9"},"dailyUsageBuckets":[{"startDate":"2026-07-07","tokens":"42"}]}}'
      ;;
    *'"method":"account/read"'*)
      case "$line" in
        *'"params"'*) printf '%s\n' '{"id":4,"result":{"account":{"type":"chatgpt","email":"stub@example.com","planType":"pro"},"requiresOpenaiAuth":false}}' ;;
        *) printf '%s\n' '{"id":4,"error":{"code":-32600,"message":"Invalid request: missing field `params`"}}' ;;
      esac
      ;;
  esac
done
"#,
    );

    let usage = get_account_usage(AccountUsageRequest {
        executable: Some(fake.executable().to_string()),
        env: vec![
            (
                "CODEXCW_ARGS_FILE".to_string(),
                fake.args_file.to_string_lossy().to_string(),
            ),
            (
                "CODEXCW_STDIN_FILE".to_string(),
                fake.stdin_file.to_string_lossy().to_string(),
            ),
            ("CODEX_HOME".to_string(), "/tmp/codexcw-home".to_string()),
        ],
        ..Default::default()
    })
    .await
    .expect("usage succeeds");

    assert_eq!(usage.account.as_ref().unwrap().email, "stub@example.com");
    assert_eq!(usage.rate_limits.plan_type, "pro");
    assert_eq!(
        usage.rate_limits.primary.as_ref().unwrap().used_percent,
        12.5
    );
    assert_eq!(
        usage
            .rate_limits
            .individual_limit
            .as_ref()
            .unwrap()
            .remaining_percent,
        75.0
    );
    assert_eq!(
        usage
            .rate_limits_by_limit_id
            .get("spark")
            .unwrap()
            .limit_name,
        "Codex Spark"
    );
    assert_eq!(
        usage
            .token_usage
            .as_ref()
            .unwrap()
            .summary
            .lifetime_tokens
            .as_deref(),
        Some("12345678901234567890")
    );
    assert_eq!(
        usage.token_usage.as_ref().unwrap().daily_usage_buckets[0].tokens,
        "42"
    );
    assert!(usage.raw_rate_limits.contains("rateLimits"));
    assert!(usage
        .raw_token_usage
        .as_ref()
        .unwrap()
        .contains("lifetimeTokens"));
    assert!(usage.raw_account.unwrap().contains("stub@example.com"));

    let args = read_args(&fake.args_file);
    assert_eq!(
        args,
        [
            "-s",
            "read-only",
            "-a",
            "untrusted",
            "app-server",
            "--stdio"
        ]
    );
    assert_eq!(
        std::fs::read_to_string(&fake.stdin_file).unwrap(),
        "/tmp/codexcw-home\n"
    );
}

#[tokio::test(flavor = "multi_thread", worker_threads = 2)]
async fn account_usage_surfaces_rpc_error() {
    let fake = write_fake_codex(
        r#"while IFS= read -r line; do
  case "$line" in
    *'"method":"initialized"'*) ;;
    *'"method":"initialize"'*) printf '%s\n' '{"id":1,"result":{}}' ;;
    *'"method":"account/rateLimits/read"'*) printf '%s\n' '{"id":2,"error":{"message":"login required"}}' ;;
  esac
done
"#,
    );

    let error = get_account_usage(AccountUsageRequest {
        executable: Some(fake.executable().to_string()),
        ..Default::default()
    })
    .await
    .expect_err("usage fails");

    match error {
        Error::Process(message) => assert!(message.contains("login required")),
        other => panic!("unexpected error: {other}"),
    }
}

#[tokio::test(flavor = "multi_thread", worker_threads = 2)]
async fn account_usage_treats_optional_rpc_errors_as_absent() {
    let fake = write_fake_codex(
        r#"while IFS= read -r line; do
  case "$line" in
    *'"method":"initialized"'*) ;;
    *'"method":"initialize"'*) printf '%s\n' '{"id":1,"result":{}}' ;;
    *'"method":"account/rateLimits/read"'*) printf '%s\n' '{"id":2,"result":{"rateLimits":{"planType":"pro"}}}' ;;
    *'"method":"account/usage/read"'*) printf '%s\n' '{"id":3,"error":{"code":-32601,"message":"Method not found"}}' ;;
    *'"method":"account/read"'*) printf '%s\n' '{"id":4,"error":{"code":-32601,"message":"Method not found"}}' ;;
  esac
done
"#,
    );

    let usage = get_account_usage(AccountUsageRequest {
        executable: Some(fake.executable().to_string()),
        ..Default::default()
    })
    .await
    .expect("usage succeeds");

    assert_eq!(usage.rate_limits.plan_type, "pro");
    assert!(usage.account.is_none());
    assert!(usage.token_usage.is_none());
    assert!(usage.raw_token_usage.is_none());
    assert!(usage.raw_account.is_none());
}

#[tokio::test(flavor = "multi_thread", worker_threads = 2)]
async fn account_usage_custom_timeout_fails_slow_optional_read() {
    let fake = write_fake_codex(
        r#"while IFS= read -r line; do
  case "$line" in
    *'"method":"initialized"'*) ;;
    *'"method":"initialize"'*) printf '%s\n' '{"id":1,"result":{}}' ;;
    *'"method":"account/rateLimits/read"'*) printf '%s\n' '{"id":2,"result":{"rateLimits":{"planType":"pro"}}}' ;;
    *'"method":"account/usage/read"'*) sleep 3; printf '%s\n' '{"id":3,"result":{}}' ;;
  esac
done
"#,
    );

    let error = get_account_usage(AccountUsageRequest {
        executable: Some(fake.executable().to_string()),
        timeout: Some(Duration::from_millis(200)),
        ..Default::default()
    })
    .await
    .expect_err("usage fails");

    match error {
        Error::Process(message) => assert!(
            message.contains("timeout waiting for account/usage/read"),
            "unexpected message: {message}"
        ),
        other => panic!("unexpected error: {other}"),
    }
}

#[tokio::test(flavor = "multi_thread", worker_threads = 2)]
async fn account_usage_attaches_stderr_to_errors() {
    let fake = write_fake_codex(
        r#"echo 'boom from codex' >&2
while IFS= read -r line; do
  case "$line" in
    *'"method":"initialized"'*) ;;
    *'"method":"initialize"'*) printf '%s\n' '{"id":1,"result":{}}' ;;
    *'"method":"account/rateLimits/read"'*) exit 1 ;;
  esac
done
"#,
    );

    let error = get_account_usage(AccountUsageRequest {
        executable: Some(fake.executable().to_string()),
        ..Default::default()
    })
    .await
    .expect_err("usage fails");

    match error {
        Error::Process(message) => assert!(
            message.contains("boom from codex"),
            "unexpected message: {message}"
        ),
        other => panic!("unexpected error: {other}"),
    }
}

#[tokio::test(flavor = "multi_thread", worker_threads = 2)]
async fn live_account_usage_and_fast_mode() {
    if std::env::var("CODEXCW_LIVE_CODEX").as_deref() != Ok("1") {
        return;
    }

    let usage = get_account_usage(AccountUsageRequest::default())
        .await
        .expect("live account usage succeeds");
    assert!(!usage.raw_rate_limits.is_empty());

    let dir = tempfile::tempdir().expect("temp dir");
    let result = Runner::new()
        .run(Request {
            prompt: "Responda exatamente: OK".to_string(),
            dir: Some(dir.path().to_string_lossy().to_string()),
            ignore_rules: true,
            config: vec![ConfigOverride::new("service_tier", "\"priority\"")],
            ..Default::default()
        })
        .await
        .expect("live fast-mode run succeeds");

    assert!(result.final_message.to_uppercase().contains("OK"));
}
