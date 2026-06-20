# AGENTS

Canonical guide for humans and AI coding agents working in this repository.
Read this end-to-end before proposing substantial changes.

## Project shape

- **Module**: declared in `go.mod`. The template ships as `github.com/c3-oss/go-template`; after `./scripts/setup.sh` it will reflect your repo path.
- **Layout**:
  - `cmd/<binary>/` — entrypoints (`main` packages). The template ships with one (`cmd/myapp/`); add more by mirroring the pattern.
  - `internal/` — non-exportable application code (`buildinfo`, `cli`, `logging`).
  - `pkg/` — exportable packages. Empty by default; add carefully — API stability matters here.
  - `scripts/` — small bash utilities (rename, coverage report).
  - `docs/` — long-form documentation. Architecture, design notes, runbooks.
- **Generated outputs** (gitignored): `bin/`, `dist/`, `coverage.*`.

## Build, test, develop

Toolchain is pinned in `devbox.json`. Enter the shell with `devbox shell` and
all subsequent commands resolve through the pinned versions.

Common tasks (run via `just <target>`):

| Target | Purpose |
|---|---|
| `just build` | compile every `cmd/*` into `bin/` |
| `just test` | `go test ./...` |
| `just test-race` | full race detector + `-count=1` |
| `just cover` | coverage profile and per-function totals |
| `just lint` | `golangci-lint run ./...` |
| `just lint-sec` | `gosec` static security analysis |
| `just lint-vuln` | `govulncheck` against `./...` |
| `just quality` | Markdown lint, link check, secret scan |
| `just ci` | local mirror of the PR pipeline |
| `just snapshot` | GoReleaser dry-run with SBOMs |
| `just docker-build` | local Docker image |
| `just clean` | drop build outputs |

`just tools` installs Go-based binaries (`govulncheck`, `gosec`) into
`./bin`. Devbox does this automatically on `devbox shell` entry.

## Coding style

- `gofumpt` formats Go (stricter than `gofmt`). Run via golangci-lint or
  your editor.
- `goimports` orders imports.
- Tests live alongside source: `*_test.go` in the same package.
- Test assertions use `github.com/stretchr/testify/require` for failures
  that should abort the test and `assert` for soft checks.
- Logging goes to stderr (via `internal/logging`); stdout is reserved for
  command output that callers may pipe.
- Comments explain *why*, not *what*. Identifiers carry the *what*.

## Commits and PRs

Conventional Commits with **mandatory scope** are enforced by commitlint
and validated by CI.

- Format: `<type>(<scope>): <subject>` — e.g. `feat(cli): add status command`.
- Allowed types (any `@commitlint/config-conventional` type): `feat`, `fix`,
  `chore`, `docs`, `test`, `build`, `ci`, `refactor`, `perf`, `style`, `revert`.
- The CI changelog (via GoReleaser) groups `feat` and `fix` and drops
  `docs`, `test`, `chore`, `ci`, and merge commits.

PRs target `master`. CI runs five jobs that must all pass: `quality`,
`test`, `lint`, `security`, `build` (Ubuntu + macOS matrix).

## Hooks

`./.husky/` is wired automatically by `pnpm install` (which runs as part
of `devbox shell`).

- `pre-commit` → `lint-staged` (Markdown) + `gitleaks protect --staged`.
- `commit-msg` → `commitlint` (mandatory scope).
- `pre-push` → `just hooks-pre-push` (== `just quality`).

## Releases

Push a tag `v<semver>` to `master` and `.github/workflows/release.yml`
takes it from there: GoReleaser builds binaries for linux/darwin × amd64/arm64,
publishes archives + SHA-256 checksums + per-archive SPDX SBOMs (via Syft),
and Docker pushes a multi-arch image to GHCR.

`just snapshot` is the local equivalent and writes everything to `dist/`.

## What is intentionally *not* here

- No `Makefile` — `just` only.
- No `pkg/` content out of the box — add carefully, exports become contracts.
- No CGO. Switch `CGO_ENABLED=1` in `Dockerfile` and `.goreleaser.yaml` if you need it.
- No web framework / DB driver / message broker — add what the project needs.
- No `.claude/agents` or `.codex/skills` content — the structure is ready; populate per project.
