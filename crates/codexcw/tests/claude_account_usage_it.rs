//! Claude account-usage tests driven by a fake executable.

#![cfg(unix)]

mod common;

use std::time::Duration;

use codexcw::{get_claude_account_usage, ClaudeAccountUsageRequest, Error};

use common::{read_args, write_fake_codex};

#[tokio::test]
async fn reads_claude_usage_report_and_windows() {
    let fake = write_fake_codex(
        r#"record_args "$@"
cat > "$CODEXCW_STDIN_FILE"
printf '%s\n' '{"type":"result","subtype":"success","is_error":false,"result":"You are currently using your subscription\n\nCurrent session: 13% used · resets Jul 16 at 3:50pm (America/Sao_Paulo)\nCurrent week (all models): 5% used · resets Jul 18 at 9am (America/Sao_Paulo)"}'
"#,
    );

    let usage = get_claude_account_usage(ClaudeAccountUsageRequest {
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
        ],
        timeout: Some(Duration::from_secs(1)),
    })
    .await
    .expect("usage succeeds");

    assert!(usage.report.starts_with("You are currently"));
    assert_eq!(usage.windows.len(), 2);
    assert_eq!(usage.windows[0].label, "Current session");
    assert_eq!(usage.windows[0].used_percent, 13.0);
    assert_eq!(
        usage.windows[0].resets_at,
        "Jul 16 at 3:50pm (America/Sao_Paulo)"
    );
    assert!(usage.raw.contains(r#""is_error":false"#));
    assert_eq!(
        read_args(&fake.args_file),
        ["-p", "--output-format", "json", "--no-session-persistence"]
    );
    assert_eq!(std::fs::read_to_string(&fake.stdin_file).unwrap(), "/usage");
}

#[tokio::test]
async fn reports_claude_usage_process_failures() {
    let fake = write_fake_codex(
        r#"cat >/dev/null
printf '%s\n' 'not available' >&2
exit 7
"#,
    );

    let error = get_claude_account_usage(ClaudeAccountUsageRequest {
        executable: Some(fake.executable().to_string()),
        ..Default::default()
    })
    .await
    .expect_err("usage fails");

    match error {
        Error::Exit { code, stderr, .. } => {
            assert_eq!(code, 7);
            assert_eq!(stderr, "not available");
        }
        other => panic!("unexpected error: {other:?}"),
    }
}

#[tokio::test]
async fn preserves_reported_claude_usage_errors() {
    let fake = write_fake_codex(
        r#"cat >/dev/null
printf '%s\n' '{"type":"result","is_error":true,"result":"","errors":["subscription unavailable","try later"]}'
"#,
    );

    let error = get_claude_account_usage(ClaudeAccountUsageRequest {
        executable: Some(fake.executable().to_string()),
        ..Default::default()
    })
    .await
    .expect_err("usage fails");

    assert!(
        matches!(error, Error::Process(message) if message.contains("subscription unavailable; try later"))
    );
}

#[tokio::test]
async fn applies_custom_claude_usage_timeout() {
    let fake = write_fake_codex(
        r#"cat >/dev/null
sleep 1
"#,
    );

    let error = get_claude_account_usage(ClaudeAccountUsageRequest {
        executable: Some(fake.executable().to_string()),
        timeout: Some(Duration::from_millis(20)),
        ..Default::default()
    })
    .await
    .expect_err("usage times out");

    assert!(matches!(
        error,
        Error::Process(message) if message.contains("timed out")
    ));
}

#[tokio::test]
async fn live_claude_account_usage() {
    if std::env::var("CODEXCW_LIVE_CLAUDE").as_deref() != Ok("1") {
        return;
    }

    let usage = get_claude_account_usage(ClaudeAccountUsageRequest::default())
        .await
        .expect("live Claude account usage succeeds");

    assert!(!usage.report.is_empty());
    assert!(!usage.raw.is_empty());
    assert!(!usage.windows.is_empty());
}
