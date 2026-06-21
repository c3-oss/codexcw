//! Integration tests ported from the Go `runner_test.go` suite. Unix-only
//! because they drive a fake `codex` shell script through the executable seam.

#![cfg(unix)]

mod common;

use codexcw::{handler, Error, EventKind, EventPayload, ManyOptions, Request, RunOptions, Runner};
use common::{read_args, write_fake_codex};

fn runner_for(fake: &common::FakeCodex) -> Runner {
    Runner::builder()
        .executable(fake.executable())
        .env("CODEXCW_ARGS_FILE", fake.args_file.to_str().unwrap())
        .env("CODEXCW_STDIN_FILE", fake.stdin_file.to_str().unwrap())
        .build()
}

#[tokio::test(flavor = "multi_thread", worker_threads = 2)]
async fn run_decodes_events_and_uses_safe_defaults() {
    let fake = write_fake_codex(
        r#"record_args "$@"
cat > "$CODEXCW_STDIN_FILE"
printf '%s\n' '{"type":"thread.started","thread_id":"thread-1"}'
printf '%s\n' '{"type":"turn.started"}'
printf '%s\n' '{"type":"item.completed","item":{"id":"item_0","type":"agent_message","text":"Oi."}}'
printf '%s\n' '{"type":"turn.completed","usage":{"input_tokens":10,"cached_input_tokens":2,"output_tokens":3,"reasoning_output_tokens":1}}'
"#,
    );

    let result = runner_for(&fake)
        .run(Request::new("diga oi"))
        .await
        .expect("run succeeds");

    assert_eq!(result.thread_id, "thread-1");
    assert_eq!(result.final_message, "Oi.");
    assert_eq!(result.usage.input_tokens, 10);
    assert_eq!(result.events.len(), 4);

    let stdin = std::fs::read_to_string(&fake.stdin_file).unwrap();
    assert_eq!(stdin, "diga oi");

    let args = read_args(&fake.args_file);
    for want in [
        "exec",
        "--json",
        "--color",
        "never",
        "--skip-git-repo-check",
        "--ephemeral",
        "--sandbox",
        "read-only",
        "-c",
        r#"approval_policy="never""#,
    ] {
        assert!(args.contains(&want.to_string()), "missing arg: {want}");
    }
    assert_eq!(args.last().unwrap(), "-");
}

#[tokio::test(flavor = "multi_thread", worker_threads = 2)]
async fn command_execution_failure_is_event_not_run_error() {
    let fake = write_fake_codex(
        r#"record_args "$@"
cat >/dev/null
printf '%s\n' '{"type":"thread.started","thread_id":"thread-2"}'
printf '%s\n' '{"type":"turn.started"}'
printf '%s\n' '{"type":"item.completed","item":{"id":"item_0","type":"command_execution","command":"false","aggregated_output":"boom\n","exit_code":7,"status":"failed"}}'
printf '%s\n' '{"type":"item.completed","item":{"id":"item_1","type":"agent_message","text":"Exit 7"}}'
printf '%s\n' '{"type":"turn.completed","usage":{"input_tokens":1,"output_tokens":1}}'
"#,
    );

    let result = runner_for(&fake)
        .run(Request::new("run false"))
        .await
        .expect("run succeeds");

    assert_eq!(result.events.len(), 5);
    let item = result.events[2].item_completed().unwrap();
    assert_eq!(item.exit_code, Some(7));
    assert_eq!(item.status, "failed");
    assert_eq!(result.final_message, "Exit 7");
}

#[tokio::test(flavor = "multi_thread", worker_threads = 2)]
async fn process_exit_error_carries_stderr_and_last_event() {
    let fake = write_fake_codex(
        r#"record_args "$@"
cat >/dev/null
printf '%s\n' '{"type":"thread.started","thread_id":"thread-3"}'
printf '%s\n' '{"type":"turn.started"}'
printf '%s\n' 'stderr detail' >&2
exit 1
"#,
    );

    let error = runner_for(&fake)
        .run(Request::new("fail"))
        .await
        .expect_err("run fails");

    match error {
        Error::Exit {
            code,
            stderr,
            last_event,
        } => {
            assert_eq!(code, 1);
            assert!(stderr.contains("stderr detail"));
            let last = last_event.expect("last event present");
            assert_eq!(last.kind, EventKind::TurnStarted);
        }
        other => panic!("unexpected error: {other:?}"),
    }
}

#[tokio::test(flavor = "multi_thread", worker_threads = 2)]
async fn codex_event_error_precedes_exit_error() {
    let fake = write_fake_codex(
        r#"record_args "$@"
cat >/dev/null
printf '%s\n' '{"type":"thread.started","thread_id":"thread-3"}'
printf '%s\n' '{"type":"turn.started"}'
printf '%s\n' '{"type":"error","message":"invalid_json_schema: bad model"}'
printf '%s\n' 'stderr detail' >&2
exit 1
"#,
    );

    let error = runner_for(&fake)
        .run(Request::new("fail"))
        .await
        .expect_err("run fails");

    match error {
        Error::Codex { message, event } => {
            assert!(message.contains("invalid_json_schema: bad model"));
            assert!(matches!(event.payload, EventPayload::Error(_)));
        }
        other => panic!("codex error event must take precedence over exit error, got: {other:?}"),
    }
}

