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

if [ "${CODEXCW_STDIN_FILE:-}" != "" ]; then
  cat >"$CODEXCW_STDIN_FILE"
else
  cat >/dev/null
fi

printf '%s\n' '{"type":"system","subtype":"init","cwd":"/work","session_id":"sess-1"}'
printf '%s\n' '{"type":"assistant","message":{"id":"msg_1","content":[{"type":"tool_use","id":"toolu_1","name":"Write","input":{"file_path":"/work/hello.txt","content":"hello"}}]},"session_id":"sess-1"}'
printf '%s\n' '{"type":"user","message":{"content":[{"tool_use_id":"toolu_1","type":"tool_result","content":"File created successfully"}]},"session_id":"sess-1","tool_use_result":{"type":"create"}}'
printf '%s\n' '{"type":"assistant","message":{"id":"msg_2","content":[{"type":"text","text":"Done."}]},"session_id":"sess-1"}'
printf '%s\n' '{"type":"result","subtype":"success","is_error":false,"result":"Done.","session_id":"sess-1","usage":{"input_tokens":18,"cache_read_input_tokens":45921,"output_tokens":380}}'
