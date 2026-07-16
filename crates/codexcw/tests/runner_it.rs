//! Integration tests ported from the Go `runner_test.go` suite. Unix-only
//! because they drive a fake `codex` shell script through the executable seam.

#![cfg(unix)]

mod common;

use codexcw::{handler, Error, EventKind, EventPayload, ManyOptions, Request, RunOptions, Runner};
use common::{read_args, write_fake_codex};
use tokio::time::{timeout, Duration};

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
    assert!(error.to_string().contains("1 agent run(s) failed"));
    let failed = error
        .results
        .iter()
        .find(|r| r.index == 1)
        .expect("second result");
    assert!(matches!(failed.error, Some(Error::PromptRequired)));
}

#[tokio::test(flavor = "multi_thread", worker_threads = 2)]
async fn claude_run_normalizes_events() {
    let fake = write_fake_codex(
        r#"record_args "$@"
cat > "$CODEXCW_STDIN_FILE"
printf '%s\n' '{"type":"system","subtype":"init","cwd":"/work","session_id":"sess-1"}'
printf '%s\n' '{"type":"assistant","message":{"id":"msg_1","content":[{"type":"tool_use","id":"toolu_1","name":"Write","input":{"file_path":"/work/hello.txt","content":"hello"}}]},"session_id":"sess-1"}'
printf '%s\n' '{"type":"rate_limit_event","session_id":"sess-1"}'
printf '%s\n' '{"type":"user","message":{"content":[{"tool_use_id":"toolu_1","type":"tool_result","content":"File created successfully"}]},"session_id":"sess-1","tool_use_result":{"type":"create"}}'
printf '%s\n' '{"type":"assistant","message":{"id":"msg_2","content":[{"type":"text","text":"Done."}]},"session_id":"sess-1"}'
printf '%s\n' '{"type":"result","subtype":"success","is_error":false,"result":"Done.","session_id":"sess-1","usage":{"input_tokens":18,"cache_read_input_tokens":45921,"output_tokens":380}}'
"#,
    );

    let runner = Runner::builder()
        .agent(codexcw::Agent::Claude)
        .executable(fake.executable())
        .env("CODEXCW_ARGS_FILE", fake.args_file.to_str().unwrap())
        .env("CODEXCW_STDIN_FILE", fake.stdin_file.to_str().unwrap())
        .build();

    let result = runner
        .run(Request {
            prompt: "create hello.txt".to_string(),
            model: Some(codexcw::claude_model::HAIKU.to_string()),
            ..Default::default()
        })
        .await
        .expect("run succeeds");

    assert_eq!(result.thread_id, "sess-1");
    assert_eq!(result.final_message, "Done.");
    assert_eq!(result.usage.input_tokens, 18);
    assert_eq!(result.usage.cached_input_tokens, 45921);
    assert_eq!(result.usage.output_tokens, 380);

    let kinds: Vec<String> = result
        .events
        .iter()
        .map(|e| e.kind.as_str().to_string())
        .collect();
    assert_eq!(
        kinds,
        vec![
            "thread.started",
            "turn.started",
            "item.started",
            "rate_limit_event",
            "item.completed",
            "item.completed",
            "turn.completed",
        ]
    );
    for event in &result.events {
        assert_eq!(event.thread_id, "sess-1");
        assert!(!event.raw.is_empty());
    }

    let file_change = result.events[4].item_completed().unwrap();
    assert_eq!(file_change.kind, codexcw::ItemKind::FileChange);
    assert_eq!(file_change.changes[0].kind, "add");
    assert_eq!(file_change.aggregated_output, "File created successfully");

    let stdin = std::fs::read_to_string(&fake.stdin_file).unwrap();
    assert_eq!(stdin, "create hello.txt");

    let args = read_args(&fake.args_file);
    assert_eq!(args[0], "-p");
    for want in [
        "--output-format",
        "stream-json",
        "--verbose",
        "--model",
        "haiku",
        "--no-session-persistence",
    ] {
        assert!(args.contains(&want.to_string()), "missing arg: {want}");
    }
}

