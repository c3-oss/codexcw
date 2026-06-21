# AGENTS

Canonical guide for humans and AI coding agents working in this repository.
Read this end-to-end before proposing substantial changes.

## Project shape

A Cargo workspace whose FFI-free core crate is wrapped by two thin binding
crates and published to three registries.

- **`crates/codexcw/`** — the public Rust library (`codexcw` on crates.io). It
  spawns `codex exec --json`, decodes the JSONL stream, and exposes runs as
  async streams, callbacks, results, and typed errors. It depends on **no** FFI
  crate.
- **`bindings/node/`** — the napi-rs binding crate plus the npm package
  `@c3-oss/codexcw`. A hand-written `index.js`/`index.d.ts` wraps the generated
  native loader (`binding.js`) with idiomatic ergonomics.
- **`bindings/python/`** — the PyO3 binding crate (`_codexcw`) plus the PyPI
  package `codexcw`. The native module is synchronous; `codexcw/__init__.py` adds
  the `Request` dataclass and typed exceptions, and `codexcw/aio.py` adds an
  asyncio facade.
- **Generated outputs** (gitignored): `target/`, `node_modules/`, `*.node`,
  `*.so`, `.venv/`, `dist/`.

## Build, test, develop

The toolchain is pinned in `devbox.json` (Rust via `rustup` + `rust-toolchain.toml`,
plus Node, Python, `uv`, `maturin`, and the quality tools). Enter with
`devbox shell` and run tasks via `just`:

| Target | Purpose |
|---|---|
| `just build` | build the core crate |
| `just test` | core unit + integration tests |
| `just fmt-check` | verify `rustfmt` formatting |
| `just lint` | `clippy --workspace`, warnings as errors |
| `just audit` | `cargo deny` + `cargo audit` |
| `just build-node` / `just test-node` | build and smoke-test the npm package |
| `just build-py` / `just test-py` | build and smoke-test the PyPI package |
| `just quality` | markdown lint, link check, secret scan |
| `just ci` | local mirror of the PR pipeline |

## Coding style

- `rustfmt` formats Rust; `clippy` runs with `-D warnings`.
- The core crate sets `#![warn(missing_docs)]`; every exported item is documented.
- Library code preserves the raw Codex JSON (`Event.raw`) when adding typed helpers.
- Tests for Codex process behavior use a fake `codex` executable via the runner's
  `executable` seam. The same fake JSONL fixture drives the Rust, Node, and Python
  smoke tests so all three decode identically.
- Comments explain *why*, not *what*.

## API parity

The three surfaces mirror the same model: a `Runner` with `run` / `start` /
`run_many`, a `Session` exposing an event stream plus `wait`, a `Group` for
batches, and a typed error carrying a `kind` discriminator. TypeScript is
async-first; Python is sync-first with an `aio` async variant.

## Commits and PRs

Conventional Commits with a **mandatory scope** are enforced by commitlint and
validated by CI.

- Format: `<type>(<scope>): <subject>` — e.g. `feat(core): add resume support`.
- Common scopes: `core`, `node`, `py`, `ci`, `tooling`, `docs`.
- Allowed types: `feat`, `fix`, `chore`, `docs`, `test`, `build`, `ci`,
  `refactor`, `perf`, `style`, `revert`.

PRs target `master`. CI runs `quality`, `rust`, `node`, and `python` jobs that
must all pass.

## Hooks

`./.husky/` is wired by `pnpm install` (which runs on `devbox shell` entry).

- `pre-commit` → `lint-staged` (Markdown) + `gitleaks protect --staged`.
- `commit-msg` → `commitlint` (mandatory scope).
- `pre-push` → `just hooks-pre-push` (== `just quality`).

## Releases

Each artifact has its own release train, triggered by a tag prefix:

- `rust-v<semver>` → `.github/workflows/release-crate.yml` publishes to crates.io.
- `node-v<semver>` → `.github/workflows/release-npm.yml` builds per-platform
  addons and publishes `@c3-oss/codexcw` to npm.
- `py-v<semver>` → `.github/workflows/release-pypi.yml` builds wheels + sdist and
  publishes `codexcw` to PyPI.

The publish steps are placeholders until the first release; until then the
workflows only verify packaging.

## Repository boundaries

- Task automation uses `just`; the toolchain is pinned with `devbox`.
- The core crate's public API is a contract and stays free of `napi`/`pyo3` types.
- `cargo publish -p codexcw` ships only the core crate; binding crates are
  `publish = false`.
- Agent integrations live in `.claude/` and `.codex/`.