#[tokio::test(flavor = "multi_thread", worker_threads = 2)]
async fn decode_error_reports_line_and_raw() {
    let fake = write_fake_codex(
        r#"record_args "$@"
cat >/dev/null
printf '%s\n' 'not-json'
"#,
    );

    let error = runner_for(&fake)
        .run(Request::new("decode"))
        .await
        .expect_err("run fails");

    match error {
        Error::Decode { line, raw, .. } => {
            assert_eq!(line, 1);
            assert_eq!(raw.unwrap(), b"not-json");
        }
        other => panic!("unexpected error: {other:?}"),
    }
}

#[tokio::test(flavor = "multi_thread", worker_threads = 2)]
async fn handler_error_cancels_run() {
    let fake = write_fake_codex(
        r#"record_args "$@"
cat >/dev/null
printf '%s\n' '{"type":"thread.started","thread_id":"thread-4"}'
printf '%s\n' '{"type":"turn.started"}'
sleep 5
"#,
    );

    let opts = RunOptions::with_handler(handler(|event| async move {
        if event.kind == EventKind::TurnStarted {
            Err("stop".to_string())
        } else {
            Ok(())
        }
    }));

    let error = runner_for(&fake)
        .run_opts(Request::new("handler"), opts)
        .await
        .expect_err("run fails");

    match error {
        Error::Handler(message) => assert_eq!(message, "stop"),
        other => panic!("unexpected error: {other:?}"),
    }
}

#[tokio::test(flavor = "multi_thread", worker_threads = 4)]
async fn run_many_collects_results() {
    let fake = write_fake_codex(
        r#"record_args "$@"
cat >/dev/null
printf '%s\n' '{"type":"thread.started","thread_id":"thread-many"}'
printf '%s\n' '{"type":"item.completed","item":{"id":"item_0","type":"agent_message","text":"done"}}'
printf '%s\n' '{"type":"turn.completed","usage":{"input_tokens":1,"output_tokens":1}}'
"#,
    );

    let mut group = runner_for(&fake)
        .run_many(
            vec![Request::new("a"), Request::new("b"), Request::new("c")],
            ManyOptions {
                max_concurrent: 2,
                ..Default::default()
            },
        )
        .await;

    let mut event_count = 0;
    while group.next_event().await.is_some() {
        event_count += 1;
    }

    let results = group.wait().await.expect("group succeeds");
    assert_eq!(results.len(), 3);
    assert!(event_count > 0);
    for result in results {
        let report = result.result.expect("result present");
        assert_eq!(report.final_message, "done");
    }
}

#[tokio::test(flavor = "multi_thread", worker_threads = 2)]
async fn codex_turn_failed_returns_codex_error() {
    let fake = write_fake_codex(
        r#"record_args "$@"
cat >/dev/null
printf '%s\n' '{"type":"thread.started","thread_id":"thread-failed"}'
printf '%s\n' '{"type":"turn.failed","error":{"message":"turn broke"}}'
"#,
    );

    let error = runner_for(&fake)
        .run(Request::new("fail"))
        .await
        .expect_err("run fails");

    match error {
        Error::Codex { message, event } => {
            assert!(message.contains("turn broke"));
            assert!(matches!(event.payload, EventPayload::TurnFailed { .. }));
        }
        other => panic!("unexpected error: {other:?}"),
    }
}

#[tokio::test(flavor = "multi_thread", worker_threads = 2)]
async fn stderr_tail_limit_keeps_trailing_bytes() {
    let fake = write_fake_codex(
        r#"record_args "$@"
cat >/dev/null
printf '%s' '0123456789' >&2
exit 1
"#,
    );

    let runner = Runner::builder()
        .executable(fake.executable())
        .stderr_limit(4)
        .build();

    let error = runner
        .run(Request::new("fail"))
        .await
        .expect_err("run fails");
    match error {
        Error::Exit { stderr, .. } => assert_eq!(stderr, "6789"),
        other => panic!("unexpected error: {other:?}"),
    }
}

#[tokio::test(flavor = "multi_thread", worker_threads = 2)]
async fn run_many_returns_group_error() {
    let fake = write_fake_codex(
        r#"record_args "$@"
cat >/dev/null
printf '%s\n' '{"type":"thread.started","thread_id":"thread-ok"}'
printf '%s\n' '{"type":"turn.completed","usage":{"input_tokens":1,"output_tokens":1}}'
"#,
    );

    let mut group = runner_for(&fake)
        .run_many(
            vec![Request::new("ok"), Request::default()],
            ManyOptions::default(),
        )
        .await;
    while group.next_event().await.is_some() {}

    let error = group.wait().await.expect_err("group fails");
    assert_eq!(error.results.len(), 2);
    assert!(error.to_string().contains("1 codex run(s) failed"));
    let failed = error
        .results
        .iter()
        .find(|r| r.index == 1)
        .expect("second result");
    assert!(matches!(failed.error, Some(Error::PromptRequired)));
}
