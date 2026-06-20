# Contributing

Thanks for helping improve this project.

## Development environment

The toolchain is pinned in `devbox.json`. The recommended workflow:

```bash
devbox shell      # enters the pinned environment, installs Node deps, wires hooks
just ci           # runs the full local CI lane (tidy, vet, lint, sec, test, build)
```

Without devbox you'll need: Go 1.26+, just, Node 24 + pnpm 10,
golangci-lint v2, gofumpt, gosec, govulncheck, gitleaks, lychee,
markdownlint-cli2, and GoReleaser. Mileage may vary.

## Branching and PRs

- Branches off `master`. Open a PR targeting `master`.
- Keep PRs focused. Refactors, bug fixes, and feature work belong in
  separate PRs unless the dependency is structural.
- CI must be green before merge: `quality`, `test`, `lint`, `security`,
  and `build / {ubuntu,macos}-latest`.

## Commit messages

[Conventional Commits](https://www.conventionalcommits.org/) with a
**mandatory scope**. Examples:

- `feat(cli): add status command`
- `fix(server): handle empty body on POST /healthz`
- `chore(deps): bump testify to v1.12`
- `docs(readme): clarify install instructions`

The `commit-msg` hook validates every commit locally; CI re-validates the
range on each PR.

## Style

- Run `golangci-lint run` — it enforces the standard linter set plus the
  `gofumpt` formatter and `goimports` ordering.
- Tests use `testify/require` for fatal assertions and `testify/assert`
  for soft checks.
- Comments explain *why*, not *what*.

## Releasing

Tag a `v<semver>` on `master`. CI publishes binaries, Docker images, and
SBOMs automatically. See [`AGENTS.md`](AGENTS.md#releases) for details.
