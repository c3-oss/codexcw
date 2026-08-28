#!/bin/sh
set -eu

if [ "${CODEXCW_ARGS_FILE:-}" != "" ]; then
  : >"$CODEXCW_ARGS_FILE"
  for arg in "$@"; do
    printf '%s\n' "$arg" >>"$CODEXCW_ARGS_FILE"
  done
fi

prompt_file=
previous=
for arg in "$@"; do
  if [ "$previous" = "--prompt-file" ]; then
    prompt_file=$arg
  fi
  previous=$arg
done

if [ "${CODEXCW_STDIN_FILE:-}" != "" ]; then
  cat "$prompt_file" >"$CODEXCW_STDIN_FILE"
fi

printf '%s\n' '{"type":"system","subtype":"init","session_id":"grok-session"}'
if [ "${CODEXCW_GROK_ERROR:-}" != "" ]; then
  printf '%s\n' '{"type":"result","subtype":"error_max_turns","is_error":true,"errors":["Reached the maximum number of turns"],"session_id":"grok-session"}'
  exit 1
fi
printf '%s\n' '{"type":"assistant","message":{"id":"msg_1","content":[{"type":"thinking","thinking":"Checking."},{"type":"tool_use","id":"tool_1","name":"run_terminal_command","input":{"command":"printf hi","description":"Print text"}}]},"session_id":"grok-session"}'
printf '%s\n' '{"type":"user","message":{"content":[{"type":"tool_result","tool_use_id":"tool_1","content":"{\"type\":\"Bash\",\"output\":[104,105,10],\"output_for_prompt\":\"exit: 0\\nhi\\n\",\"exit_code\":0}"}]},"session_id":"grok-session"}'
printf '%s\n' '{"type":"assistant","message":{"id":"msg_2","content":[{"type":"tool_use","id":"tool_2","name":"search_replace","input":{"target_file":"src/Program.cs"}}]},"session_id":"grok-session"}'
printf '%s\n' '{"type":"user","message":{"content":[{"type":"tool_result","tool_use_id":"tool_2","content":"updated src/Program.cs"}]},"session_id":"grok-session"}'
printf '%s\n' '{"type":"assistant","message":{"id":"msg_3","content":[{"type":"text","text":"Done."}]},"session_id":"grok-session"}'
printf '%s\n' '{"type":"result","subtype":"success","is_error":false,"result":"Done.","session_id":"grok-session","total_cost_usd":0.01,"usage":{"input_tokens":11,"cache_read_input_tokens":5,"output_tokens":7},"modelUsage":{"grok-code-fast-1":{"inputTokens":11,"cacheReadInputTokens":5,"outputTokens":7,"costUSD":0.01}}}'
