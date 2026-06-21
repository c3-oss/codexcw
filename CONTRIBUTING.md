# Contributing

Thanks for helping improve this project.

## Development environment

The toolchain is pinned in `devbox.json`. The recommended workflow:

```bash
devbox shell      # enters the pinned environment (Go + Rust + Node + Python), wires hooks
just ci           # runs the full local CI lane across all four languages
```

Without devbox you'll need: a Go toolchain (see `go.mod`), a Rust toolchain (see
`rust-toolchain.toml`), `just`, Node 24 + npm, Python 3.9+, `uv`, `maturin`, plus
`gitleaks`, `lychee`, and `markdownlint-cli2`. `just tools` installs the Go and
Rust dev tools (`govulncheck`, `gosec`, `cargo-deny`, `cargo-audit`).

## Branching and PRs

- Branch off `master`. Open a PR targeting `master`.
- Keep PRs focused. Work in one language area when possible; mixed changes are
  fine when they share a contract (e.g. a spec/fixture change touching all four).
- CI must be green before merge: `quality`, the Go jobs, `rust`, `node`, `python`.

## Commit messages

[Conventional Commits](https://www.conventionalcommits.org/) with a **mandatory
scope**. Examples:

- `feat(core): add resume support`
- `fix(go): allow configured codex subprocess launch`
- `fix(node): surface decode errors as typed errors`
- `docs(readme): clarify install instructions`

The `commit-msg` hook validates every commit locally; CI re-validates the range
on each PR. (Dependabot `(deps)` bumps are exempt from the subject-case rule.)

## Style

- **Go** — `gofumpt` + `goimports` via golangci-lint; `testify` for assertions.
- **Rust** — `rustfmt` + `clippy -D warnings`; full doc coverage on the core crate.
- Process behavior is tested against a fake `codex`; the same JSONL fixture drives
  the Go, Rust, Node, and Python smoke tests. Keep the four in lockstep.
- Comments explain *why*, not *what*.

## Releasing

Push a tagged release for the affected artifact: `v<semver>` (Go),
`rust-v<semver>`, `node-v<semver>`, or `py-v<semver>`. See
[`AGENTS.md`](AGENTS.md#releases).
