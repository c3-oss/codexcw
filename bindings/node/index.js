// Public CommonJS API for @c3-oss/codexcw.
// Wraps the native binding (binding.js) with idiomatic ergonomics: an
// async-iterable event stream, an optional per-event callback, and typed
// errors.

const native = require('./binding.js')

/** Model aliases accepted by the claude agent's `model` field. */
const ClaudeModel = Object.freeze({
  Haiku: 'haiku',
  Sonnet: 'sonnet',
  Opus: 'opus',
})

/** Permission modes accepted by the claude agent's `permissionMode` field. */
const PermissionMode = Object.freeze({
  AcceptEdits: 'acceptEdits',
  Auto: 'auto',
  BypassPermissions: 'bypassPermissions',
  Manual: 'manual',
  Plan: 'plan',
  DontAsk: 'dontAsk',
})

/** A typed agent run error. */
class CodexcwError extends Error {
  constructor(info) {
    super(info.message)
    this.name = 'CodexcwError'
    /** @type {string} */
    this.kind = info.kind
    if (info.code != null) this.code = info.code
    if (info.stderr != null) this.stderr = info.stderr
    if (info.line != null) this.line = info.line
  }
}

async function* drain(inner) {
  for (;;) {
    const event = await inner.nextEvent()
    if (event == null) break
    yield event
  }
}

/** A running selected-agent process. */
class Session {
  constructor(inner) {
    this._inner = inner
  }

  get id() {
    return this._inner.id
  }

  threadId() {
    return this._inner.threadId()
  }

  cancel() {
    this._inner.cancel()
  }

  events() {
    return drain(this._inner)
  }

  async wait() {
    const outcome = await this._inner.wait()
    if (outcome.error) throw new CodexcwError(outcome.error)
    return outcome.result
  }
}

/** A batch of running selected-agent processes. */
class Group {
  constructor(inner) {
    this._inner = inner
  }

  cancel() {
    this._inner.cancel()
  }

  events() {
    return drain(this._inner)
  }

  async wait() {
    const results = await this._inner.wait()
    return results.map((r) => ({
      index: r.index,
      runId: r.runId,
      result: r.result ?? null,
      error: r.error ? new CodexcwError(r.error) : null,
    }))
  }
}

/** Starts selected-agent processes with safe automation defaults. */
class Runner {
  constructor(options) {
    if (options?.agent != null && options.agent !== 'codex' && options.agent !== 'claude') {
      throw new CodexcwError({
        kind: 'invalidRequest',
        message: `unknown agent: ${options.agent}`,
      })
    }
    this._inner = new native.Runner(options)
  }

  async start(req) {
    return new Session(await this._inner.start(req))
  }

  async run(req, onEvent) {
    if (!onEvent) {
      const outcome = await this._inner.runRaw(req)
      if (outcome.error) throw new CodexcwError(outcome.error)
      return outcome.result
    }
    const session = await this.start(req)
    try {
      for await (const event of session.events()) {
        await onEvent(event)
      }
    } catch (err) {
      session.cancel()
      await session.wait().catch(() => {})
      throw err
    }
    return session.wait()
  }

  async runMany(reqs, options) {
    return new Group(await this._inner.runMany(reqs, options))
  }
}

/** Reads Codex account usage and limits through `codex app-server`. */
async function getAccountUsage(req) {
  const outcome = await native.getAccountUsageRaw(req ?? null)
  if (outcome.error) throw new CodexcwError(outcome.error)
  if (!outcome.result) {
    throw new CodexcwError({
      kind: 'process',
      message: 'account usage result missing',
    })
  }
  return outcome.result
}

/** Reads Claude account usage through the Claude Code `/usage` command. */
async function getClaudeAccountUsage(req) {
  const outcome = await native.getClaudeAccountUsageRaw(req ?? null)
  if (outcome.error) throw new CodexcwError(outcome.error)
  if (!outcome.result) {
    throw new CodexcwError({
      kind: 'process',
      message: 'Claude account usage result missing',
    })
  }
  return outcome.result
}

module.exports = {
  Runner,
  Session,
  Group,
  CodexcwError,
  getAccountUsage,
  getClaudeAccountUsage,
  ClaudeModel,
  PermissionMode,
}
