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
from codexcw import CodexcwError, Request, Runner

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
