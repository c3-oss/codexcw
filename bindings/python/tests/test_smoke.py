"""Smoke tests for codexcw, driven by a fake `codex` so they run without a real
Codex install. Unix-only (the fixture is a shell script)."""

from __future__ import annotations

import os
import stat
import sys
from pathlib import Path

import pytest

import codexcw
import codexcw.aio
from codexcw import AccountUsageRequest, CodexcwError, Request, Runner, get_account_usage

pytestmark = pytest.mark.skipif(
    sys.platform == "win32", reason="fixture is a POSIX shell script"
)

FAKE_CODEX = Path(__file__).parent / "fixtures" / "fake_codex.sh"


@pytest.fixture(autouse=True)
def _executable_fixture():
    FAKE_CODEX.chmod(FAKE_CODEX.stat().st_mode | stat.S_IEXEC)


def _runner_with_capture(tmp_path: Path) -> tuple[Runner, Path, Path]:
    args_file = tmp_path / "args.txt"
    stdin_file = tmp_path / "stdin.txt"
    runner = Runner(
        executable=str(FAKE_CODEX),
        env={
            "CODEXCW_ARGS_FILE": str(args_file),
            "CODEXCW_STDIN_FILE": str(stdin_file),
        },
    )
    return runner, args_file, stdin_file


def _usage_fake(tmp_path: Path) -> Path:
    fake = tmp_path / "codex-usage"
    fake.write_text(
        """#!/bin/sh
set -eu
: >"$CODEXCW_ARGS_FILE"
for arg in "$@"; do
  printf '%s\\n' "$arg" >>"$CODEXCW_ARGS_FILE"
done
printf '%s\\n' "$CODEX_HOME" >"$CODEXCW_ENV_FILE"
while IFS= read -r line; do
  case "$line" in
    *'"method":"initialized"'*) ;;
    *'"method":"initialize"'*) printf '%s\\n' '{"id":1,"result":{}}' ;;
    *'"method":"account/rateLimits/read"'*)
      printf '%s\\n' '{"id":2,"result":{"rateLimits":{"limitId":null,"limitName":null,"planType":"pro","rateLimitReachedType":null,"primary":{"usedPercent":12.5,"windowDurationMins":300,"resetsAt":1766948068},"credits":{"hasCredits":true,"unlimited":false,"balance":"7"},"individualLimit":{"limit":"100","used":25,"remainingPercent":"75","resetsAt":"1768000000"}},"rateLimitsByLimitId":{"spark":{"limitName":"Codex Spark","primary":{"usedPercent":8,"windowDurationMins":300,"resetsAt":1767000000}}}}}'
      ;;
    *'"method":"account/usage/read"'*)
      printf '%s\\n' '{"id":3,"result":{"summary":{"lifetimeTokens":"12345678901234567890","peakDailyTokens":456,"longestRunningTurnSec":"789","currentStreakDays":3,"longestStreakDays":"9"},"dailyUsageBuckets":[{"startDate":"2026-07-07","tokens":"42"}]}}'
      ;;
    *'"method":"account/read"'*)
      printf '%s\\n' '{"id":4,"result":{"account":{"type":"chatgpt","email":"stub@example.com","planType":"pro"},"requiresOpenaiAuth":false}}'
      ;;
  esac
done
"""
    )
    fake.chmod(fake.stat().st_mode | stat.S_IEXEC)
    return fake


def test_run_decodes_events_and_uses_safe_defaults(tmp_path):
    runner, args_file, stdin_file = _runner_with_capture(tmp_path)

    result = runner.run(Request(prompt="diga oi"))

    assert result.thread_id == "thread-1"
    assert result.final_message == "Oi."
    assert result.usage.input_tokens == 10
    assert len(result.events) == 4

    assert stdin_file.read_text() == "diga oi"

    args = args_file.read_text().strip().split("\n")
    assert "--json" in args
    assert "--sandbox" in args
    assert "read-only" in args
    assert 'approval_policy="never"' in args
    assert args[-1] == "-"


def test_streaming_yields_events_in_order(tmp_path):
    runner, _, _ = _runner_with_capture(tmp_path)

    session = runner.start(Request(prompt="stream"))
    types = [event.type for event in session.events()]
    result = session.wait()

    assert types == ["thread.started", "turn.started", "item.completed", "turn.completed"]
    assert result.final_message == "Oi."


def test_on_event_callback_runs_per_event(tmp_path):
    runner, _, _ = _runner_with_capture(tmp_path)
    seen = []

    result = runner.run(Request(prompt="cb"), lambda event: seen.append(event.type))

    assert len(seen) == 4
    assert result.final_message == "Oi."


def test_on_event_raise_cancels_run(tmp_path):
    runner, _, _ = _runner_with_capture(tmp_path)

    def on_event(event):
        if event.type == "turn.started":
            raise RuntimeError("stop")

    with pytest.raises(RuntimeError, match="stop"):
        runner.run(Request(prompt="boom"), on_event)


