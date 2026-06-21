"""Type stubs for the native `_codexcw` extension module."""

from typing import Iterator, List, Optional

class PyUsage:
    input_tokens: int
    cached_input_tokens: int
    output_tokens: int
    reasoning_output_tokens: int
    total_tokens: int

class PyFileChange:
    path: str
    kind: str

class PyItem:
    id: str
    type: str
    status: str
    text: str
    message: str
    command: str
    aggregated_output: str
    exit_code: Optional[int]
    raw: str
    changes: List[PyFileChange]

class PyEvent:
    type: str
    run_id: str
    thread_id: str
    raw: str
    item: Optional[PyItem]
    usage: Optional[PyUsage]
    error: Optional[str]

class PyRunResult:
    run_id: str
    thread_id: str
    final_message: str
    usage: PyUsage
    events: List[PyEvent]
    stderr: str
    started_at_ms: float
    finished_at_ms: float

class PyError:
    kind: str
    message: str
    code: Optional[int]
    stderr: Optional[str]
    line: Optional[int]

class PyOutcome:
    result: PyRunResult
    error: Optional[PyError]

class PyRunEvent:
    run_id: str
    index: int
    event: PyEvent

class PyGroupResult:
    index: int
    run_id: str
    result: Optional[PyRunResult]
    error: Optional[PyError]

class Session:
    id: str
    def __iter__(self) -> Iterator[PyEvent]: ...
    def __next__(self) -> PyEvent: ...
    def next_event(self) -> Optional[PyEvent]: ...
    def wait(self) -> PyOutcome: ...
    def thread_id(self) -> str: ...
    def cancel(self) -> None: ...

class Group:
    def __iter__(self) -> Iterator[PyRunEvent]: ...
    def __next__(self) -> PyRunEvent: ...
    def next_event(self) -> Optional[PyRunEvent]: ...
    def wait(self) -> List[PyGroupResult]: ...
    def cancel(self) -> None: ...

class Runner:
    def __init__(
        self,
        *,
        executable: Optional[str] = ...,
        env: Optional[dict] = ...,
        event_buffer: Optional[int] = ...,
        stderr_limit: Optional[int] = ...,
        scan_max_bytes: Optional[int] = ...,
        default_sandbox: Optional[str] = ...,
        default_approval: Optional[str] = ...,
    ) -> None: ...
    def run(self, req: object) -> PyOutcome: ...
    def start(self, req: object) -> Session: ...
    def run_many(
        self,
        reqs: list,
        max_concurrent: Optional[int] = ...,
        event_buffer: Optional[int] = ...,
    ) -> Group: ...
