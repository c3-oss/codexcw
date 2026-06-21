# Contributing

Thanks for helping improve this project.

## Development environment

The toolchain is pinned in `devbox.json`. The recommended workflow:

```bash
devbox shell      # enters the pinned environment, installs deps, wires hooks
just ci           # runs the full local CI lane
```

Without devbox you'll need: a Rust toolchain (see `rust-toolchain.toml`),
`just`, Node 24 + npm, Python 3.9+, `uv`, `maturin`, plus `gitleaks`, `lychee`,
and `markdownlint-cli2`. Install the Rust dev tools with `just tools`
(`cargo-deny`, `cargo-audit`).

## Branching and PRs

- Branch off `master`. Open a PR targeting `master`.
- Keep PRs focused. Refactors, bug fixes, and feature work belong in separate
  PRs unless the dependency is structural.
- CI must be green before merge: `quality`, `rust`, `node`, and `python`.

## Commit messages

[Conventional Commits](https://www.conventionalcommits.org/) with a **mandatory
scope**. Examples:

- `feat(core): add resume support`
- `fix(node): surface decode errors as typed errors`
- `chore(deps): bump pyo3 to 0.29`
- `docs(readme): clarify install instructions`

The `commit-msg` hook validates every commit locally; CI re-validates the range
on each PR.

## Style

- Rust is formatted by `rustfmt` and linted by `clippy` (`-D warnings`).
- The core crate keeps full doc coverage (`#![warn(missing_docs)]`).
- Process behavior is tested with a fake `codex` executable; the same JSONL
  fixture drives the Rust, Node, and Python smoke tests.
- Comments explain *why*, not *what*.

## Releasing

Push a tagged release for the affected artifact: `rust-v<semver>`,
`node-v<semver>`, or `py-v<semver>`. See [`AGENTS.md`](AGENTS.md#releases).