def test_missing_prompt_raises_typed_error(tmp_path):
    runner, _, _ = _runner_with_capture(tmp_path)

    with pytest.raises(CodexcwError) as excinfo:
        runner.run(Request())
    assert excinfo.value.kind == "promptRequired"


def test_run_many_collects_results(tmp_path):
    runner, _, _ = _runner_with_capture(tmp_path)

    group = runner.run_many(
        [Request(prompt="a"), Request(prompt="b"), Request(prompt="c")],
        max_concurrent=2,
    )

    event_count = sum(1 for _ in group.events())
    results = group.wait()

    assert len(results) == 3
    assert event_count > 0
    for result in results:
        assert result.error is None
        assert result.result.final_message == "Oi."


def test_get_account_usage_reads_limits(tmp_path):
    fake = _usage_fake(tmp_path)
    args_file = tmp_path / "usage-args.txt"
    env_file = tmp_path / "usage-env.txt"
    codex_home = tmp_path / "codex-home"

    usage = get_account_usage(
        AccountUsageRequest(
            executable=str(fake),
            env={
                "CODEXCW_ARGS_FILE": str(args_file),
                "CODEXCW_ENV_FILE": str(env_file),
                "CODEX_HOME": str(codex_home),
            },
            timeout=5.0,
        )
    )

    assert usage.account.email == "stub@example.com"
    assert usage.rate_limits.plan_type == "pro"
    assert usage.rate_limits.primary.used_percent == 12.5
    assert usage.rate_limits.credits.balance == "7"
    assert usage.rate_limits.individual_limit.remaining_percent == 75.0
    assert usage.rate_limits_by_limit_id["spark"].limit_name == "Codex Spark"
    assert usage.token_usage.summary.lifetime_tokens == "12345678901234567890"
    assert usage.token_usage.summary.peak_daily_tokens == "456"
    assert usage.token_usage.daily_usage_buckets[0].tokens == "42"
    assert "rateLimits" in usage.raw_rate_limits
    assert "lifetimeTokens" in usage.raw_token_usage
    assert args_file.read_text().strip().split("\n") == [
        "-s",
        "read-only",
        "-a",
        "untrusted",
        "app-server",
        "--stdio",
    ]
    assert env_file.read_text() == f"{codex_home}\n"


def test_get_account_usage_timeout_raises(tmp_path):
    fake = tmp_path / "codex-usage-slow"
    fake.write_text(
        """#!/bin/sh
set -eu
while IFS= read -r line; do
  case "$line" in
    *'"method":"initialized"'*) ;;
    *'"method":"initialize"'*) printf '%s\\n' '{"id":1,"result":{}}' ;;
    *'"method":"account/rateLimits/read"'*)
      printf '%s\\n' '{"id":2,"result":{"rateLimits":{"planType":"pro"}}}'
      ;;
    *'"method":"account/usage/read"'*) sleep 5 ;;
  esac
done
"""
    )
    fake.chmod(fake.stat().st_mode | stat.S_IEXEC)

    with pytest.raises(CodexcwError) as excinfo:
        get_account_usage(AccountUsageRequest(executable=str(fake), timeout=0.2))

    assert excinfo.value.kind == "process"
    assert "timeout" in str(excinfo.value)


async def test_async_run_and_stream(tmp_path):
    args_file = tmp_path / "args.txt"
    stdin_file = tmp_path / "stdin.txt"
    runner = codexcw.aio.Runner(
        executable=str(FAKE_CODEX),
        env={
            "CODEXCW_ARGS_FILE": str(args_file),
            "CODEXCW_STDIN_FILE": str(stdin_file),
        },
    )

    session = await runner.start(Request(prompt="oi"))
    types = [event.type async for event in session.events()]
    result = await session.wait()

    assert types == ["thread.started", "turn.started", "item.completed", "turn.completed"]
    assert result.final_message == "Oi."


async def test_async_get_account_usage(tmp_path):
    fake = _usage_fake(tmp_path)
    usage = await codexcw.aio.get_account_usage(
        AccountUsageRequest(
            executable=str(fake),
            env={
                "CODEXCW_ARGS_FILE": str(tmp_path / "async-args.txt"),
                "CODEXCW_ENV_FILE": str(tmp_path / "async-env.txt"),
                "CODEX_HOME": str(tmp_path),
            },
        )
    )

    assert usage.account.email == "stub@example.com"
    assert usage.rate_limits_by_limit_id["spark"].limit_name == "Codex Spark"
    assert usage.token_usage.daily_usage_buckets[0].tokens == "42"


@pytest.mark.skipif(
    os.environ.get("CODEXCW_LIVE_CODEX") != "1",
    reason="set CODEXCW_LIVE_CODEX=1 to run against the real codex executable",
)
def test_live_get_account_usage_and_fast_mode(tmp_path):
    usage = get_account_usage()
    assert usage.raw_rate_limits

    runner = Runner()
    result = runner.run(
        Request(
            prompt="Responda exatamente: OK",
            dir=str(tmp_path),
            ignore_rules=True,
            config=[codexcw.ConfigOverride(key="service_tier", value='"priority"')],
        )
    )

    assert "OK" in result.final_message.upper()
