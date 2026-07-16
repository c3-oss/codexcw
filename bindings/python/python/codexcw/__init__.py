"""Run agent CLIs non-interactively from Python, backed by a Rust core.

``codexcw`` runs Codex or Claude Code non-interactively: it spawns the
selected agent CLI, decodes the JSONL event stream, and exposes each run as
iterables, callbacks, results, and typed errors. The Codex agent (the default)
wraps ``codex exec --json`` with automation-friendly defaults (read-only
sandbox, approval ``never``, ephemeral sessions, color disabled, git check
skipped); ``Runner(agent=AGENT_CLAUDE)`` wraps Claude Code
(``claude -p --output-format stream-json``), normalizing its events into the
same event model.

The synchronous API lives here; :mod:`codexcw.aio` mirrors it with ``async``.
"""

from __future__ import annotations

from dataclasses import dataclass
from types import SimpleNamespace
from typing import Callable, Iterator, List, Optional

from . import _codexcw
from ._codexcw import PyAccountCredits as AccountCredits
from ._codexcw import PyAccountRateLimits as AccountRateLimits
from ._codexcw import PyAccountRateLimitWindow as AccountRateLimitWindow
from ._codexcw import PyAccountSpendLimit as AccountSpendLimit
from ._codexcw import PyAccountTokenUsage as AccountTokenUsage
from ._codexcw import PyAccountTokenUsageDailyBucket as AccountTokenUsageDailyBucket
from ._codexcw import PyAccountTokenUsageSummary as AccountTokenUsageSummary
from ._codexcw import PyAccountUsage as AccountUsage
from ._codexcw import PyAccountUsageAccount as AccountUsageAccount
from ._codexcw import PyClaudeAccountUsage as ClaudeAccountUsage
from ._codexcw import PyClaudeAccountUsageWindow as ClaudeAccountUsageWindow
from ._codexcw import PyEvent as Event
from ._codexcw import PyFileChange as FileChange
from ._codexcw import PyItem as Item
from ._codexcw import PyModelUsage as ModelUsage
from ._codexcw import PyRunEvent as RunEvent
from ._codexcw import PyRunResult as RunResult
from ._codexcw import PyUsage as Usage

__all__ = [
    "AGENT_CLAUDE",
    "AGENT_CODEX",
    "CLAUDE_MODEL_HAIKU",
    "CLAUDE_MODEL_OPUS",
    "CLAUDE_MODEL_SONNET",
    "ClaudeAccountUsage",
    "ClaudeAccountUsageRequest",
    "ClaudeAccountUsageWindow",
    "PERMISSION_ACCEPT_EDITS",
    "PERMISSION_AUTO",
    "PERMISSION_BYPASS_PERMISSIONS",
    "PERMISSION_DONT_ASK",
    "PERMISSION_MANUAL",
    "PERMISSION_PLAN",
    "Agent",
    "PermissionMode",
    "AccountCredits",
    "AccountRateLimitWindow",
    "AccountRateLimits",
    "AccountSpendLimit",
    "AccountTokenUsage",
    "AccountTokenUsageDailyBucket",
    "AccountTokenUsageSummary",
    "AccountUsage",
    "AccountUsageAccount",
    "AccountUsageRequest",
    "ApprovalPolicy",
    "CodexcwError",
    "ConfigOverride",
    "Event",
    "FileChange",
    "Group",
    "GroupResult",
    "Item",
    "ModelUsage",
    "Request",
    "RunEvent",
    "RunResult",
    "Runner",
    "SandboxMode",
    "Session",
    "Usage",
    "get_account_usage",
    "get_claude_account_usage",
]

# String literals accepted by ``Request.sandbox`` and ``Request.approval``.
SandboxMode = str
ApprovalPolicy = str

# String literals accepted by ``Runner(agent=...)`` and
# ``Request.permission_mode``.
Agent = str
PermissionMode = str

AGENT_CODEX = "codex"
AGENT_CLAUDE = "claude"

# Model aliases accepted by the claude agent's ``Request.model``.
CLAUDE_MODEL_HAIKU = "haiku"
CLAUDE_MODEL_SONNET = "sonnet"
CLAUDE_MODEL_OPUS = "opus"

# Permission modes accepted by the claude agent's ``Request.permission_mode``.
PERMISSION_ACCEPT_EDITS = "acceptEdits"
PERMISSION_AUTO = "auto"
PERMISSION_BYPASS_PERMISSIONS = "bypassPermissions"
PERMISSION_MANUAL = "manual"
PERMISSION_PLAN = "plan"
PERMISSION_DONT_ASK = "dontAsk"


class CodexcwError(Exception):
    """A typed agent run error.

    The ``kind`` attribute is one of ``promptRequired``, ``invalidRequest``,
    ``exit``, ``decode``, ``codex``, ``claude``, ``handler``, ``cancelled``,
    ``process``.
    """

    def __init__(self, info: "_codexcw.PyError") -> None:
        super().__init__(info.message)
        self.kind: str = info.kind
        self.code: Optional[int] = info.code
        self.stderr: Optional[str] = info.stderr
        self.line: Optional[int] = info.line


def _result_or_raise(outcome):
    if outcome.error is not None:
        raise CodexcwError(outcome.error)
    return outcome.result


def _account_usage_or_raise(outcome):
    result = _result_or_raise(outcome)
    if result is None:
        raise CodexcwError(
            SimpleNamespace(
                kind="process",
                message="account usage result missing",
                code=None,
                stderr=None,
                line=None,
            )
        )
    return result


@dataclass
class ConfigOverride:
    """One ``-c key=value`` config override."""

    key: str = ""
    value: str = ""