#[tokio::test(flavor = "multi_thread", worker_threads = 2)]
async fn claude_error_result_returns_claude_error() {
    let fake = write_fake_codex(
        r#"record_args "$@"
cat >/dev/null
printf '%s\n' '{"type":"system","subtype":"init","session_id":"sess-err"}'
printf '%s\n' '{"type":"result","subtype":"success","is_error":true,"result":"issue with the selected model","session_id":"sess-err","total_cost_usd":0.02,"usage":{"input_tokens":3,"cache_creation_input_tokens":5,"cache_read_input_tokens":7,"output_tokens":11}}'
exit 1
"#,
    );

    let runner = Runner::builder()
        .agent(codexcw::Agent::Claude)
        .executable(fake.executable())
        .build();

    let error = runner.run(Request::new("hi")).await.expect_err("run fails");

    match error {
        Error::Claude { message, event } => {
            assert!(message.starts_with("claude turn failed:"));
            assert!(message.contains("issue with the selected model"));
            match event.payload {
                EventPayload::TurnFailed { usage, .. } => {
                    assert_eq!(usage.total_tokens, 26);
                    assert_eq!(usage.total_cost_usd, 0.02);
                }
                other => panic!("unexpected payload: {other:?}"),
            }
        }
        other => panic!("unexpected error: {other:?}"),
    }
}

#[tokio::test(flavor = "multi_thread", worker_threads = 2)]
async fn session_wait_completes_when_event_buffer_fills() {
    let fake = write_fake_codex(
        r#"record_args "$@"
cat >/dev/null
printf '%s\n' '{"type":"thread.started","thread_id":"thread-buffer"}'
printf '%s\n' '{"type":"turn.started"}'
printf '%s\n' '{"type":"item.completed","item":{"id":"item_0","type":"agent_message","text":"done"}}'
printf '%s\n' '{"type":"turn.completed","usage":{"input_tokens":1,"output_tokens":1}}'
"#,
    );
    let runner = Runner::builder()
        .executable(fake.executable())
        .event_buffer(1)
        .build();
    let mut session = runner
        .start(Request::new("buffer"))
        .await
        .expect("session starts");

    let result = timeout(Duration::from_secs(5), session.wait())
        .await
        .expect("wait must not depend on event consumption")
        .expect("run succeeds");

    assert_eq!(result.events.len(), 4);
    assert_eq!(result.final_message, "done");

    let mut streamed = Vec::new();
    while let Some(event) = session.next_event().await {
        streamed.push(event.kind);
    }
    assert_eq!(
        streamed,
        [
            EventKind::ThreadStarted,
            EventKind::TurnStarted,
            EventKind::ItemCompleted,
            EventKind::TurnCompleted,
        ]
    );
}

#[tokio::test(flavor = "multi_thread", worker_threads = 2)]
async fn session_wait_handles_claude_shaped_events_without_consumption() {
    let fake = write_fake_codex(
        r#"record_args "$@"
cat >/dev/null
printf '%s\n' '{"type":"system","subtype":"init","session_id":"session-buffer"}'
printf '%s\n' '{"type":"rate_limit_event","session_id":"session-buffer"}'
printf '%s\n' '{"type":"result","subtype":"success","result":"done","session_id":"session-buffer"}'
"#,
    );
    let runner = Runner::builder()
        .executable(fake.executable())
        .event_buffer(1)
        .build();
    let session = runner
        .start(Request::new("buffer"))
        .await
        .expect("session starts");

    let result = timeout(Duration::from_secs(5), session.wait())
        .await
        .expect("wait must not depend on event shape")
        .expect("run succeeds");
    let kinds: Vec<&str> = result
        .events
        .iter()
        .map(|event| event.kind.as_str())
        .collect();

    assert_eq!(kinds, ["system", "rate_limit_event", "result"]);
}

#[tokio::test(flavor = "multi_thread", worker_threads = 4)]
async fn group_wait_completes_when_event_buffer_fills() {
    let fake = write_fake_codex(
        r#"record_args "$@"
cat >/dev/null
printf '%s\n' '{"type":"thread.started","thread_id":"thread-group-buffer"}'
printf '%s\n' '{"type":"turn.started"}'
printf '%s\n' '{"type":"item.completed","item":{"id":"item_0","type":"agent_message","text":"done"}}'
printf '%s\n' '{"type":"turn.completed","usage":{"input_tokens":1,"output_tokens":1}}'
"#,
    );
    let mut group = runner_for(&fake)
        .run_many(
            vec![Request::new("a"), Request::new("b")],
            ManyOptions {
                max_concurrent: 2,
                event_buffer: Some(1),
                ..Default::default()
            },
        )
        .await;

    let results = timeout(Duration::from_secs(5), group.wait())
        .await
        .expect("group wait must not depend on event consumption")
        .expect("group succeeds");

    assert_eq!(results.len(), 2);
    assert!(results.iter().all(|result| result
        .result
        .as_ref()
        .is_some_and(|report| report.events.len() == 4)));

    let mut event_count = 0;
    while group.next_event().await.is_some() {
        event_count += 1;
    }
    assert_eq!(event_count, 8);
}

