# go-template

[![CI](https://github.com/c3-oss/go-template/actions/workflows/ci.yml/badge.svg)](https://github.com/c3-oss/go-template/actions/workflows/ci.yml)
[![Release](https://github.com/c3-oss/go-template/actions/workflows/release.yml/badge.svg)](https://github.com/c3-oss/go-template/actions/workflows/release.yml)
[![License: CC0 1.0](https://img.shields.io/badge/license-CC0%201.0-lightgrey.svg)](LICENSE)

A starting point for Go projects in the `c3-oss` org. Captures the
toolchain, hooks, CI/CD, release, security scanning, and agent
integrations already battle-tested across `prosa`, `nfe`, and
`lastfm-webp-widgets`.

## What you get

- **Layout**: `cmd/<binary>` + `internal/` + `pkg/`. Easy to add more binaries.
- **Toolchain pinned via [devbox](https://www.jetify.com/devbox)**: Go 1.26,
  just, golangci-lint v2 (with gofumpt + goimports), gosec, govulncheck,
  syft, gitleaks, lychee, markdownlint-cli2, GoReleaser, Node + pnpm.
- **Task runner**: a single `.justfile` exposes build, test, lint, quality,
  security, CI, docker, and release-snapshot targets.
- **Quality gates**: golangci-lint v2 standard linter set + gofumpt
  formatter; markdown + link checks; secret scanning.
- **Security**: `gosec` static analysis, `govulncheck` against the public
  vulnerability database, SPDX SBOMs published per release archive (Syft).
- **Hooks**: Husky-managed `pre-commit` (lint-staged + gitleaks staged),
  `commit-msg` (commitlint — Conventional Commits with mandatory scope),
  and `pre-push` (full quality gate).
- **CI/CD**: GitHub Actions runs `quality`, `test` (race + coverage),
  `lint`, `security`, and a cross-platform `build` matrix on every PR.
  Tag releases trigger GoReleaser + Docker multi-arch GHCR push + SBOM.
- **Dependabot**: weekly updates for Go modules, GitHub Actions, npm, Docker.
- **Agent-aware**: `AGENTS.md`, `CLAUDE.md`, `.claude/`, `.codex/`, and a
  devcontainer config ready to extend.

## Using this template

Click **Use this template** on GitHub, clone the new repo, then:

```bash
# 1. Rename the placeholder ("myapp") to your binary, and the module path
#    to your new repo. The script handles cmd/<name>/, go.mod, Dockerfile,
#    GoReleaser, and every other reference.
./scripts/setup.sh github.com/c3-oss/<your-repo> <your-binary>

# 2. Enter the pinned toolchain (installs Go-based tools + Node deps,
#    wires Husky hooks).
devbox shell

# 3. Validate everything still works.
just ci
```

Commit the rename (`chore(setup): rename template to <name>`) and you're ready
to develop.

## Quick reference

```bash
just build         # compile all cmd/* into bin/
just run           # build then run the default binary
just test-race     # full race detector
just lint          # golangci-lint v2
just lint-sec      # gosec
just lint-vuln     # govulncheck
just quality       # markdown + link check + secret scan
just ci            # local mirror of the PR pipeline
just snapshot      # goreleaser --snapshot (writes dist/ with SBOMs)
just docker-build  # build the local Docker image
```

See [`AGENTS.md`](AGENTS.md) for the canonical project guide.

## License

[CC0 1.0 Universal](LICENSE) — public domain dedication. Do whatever you
want with this template.
