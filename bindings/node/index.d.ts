// Public TypeScript API for @c3-oss/codexcw.

/** Sandbox policy passed to `codex exec`. */
export type SandboxMode = 'read-only' | 'workspace-write' | 'danger-full-access'

/** Approval policy passed to Codex. */
export type ApprovalPolicy = 'untrusted' | 'on-request' | 'never'

/** One `-c key=value` config override. */
export interface ConfigOverride {
  key: string
  value: string
}

/** Token usage reported by Codex. */
export interface Usage {
  inputTokens: number
  cachedInputTokens: number
  outputTokens: number
  reasoningOutputTokens: number
  totalTokens: number
}

/** Options for reading Codex account usage. */
export interface AccountUsageRequest {
  executable?: string
  env?: Record<string, string>
  /** Per-request JSON-RPC timeout in milliseconds. Defaults to 10 seconds. */
  timeoutMs?: number
}

/** Codex account limits and credits. */
export interface AccountUsage {
  account?: AccountUsageAccount
  tokenUsage?: AccountTokenUsage
  rateLimits: AccountRateLimits
  rateLimitsByLimitId: Record<string, AccountRateLimits>
  rawRateLimits: string
  rawTokenUsage?: string
  rawAccount?: string
}

/** Authenticated account reported by Codex. */
export interface AccountUsageAccount {
  type: string
  email: string
  planType: string
  requiresOpenaiAuth: boolean
}

/** One Codex rate-limit set. */
export interface AccountRateLimits {
  limitId: string
  limitName: string
  primary?: AccountRateLimitWindow
  secondary?: AccountRateLimitWindow
  credits?: AccountCredits
  individualLimit?: AccountSpendLimit
  planType: string
  rateLimitReachedType: string
}

/** One account usage window. */
export interface AccountRateLimitWindow {
  usedPercent: number
  windowDurationMins: number
  resetsAt: number
}

/** Codex credit balance snapshot. */
export interface AccountCredits {
  hasCredits: boolean
  unlimited: boolean
  balance?: string
}

/** Individual spend or credit-control limit. */
export interface AccountSpendLimit {
  limit: number
  used: number
  remainingPercent: number
  resetsAt: number
}

/** Account token-usage summary reported by Codex. */
export interface AccountTokenUsage {
  summary: AccountTokenUsageSummary
  dailyUsageBuckets: AccountTokenUsageDailyBucket[]
}

/** Aggregate account token-usage metrics. */
export interface AccountTokenUsageSummary {
  lifetimeTokens?: string
  peakDailyTokens?: string
  longestRunningTurnSec?: string
  currentStreakDays?: string
  longestStreakDays?: string
}

/** One daily account token-usage bucket. */
export interface AccountTokenUsageDailyBucket {
  startDate: string
  tokens: string
}

/** One file edit inside a `file_change` item. */
export interface FileChange {
  path: string
  kind: string
}

/** A typed projection of a Codex item. */
export interface Item {
  id: string
  type: string
  status: string
  text: string
  message: string
  command: string
  aggregatedOutput: string
  exitCode?: number
  raw: string
  changes: FileChange[]
}

/** One decoded Codex event. */
export interface CodexEvent {
  type: string
  runId: string
  threadId: string
  /** The original JSON event text. */
  raw: string
  /** Set for `item.started` and `item.completed`. */
  item?: Item
  /** Set for `turn.completed`. */
  usage?: Usage
  /** Set for `error` and `turn.failed`. */
  error?: string
}

/** Summary of a completed run. */
export interface RunResult {
  runId: string
  threadId: string
  finalMessage: string
  usage: Usage
  events: CodexEvent[]
  stderr: string
  startedAtMs: number
  finishedAtMs: number
}

/** A Codex run request. All fields are optional except prompt or stdin. */
export interface Request {
  prompt?: string
  stdin?: string
  dir?: string
  addDirs?: string[]
  images?: string[]
  model?: string
  profile?: string
  sandbox?: SandboxMode
  approval?: ApprovalPolicy
  config?: ConfigOverride[]
  enable?: string[]
  disable?: string[]
  strictConfig?: boolean
  persistent?: boolean
  ignoreUserConfig?: boolean
  ignoreRules?: boolean
  requireGitRepo?: boolean
  outputSchemaPath?: string
  outputSchema?: string
  outputLastMessagePath?: string
  dangerouslyBypassSandbox?: boolean
  dangerouslyBypassHooks?: boolean
  env?: Record<string, string>
  resumeId?: string
  resumeLast?: boolean
  resumeAll?: boolean
}

/** Options for constructing a {@link Runner}. */
export interface RunnerOptions {
  executable?: string
  env?: Record<string, string>
  eventBuffer?: number
  stderrLimit?: number
  scanMaxBytes?: number
  defaultSandbox?: SandboxMode
  defaultApproval?: ApprovalPolicy
}

/** Options for {@link Runner.runMany}. */
export interface ManyOptions {
  maxConcurrent?: number
  eventBuffer?: number
}

/** One multiplexed event from {@link Runner.runMany}. */
export interface RunEvent {
  runId: string
  index: number
  event: CodexEvent
}

/** One result from {@link Runner.runMany}. */
export interface GroupResult {
  index: number
  runId: string
  result: RunResult | null
  error: CodexcwError | null
}

/** The kind discriminator on a {@link CodexcwError}. */
export type ErrorKind =
  | 'promptRequired'
  | 'invalidRequest'
  | 'exit'
  | 'decode'
  | 'codex'
  | 'handler'
  | 'cancelled'
  | 'process'

/** A typed Codex run error. */
export declare class CodexcwError extends Error {
  kind: ErrorKind
  /** Process exit code, for `exit` errors. */
  code?: number
  /** Captured stderr tail, for `exit` errors. */
  stderr?: string
  /** One-based JSONL line number, for `decode` errors. */
  line?: number
}

/** A running `codex exec` process. */
export declare class Session {
  readonly id: string
  threadId(): string
  cancel(): void
  /** Streams decoded events until the process exits. */
  events(): AsyncIterableIterator<CodexEvent>
  /** Waits for the process to exit; rejects with {@link CodexcwError}. */
  wait(): Promise<RunResult>
}

/** A batch of running `codex exec` processes. */
export declare class Group {
  cancel(): void
  /** Streams multiplexed events until every run finishes. */
  events(): AsyncIterableIterator<RunEvent>
  wait(): Promise<GroupResult[]>
}

/** Starts `codex exec` processes with safe automation defaults. */
export declare class Runner {
  constructor(options?: RunnerOptions)
  /** Launches one process and returns a {@link Session}. */
  start(req: Request): Promise<Session>
  /**
   * Runs one process to completion. With `onEvent`, the callback runs for each
   * event; a throw cancels the run. Rejects with {@link CodexcwError}.
   */
  run(
    req: Request,
    onEvent?: (event: CodexEvent) => void | Promise<void>,
  ): Promise<RunResult>
  /** Launches many processes with bounded concurrency. */
  runMany(reqs: Request[], options?: ManyOptions): Promise<Group>
}

/** Reads Codex account usage and limits through `codex app-server`. */
export declare function getAccountUsage(
  req?: AccountUsageRequest,
): Promise<AccountUsage>
