#!/bin/sh
# Fake `codex` for tests: records argv and stdin, then emits a fixed JSONL
# event stream. Mirrors the Go suite's writeFakeCodex helper.
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

printf '%s\n' '{"type":"thread.started","thread_id":"thread-1"}'
printf '%s\n' '{"type":"turn.started"}'
printf '%s\n' '{"type":"item.completed","item":{"id":"item_0","type":"agent_message","text":"Oi."}}'
printf '%s\n' '{"type":"turn.completed","usage":{"input_tokens":10,"cached_input_tokens":2,"output_tokens":3,"reasoning_output_tokens":1}}'
