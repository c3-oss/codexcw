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

class PyAccountUsageAccount:
    type: str
    email: str
    plan_type: str
    requires_openai_auth: bool

class PyAccountRateLimits:
    limit_id: str
    limit_name: str
    primary: Optional[PyAccountRateLimitWindow]
    secondary: Optional[PyAccountRateLimitWindow]
    credits: Optional[PyAccountCredits]
    individual_limit: Optional[PyAccountSpendLimit]
    plan_type: str
    rate_limit_reached_type: str

class PyAccountRateLimitWindow:
    used_percent: float
    window_duration_mins: int
    resets_at: int

class PyAccountCredits:
    has_credits: bool
    unlimited: bool
    balance: Optional[str]

class PyAccountSpendLimit:
    limit: float
    used: float
    remaining_percent: float
    resets_at: int

class PyAccountTokenUsageSummary:
    lifetime_tokens: Optional[str]
    peak_daily_tokens: Optional[str]
    longest_running_turn_sec: Optional[str]
    current_streak_days: Optional[str]
    longest_streak_days: Optional[str]

class PyAccountTokenUsageDailyBucket:
    start_date: str
    tokens: str

class PyAccountTokenUsage:
    summary: PyAccountTokenUsageSummary
    daily_usage_buckets: List[PyAccountTokenUsageDailyBucket]

class PyAccountUsage:
    account: Optional[PyAccountUsageAccount]
    token_usage: Optional[PyAccountTokenUsage]
    rate_limits: PyAccountRateLimits
    rate_limits_by_limit_id: dict
    raw_rate_limits: str
    raw_token_usage: Optional[str]
    raw_account: Optional[str]

class PyAccountUsageOutcome:
    result: Optional[PyAccountUsage]
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

def get_account_usage(req: object = ...) -> PyAccountUsageOutcome: ...