#[tokio::test(flavor = "multi_thread", worker_threads = 2)]
async fn group_cancel_releases_forwarding_blocked_by_backpressure() {
    let fake = write_fake_codex(
        r#"record_args "$@"
cat >/dev/null
printf '%s\n' '{"type":"thread.started","thread_id":"thread-group-cancel"}'
printf '%s\n' '{"type":"turn.started"}'
printf '%s\n' '{"type":"item.completed","item":{"id":"item_0","type":"agent_message","text":"working"}}'
mkfifo "$CODEXCW_WAIT_FIFO"
read -r _ < "$CODEXCW_WAIT_FIFO"
"#,
    );
    let wait_fifo = fake.stdin_file.with_file_name("wait.fifo");
    let runner = Runner::builder()
        .executable(fake.executable())
        .env("CODEXCW_WAIT_FIFO", wait_fifo.to_str().unwrap())
        .build();
    let mut group = runner
        .run_many(
            vec![Request::new("cancel")],
            ManyOptions {
                event_buffer: Some(1),
                ..Default::default()
            },
        )
        .await;

    timeout(Duration::from_secs(5), group.next_event())
        .await
        .expect("first event must arrive")
        .expect("event stream remains open");
    tokio::time::sleep(Duration::from_millis(100)).await;
    group.cancel();

    let error = timeout(Duration::from_secs(5), group.wait())
        .await
        .expect("cancelled group must finish under backpressure")
        .expect_err("group is cancelled");
    assert!(matches!(error.results[0].error, Some(Error::Cancelled)));

    let mut remaining = 0;
    loop {
        match timeout(Duration::from_secs(2), group.next_event()).await {
            Ok(Some(_)) => remaining += 1,
            Ok(None) => break,
            Err(_) => panic!("cancelled forwarder must close the event stream"),
        }
    }
    assert!(
        remaining <= 1,
        "cancelled forwarder delivered {remaining} queued events"
    );
}

#[tokio::test(flavor = "multi_thread", worker_threads = 2)]
async fn event_stream_preserves_order_with_small_buffer() {
    let fake = write_fake_codex(
        r#"record_args "$@"
cat >/dev/null
printf '%s\n' '{"type":"thread.started","thread_id":"thread-order"}'
printf '%s\n' '{"type":"turn.started"}'
printf '%s\n' '{"type":"item.started","item":{"id":"item_0","type":"agent_message"}}'
printf '%s\n' '{"type":"item.completed","item":{"id":"item_0","type":"agent_message","text":"done"}}'
printf '%s\n' '{"type":"turn.completed","usage":{"input_tokens":1,"output_tokens":1}}'
"#,
    );
    let runner = Runner::builder()
        .executable(fake.executable())
        .event_buffer(1)
        .build();
    let mut session = runner
        .start(Request::new("order"))
        .await
        .expect("session starts");
    let mut kinds = Vec::new();

    while let Some(event) = session.next_event().await {
        kinds.push(event.kind);
    }
    session.wait().await.expect("run succeeds");

    assert_eq!(
        kinds,
        [
            EventKind::ThreadStarted,
            EventKind::TurnStarted,
            EventKind::ItemStarted,
            EventKind::ItemCompleted,
            EventKind::TurnCompleted,
        ]
    );
}

#[tokio::test(flavor = "multi_thread", worker_threads = 2)]
async fn session_wait_handles_large_event_burst_without_consumption() {
    let fake = write_fake_codex(
        r#"record_args "$@"
cat >/dev/null
i=0
while [ "$i" -lt 4096 ]; do
  printf '{"type":"burst.%s"}\n' "$i"
  i=$((i + 1))
done
"#,
    );
    let runner = Runner::builder()
        .executable(fake.executable())
        .event_buffer(1)
        .build();
    let session = runner
        .start(Request::new("burst"))
        .await
        .expect("session starts");

    let result = timeout(Duration::from_secs(5), session.wait())
        .await
        .expect("large burst must not block completion")
        .expect("run succeeds");

    assert_eq!(result.events.len(), 4096);
    assert_eq!(result.events[0].kind.as_str(), "burst.0");
    assert_eq!(result.events[4095].kind.as_str(), "burst.4095");
}
