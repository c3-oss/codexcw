//! Grok Build CLI integration tests using a fake executable and an opt-in live run.

#![cfg(unix)]

mod common;

use codexcw::{Agent, Error, EventPayload, ItemKind, Request, Runner};
use common::{read_args, write_fake_codex};

#[tokio::test(flavor = "multi_thread", worker_threads = 2)]
async fn grok_run_normalizes_events_and_uses_prompt_file() {
    let fake = write_fake_codex(
        r#"record_args "$@"
prompt_file=
previous=
for arg in "$@"; do
  if [ "$previous" = "--prompt-file" ]; then
    prompt_file=$arg
  fi
  previous=$arg
done
cat "$prompt_file" > "$CODEXCW_STDIN_FILE"
printf '%s\n' '{"type":"system","subtype":"init","session_id":"grok-session"}'
printf '%s\n' '{"type":"assistant","message":{"id":"msg_1","content":[{"type":"thinking","thinking":"Checking."},{"type":"tool_use","id":"tool_1","name":"run_terminal_command","input":{"command":"printf hi","description":"Print text"}}]},"session_id":"grok-session"}'
printf '%s\n' '{"type":"user","message":{"content":[{"type":"tool_result","tool_use_id":"tool_1","content":"{\"type\":\"Bash\",\"output\":[104,105,10],\"output_for_prompt\":\"exit: 0\\nhi\\n\",\"exit_code\":0}"}]},"session_id":"grok-session"}'
printf '%s\n' '{"type":"assistant","message":{"id":"msg_2","content":[{"type":"tool_use","id":"tool_2","name":"search_replace","input":{"target_file":"src/main.rs","old_string":"a","new_string":"b"}}]},"session_id":"grok-session"}'
printf '%s\n' '{"type":"user","message":{"content":[{"type":"tool_result","tool_use_id":"tool_2","content":"updated src/main.rs"}]},"session_id":"grok-session"}'
printf '%s\n' '{"type":"assistant","message":{"id":"msg_3","content":[{"type":"text","text":"Done."}]},"session_id":"grok-session"}'
printf '%s\n' '{"type":"result","subtype":"success","is_error":false,"result":"Done.","session_id":"grok-session","total_cost_usd":0.01,"usage":{"input_tokens":11,"cache_read_input_tokens":5,"output_tokens":7},"modelUsage":{"grok-code-fast-1":{"inputTokens":11,"cacheReadInputTokens":5,"outputTokens":7,"costUSD":0.01}}}'
"#,
    );
    let runner = Runner::builder()
        .agent(Agent::Grok)
        .executable(fake.executable())
        .env("CODEXCW_ARGS_FILE", fake.args_file.to_str().unwrap())
        .env("CODEXCW_STDIN_FILE", fake.stdin_file.to_str().unwrap())
        .build();

    let result = runner
        .run(Request::new("inspect").stdin("extra context"))
        .await
        .expect("run succeeds");

    assert_eq!(result.thread_id, "grok-session");
    assert_eq!(result.final_message, "Done.");
    assert_eq!(result.usage.input_tokens, 11);
    assert_eq!(result.usage.cached_input_tokens, 5);
    assert_eq!(result.usage.output_tokens, 7);
    assert_eq!(result.usage.total_tokens, 23);
    assert_eq!(result.usage.reasoning_output_tokens, 0);
    assert_eq!(result.usage.total_cost_usd, 0.01);

    let command = result
        .events
        .iter()
        .filter_map(|event| event.item_completed())
        .find(|item| item.kind == ItemKind::CommandExecution)
        .unwrap();
    assert_eq!(command.command, "printf hi");
    assert_eq!(command.aggregated_output, "hi\n");
    assert_eq!(command.exit_code, Some(0));

    let file = result
        .events
        .iter()
        .filter_map(|event| event.item_completed())
        .find(|item| item.kind == ItemKind::FileChange)
        .unwrap();
    assert_eq!(file.changes[0].path, "src/main.rs");
    assert_eq!(file.changes[0].kind, "update");

    assert_eq!(
        std::fs::read_to_string(&fake.stdin_file).unwrap(),
        "inspect\n\n<stdin>\nextra context\n</stdin>\n"
    );
    assert!(result.events.iter().all(|event| !event.raw.is_empty()));

    let args = read_args(&fake.args_file);
    for want in [
        "--no-auto-update",
        "--output-format",
        "streaming-messages-json",
        "--verbatim",
        "--prompt-file",
        "--sandbox",
        "read-only",
        "--permission-mode",
        "dontAsk",
    ] {
        assert!(args.contains(&want.to_string()), "missing arg: {want}");
    }
    let prompt_index = args.iter().position(|arg| arg == "--prompt-file").unwrap();
    assert!(!std::path::Path::new(&args[prompt_index + 1]).exists());
}

#[tokio::test(flavor = "multi_thread", worker_threads = 2)]
async fn grok_error_result_precedes_process_exit_error() {
    let fake = write_fake_codex(
        r#"record_args "$@"
printf '%s\n' '{"type":"system","subtype":"init","session_id":"grok-error"}'
printf '%s\n' '{"type":"result","subtype":"error_max_turns","is_error":true,"errors":["Reached the maximum number of turns"],"session_id":"grok-error"}'
printf '%s\n' 'Error: max turns reached' >&2
exit 1
"#,
    );
    let runner = Runner::builder()
        .agent(Agent::Grok)
        .executable(fake.executable())
        .build();

    let error = runner.run(Request::new("fail")).await.unwrap_err();
    match error {
        Error::Grok { message, event } => {
            assert!(message.contains("Reached the maximum number of turns"));
            assert!(matches!(event.payload, EventPayload::TurnFailed { .. }));
        }
        other => panic!("unexpected error: {other:?}"),
    }
}

#[tokio::test(flavor = "multi_thread", worker_threads = 2)]
async fn live_grok_smoke() {
    if std::env::var_os("CODEXCW_LIVE_GROK").is_none() {
        return;
    }

    let result = Runner::builder()
        .agent(Agent::Grok)
        .build()
        .run(Request::new("Reply with exactly: CODEXCW_GROK_OK"))
        .await
        .expect("live grok run succeeds");

    assert_eq!(result.final_message.trim(), "CODEXCW_GROK_OK");
    assert!(!result.thread_id.is_empty());
}
