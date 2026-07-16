#!/bin/sh
# Fake `claude` for tests: records argv and stdin, then emits a fixed
# stream-json event stream. Mirrors the claude cases in the Go/Rust suites.
set -eu

if [ "${CODEXCW_ARGS_FILE:-}" != "" ]; then
  : >"$CODEXCW_ARGS_FILE"
  for arg in "$@"; do
    printf '%s\n' "$arg" >>"$CODEXCW_ARGS_FILE"
  done
fi

prompt=""
if [ "${CODEXCW_STDIN_FILE:-}" != "" ]; then
  cat >"$CODEXCW_STDIN_FILE"
  prompt=$(cat "$CODEXCW_STDIN_FILE")
else
  prompt=$(cat)
fi

if [ "$prompt" = "/usage" ]; then
  printf '%s\n' '{"type":"result","subtype":"success","is_error":false,"result":"You are currently using your subscription\n\nCurrent session: 13% used · resets Jul 16 at 3:50pm (America/Sao_Paulo)\nCurrent week (all models): 5% used · resets Jul 18 at 9am (America/Sao_Paulo)"}'
  exit 0
fi

if [ "${CODEXCW_CLAUDE_ERROR:-}" != "" ]; then
  printf '%s\n' '{"type":"system","subtype":"init","cwd":"/work","session_id":"sess-error"}'
  printf '%s\n' '{"type":"result","subtype":"success","is_error":true,"result":"Claude fixture failure","session_id":"sess-error","usage":{"input_tokens":1,"cache_creation_input_tokens":2,"cache_read_input_tokens":3,"output_tokens":4}}'
  exit 1
fi

printf '%s\n' '{"type":"system","subtype":"init","cwd":"/work","session_id":"sess-1"}'
printf '%s\n' '{"type":"assistant","message":{"id":"msg_1","content":[{"type":"thinking","thinking":"I will create the file."}]},"session_id":"sess-1"}'
printf '%s\n' '{"type":"assistant","message":{"id":"msg_1","content":[{"type":"tool_use","id":"toolu_1","name":"Write","input":{"file_path":"/work/hello.txt","content":"hello"}}]},"session_id":"sess-1"}'
printf '%s\n' '{"type":"user","message":{"content":[{"tool_use_id":"toolu_1","type":"tool_result","content":"File created successfully"}]},"session_id":"sess-1","tool_use_result":{"type":"create"}}'
printf '%s\n' '{"type":"assistant","message":{"id":"msg_2","content":[{"type":"thinking","thinking":"The file is ready."}]},"session_id":"sess-1"}'
printf '%s\n' '{"type":"assistant","message":{"id":"msg_2","content":[{"type":"text","text":"Done."}]},"session_id":"sess-1"}'
printf '%s\n' '{"type":"result","subtype":"success","is_error":false,"result":"Done.","session_id":"sess-1","total_cost_usd":0.013562,"usage":{"input_tokens":18,"cache_creation_input_tokens":3944,"cache_read_input_tokens":45921,"output_tokens":380},"modelUsage":{"claude-haiku-4-5-20251001":{"inputTokens":18,"outputTokens":380,"cacheReadInputTokens":45921,"cacheCreationInputTokens":3944,"webSearchRequests":0,"costUSD":0.013562,"contextWindow":200000,"maxOutputTokens":32000}}}'
