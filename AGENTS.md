# AGENTS

Canonical guide for humans and AI coding agents working in this repository.
Read this end-to-end before proposing substantial changes.

## Project shape

A polyglot monorepo: **four independent, idiomatic implementations** of the same
`codex exec --json` wrapper. There is **no FFI between Go and the Rust side** —
each implementation is native to its ecosystem; they share a repo so the spec and
test fixtures stay a single source of truth.

- **Go (root)** — the public Go library `github.com/c3-oss/codexcw`. Go module at
  the repo root (`go.mod`), CGO-free. Root `*.go`, `cmd/codexcw/` (example CLI),
  `internal/`, `scripts/`.
- **`crates/codexcw/`** — the Rust core (`codexcw` on crates.io), async (tokio),
  FFI-free.
- **`bindings/node/`** — napi-rs binding + npm package `@c3-oss/codexcw` (a
  hand-written `index.js` wraps the generated native loader).
- **`bindings/python/`** — PyO3 binding (`_codexcw`) + PyPI package `codexcw`
  (sync API in `codexcw/__init__.py`, asyncio facade in `codexcw/aio.py`).
- **Generated outputs** (gitignored): `bin/`, `target/`, `dist/`, `node_modules/`,
  `*.node`, `*.so`, `.venv/`.

Complete usage recipes per language (quickstart, streaming, resume, sandbox /
approval, bypass, batches, structured output, errors — in both sync and async
forms) live in `docs/examples/{go,rust,typescript,python}.md`. The Codex skill
(`.codex/skills/codexcw/`) and the `codexcw-expert` Claude subagent point there.

## Build, test, develop

Toolchain is pinned in `devbox.json` (Go + golangci-lint + goreleaser; Rust via
`rustup` + `rust-toolchain.toml`; Node + pnpm; Python + uv + maturin; plus the
quality tools). Enter with `devbox shell` and run via `just`. Recipes are
**language-namespaced**:

| Prefix | Examples |
|---|---|
| `go-*` | `go-build`, `go-test`, `go-test-race`, `go-vet`, `go-lint`, `go-lint-sec`, `go-lint-vuln`, `go-tidy-check`, `go-ci` |
| `rust-*` | `rust-build`, `rust-test`, `rust-fmt-check`, `rust-lint`, `rust-audit`, `rust-ci` |
| `node-*` | `node-build`, `node-test`, `node-ci` |
| `py-*` | `py-build`, `py-test`, `py-ci` |
| shared | `lint-md`, `lint-links`, `lint-secrets`, `quality`, `tools`, `clean` |

`just ci` runs every language lane plus `quality` and ends with
`git diff --exit-code`.

## Coding style

- **Go**: `gofumpt` + `goimports` (via golangci-lint); tests use
  `testify/require`/`assert`; logging to stderr; `godoc_test.go` enforces doc
  coverage on exported identifiers.
- **Rust**: `rustfmt` + `clippy -D warnings`; the core crate sets
  `#![warn(missing_docs)]`.
- Library code preserves the raw Codex JSON (`Raw`/`raw`) when adding typed helpers.
- Process behavior is tested against a **fake `codex` executable**; the same JSONL
  fixture drives the Go, Rust, Node, and Python smoke tests so all four decode
  identically. This shared spec/fixtures is the reason the implementations live
  together — keep them in lockstep.
- Comments explain *why*, not *what*.

## Commits and PRs

Conventional Commits with a **mandatory scope**, enforced by commitlint.

- Format: `<type>(<scope>): <subject>` — e.g. `feat(core): add resume support`.
- Common scopes: `go`/`cli`, `core`, `node`, `py`, `ci`, `tooling`, `docs`, `repo`.
- Dependabot's `(deps)` bumps are exempt from the subject-case rule (see
  `commitlint.config.cjs`).

PRs target `master`. CI runs `quality`, the Go jobs, `rust`, `node`, and
`python`.

## Hooks

`./.husky/` is wired by `pnpm install` (runs on `devbox shell` entry).

- `pre-commit` → `lint-staged` (Markdown) + `gitleaks protect --staged`.
- `commit-msg` → `commitlint` (mandatory scope).
- `pre-push` → `just hooks-pre-push` (== `just quality`).

## Releases

Four independent trains, each on its own tag prefix:

- `v<semver>` → `release-go.yml` (GoReleaser changelog; the Go module release).
- `rust-v<semver>` → `release-crate.yml` (crates.io).
- `node-v<semver>` → `release-npm.yml` (npm, per-platform native addons).
- `py-v<semver>` → `release-pypi.yml` (PyPI wheels + sdist).

Publish steps in the non-Go trains are placeholders until the first release.

## Repository boundaries

- The Go module stays at the repo root (import path
  `github.com/c3-oss/codexcw`); the Rust workspace and bindings are siblings.
- The Rust core crate's public API stays free of `napi`/`pyo3` types;
  `cargo publish -p codexcw` ships only the core.
- Agent integrations live in `.claude/` and `.codex/`.