@dataclass
class AccountUsageRequest:
    """Options for reading Codex account usage.

    ``timeout`` is the per-request JSON-RPC timeout in seconds; Codex defaults
    to 10 seconds when unset.
    """

    executable: Optional[str] = None
    env: Optional[dict] = None
    timeout: Optional[float] = None


def get_account_usage(req: Optional[AccountUsageRequest] = None) -> AccountUsage:
    """Reads Codex account usage and limits through ``codex app-server``."""

    return _account_usage_or_raise(_codexcw.get_account_usage(req))


@dataclass
class ClaudeAccountUsageRequest:
    """Options for reading Claude account usage.

    ``timeout`` bounds the Claude CLI call in seconds.
    """

    executable: Optional[str] = None
    env: Optional[dict] = None
    timeout: Optional[float] = None


def get_claude_account_usage(
    req: Optional[ClaudeAccountUsageRequest] = None,
) -> ClaudeAccountUsage:
    """Reads Claude account usage through the Claude Code ``/usage`` command."""

    return _account_usage_or_raise(_codexcw.get_claude_account_usage(req))


@dataclass
class Request:
    """An agent run request. All fields are optional except prompt or stdin."""

    prompt: str = ""
    stdin: Optional[str] = None
    dir: Optional[str] = None
    add_dirs: Optional[List[str]] = None
    images: Optional[List[str]] = None
    model: Optional[str] = None
    profile: Optional[str] = None
    sandbox: Optional[SandboxMode] = None
    approval: Optional[ApprovalPolicy] = None
    permission_mode: Optional[PermissionMode] = None
    allowed_tools: Optional[List[str]] = None
    disallowed_tools: Optional[List[str]] = None
    config: Optional[List[ConfigOverride]] = None
    enable: Optional[List[str]] = None
    disable: Optional[List[str]] = None
    strict_config: bool = False
    persistent: bool = False
    ignore_user_config: bool = False
    ignore_rules: bool = False
    require_git_repo: bool = False
    output_schema_path: Optional[str] = None
    output_schema: Optional[str] = None
    output_last_message_path: Optional[str] = None
    dangerously_bypass_sandbox: bool = False
    dangerously_bypass_hooks: bool = False
    env: Optional[dict] = None
    resume_id: Optional[str] = None
    resume_last: bool = False
    resume_all: bool = False


@dataclass
class GroupResult:
    """One result from :meth:`Runner.run_many`."""

    index: int
    run_id: str
    result: Optional[RunResult]
    error: Optional[CodexcwError]


class Session:
    """A running selected-agent process."""

    def __init__(self, native) -> None:
        self._native = native

    @property
    def id(self) -> str:
        return self._native.id

    def thread_id(self) -> str:
        return self._native.thread_id()

    def cancel(self) -> None:
        self._native.cancel()

    def events(self) -> Iterator[Event]:
        """Iterates decoded events until the process exits."""
        return iter(self._native)

    def wait(self) -> RunResult:
        """Waits for the process to exit; raises :class:`CodexcwError`."""
        return _result_or_raise(self._native.wait())


class Group:
    """A batch of running selected-agent processes."""

    def __init__(self, native) -> None:
        self._native = native

    def cancel(self) -> None:
        self._native.cancel()

    def events(self) -> Iterator[RunEvent]:
        """Iterates multiplexed events until every run finishes."""
        return iter(self._native)

    def wait(self) -> List[GroupResult]:
        return [
            GroupResult(
                index=r.index,
                run_id=r.run_id,
                result=r.result,
                error=CodexcwError(r.error) if r.error is not None else None,
            )
            for r in self._native.wait()
        ]


class Runner:
    """Starts selected-agent processes with safe automation defaults."""

    def __init__(
        self,
        *,
        agent: Optional[Agent] = None,
        executable: Optional[str] = None,
        env: Optional[dict] = None,
        event_buffer: Optional[int] = None,
        stderr_limit: Optional[int] = None,
        scan_max_bytes: Optional[int] = None,
        default_sandbox: Optional[SandboxMode] = None,
        default_approval: Optional[ApprovalPolicy] = None,
    ) -> None:
        if agent is not None and agent not in (AGENT_CODEX, AGENT_CLAUDE):
            raise CodexcwError(
                SimpleNamespace(
                    kind="invalidRequest",
                    message=f"unknown agent: {agent}",
                    code=None,
                    stderr=None,
                    line=None,
                )
            )
        self._native = _codexcw.Runner(
            executable=executable,
            env=env,
            event_buffer=event_buffer,
            stderr_limit=stderr_limit,
            scan_max_bytes=scan_max_bytes,
            default_sandbox=default_sandbox,
            default_approval=default_approval,
            agent=agent,
        )

    def run(
        self,
        req: Request,
        on_event: Optional[Callable[[Event], None]] = None,
    ) -> RunResult:
        """Runs one process to completion.

        With ``on_event``, the callback runs for each event; a raise cancels the
        run. Raises :class:`CodexcwError` on failure.
        """
        if on_event is None:
            return _result_or_raise(self._native.run(req))
        session = self.start(req)
        try:
            for event in session.events():
                on_event(event)
        except BaseException:
            session.cancel()
            try:
                session.wait()
            except CodexcwError:
                pass
            raise
        return session.wait()

    def start(self, req: Request) -> Session:
        """Launches one process and returns a :class:`Session`."""
        return Session(self._native.start(req))

    def run_many(
        self,
        reqs: List[Request],
        *,
        max_concurrent: Optional[int] = None,
        event_buffer: Optional[int] = None,
    ) -> Group:
        """Launches many processes with bounded concurrency."""
        return Group(self._native.run_many(list(reqs), max_concurrent, event_buffer))
