"""Async (asyncio) facade over the synchronous :mod:`codexcw` API.

Blocking native calls run in worker threads via :func:`asyncio.to_thread`, so
the event loop stays responsive. The shapes mirror the sync API:

    runner = codexcw.aio.Runner()
    result = await runner.run(codexcw.Request(prompt="diga oi"))

    session = await runner.start(codexcw.Request(prompt="..."))
    async for event in session.events():
        ...
"""

from __future__ import annotations

import asyncio
from typing import AsyncIterator, Awaitable, Callable, List, Optional, Union

from . import (
    AccountUsage,
    AccountUsageRequest,
    ClaudeAccountUsage,
    ClaudeAccountUsageRequest,
    CodexcwError,
    Event,
    GroupResult,
    Request,
    RunEvent,
    RunResult,
)
from . import Group as _SyncGroup
from . import Runner as _SyncRunner
from . import Session as _SyncSession
from . import get_account_usage as _sync_get_account_usage
from . import get_claude_account_usage as _sync_get_claude_account_usage

__all__ = [
    "Runner",
    "Session",
    "Group",
    "get_account_usage",
    "get_claude_account_usage",
]

_Sentinel = object()


class _AsyncEvents:
    def __init__(self, iterator) -> None:
        self._iterator = iterator

    def __aiter__(self) -> "_AsyncEvents":
        return self

    async def __anext__(self):
        value = await asyncio.to_thread(next, self._iterator, _Sentinel)
        if value is _Sentinel:
            raise StopAsyncIteration
        return value


class Session:
    """A running selected-agent process with async iteration."""

    def __init__(self, sync_session: _SyncSession) -> None:
        self._sync = sync_session

    @property
    def id(self) -> str:
        return self._sync.id

    def thread_id(self) -> str:
        return self._sync.thread_id()

    def cancel(self) -> None:
        self._sync.cancel()

    def events(self) -> AsyncIterator[Event]:
        return _AsyncEvents(self._sync.events())

    async def wait(self) -> RunResult:
        return await asyncio.to_thread(self._sync.wait)


class Group:
    """A batch of running selected-agent processes with async iteration."""

    def __init__(self, sync_group: _SyncGroup) -> None:
        self._sync = sync_group

    def cancel(self) -> None:
        self._sync.cancel()

    def events(self) -> AsyncIterator[RunEvent]:
        return _AsyncEvents(self._sync.events())

    async def wait(self) -> List[GroupResult]:
        return await asyncio.to_thread(self._sync.wait)


async def get_account_usage(req: Optional[AccountUsageRequest] = None) -> AccountUsage:
    """Reads Codex account usage and limits through ``codex app-server``."""

    return await asyncio.to_thread(_sync_get_account_usage, req)


async def get_claude_account_usage(
    req: Optional[ClaudeAccountUsageRequest] = None,
) -> ClaudeAccountUsage:
    """Reads Claude account usage through the Claude Code ``/usage`` command."""

    return await asyncio.to_thread(_sync_get_claude_account_usage, req)


class Runner:
    """Async wrapper around the synchronous :class:`codexcw.Runner`."""

    def __init__(self, **options) -> None:
        self._sync = _SyncRunner(**options)

    async def run(
        self,
        req: Request,
        on_event: Optional[Callable[[Event], Union[None, Awaitable[None]]]] = None,
    ) -> RunResult:
        if on_event is None:
            return await asyncio.to_thread(self._sync.run, req)
        session = await self.start(req)
        try:
            async for event in session.events():
                result = on_event(event)
                if asyncio.iscoroutine(result):
                    await result
        except BaseException:
            session.cancel()
            try:
                await session.wait()
            except CodexcwError:
                pass
            raise
        return await session.wait()

    async def start(self, req: Request) -> Session:
        return Session(await asyncio.to_thread(self._sync.start, req))

    async def run_many(
        self,
        reqs: List[Request],
        *,
        max_concurrent: Optional[int] = None,
        event_buffer: Optional[int] = None,
    ) -> Group:
        sync_group = await asyncio.to_thread(
            lambda: self._sync.run_many(
                reqs, max_concurrent=max_concurrent, event_buffer=event_buffer
            )
        )
        return Group(sync_group)
