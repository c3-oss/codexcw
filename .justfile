set shell := ["/bin/bash", "-c"]

BIN := "bin"

# --------------------------------------------------------------------------------------------------

_help:
    @just --list

# --------------------------------------------------------------------------------------------------
# Go (root module: github.com/c3-oss/codexcw)

# install Go dev tools (govulncheck, gosec) into ./bin
go-tools:
    @mkdir -p {{ BIN }}
    GOBIN="$PWD/{{ BIN }}" go install golang.org/x/vuln/cmd/govulncheck@latest
    GOBIN="$PWD/{{ BIN }}" go install github.com/securego/gosec/v2/cmd/gosec@latest

# build every cmd/* into bin/
go-build:
    @mkdir -p {{ BIN }}
    go build -o {{ BIN }}/ ./cmd/...

# build then run bin/codexcw with the given args
go-run *ARGS:
    @just go-build
    @{{ BIN }}/codexcw {{ ARGS }}

# go test ./...
go-test:
    go test ./...

# go test with the race detector
go-test-race:
    go test -race -count=1 ./...

# coverage profile + per-function totals
go-cover:
    go test -coverprofile=coverage.out ./...
    go tool cover -func=coverage.out | tail -20

# go vet ./...
go-vet:
    go vet ./...

# golangci-lint
go-lint:
    golangci-lint run ./...

# gosec static security analysis
go-lint-sec:
    @command -v gosec >/dev/null 2>&1 || { echo "gosec not in PATH — run 'just go-tools'"; exit 127; }
    gosec -quiet ./...

# govulncheck vulnerability scan
go-lint-vuln:
    @command -v govulncheck >/dev/null 2>&1 || { echo "govulncheck not in PATH — run 'just go-tools'"; exit 127; }
    govulncheck ./...

# go mod tidy
go-tidy:
    go mod tidy

# verify go.mod/go.sum are tidy
go-tidy-check:
    go mod tidy
    git diff --exit-code -- go.mod go.sum

# goreleaser dry run (changelog validation)
go-snapshot:
    @command -v goreleaser >/dev/null 2>&1 || { echo "goreleaser is required"; exit 127; }
    goreleaser release --snapshot --clean

# local Go CI lane
go-ci: go-tidy-check go-vet go-lint go-lint-sec go-lint-vuln go-test-race go-build

# --------------------------------------------------------------------------------------------------
# Rust (workspace: crates/codexcw)

rust-build:
    cargo build -p codexcw

rust-test:
    cargo test -p codexcw

rust-fmt:
    cargo fmt --all

rust-fmt-check:
    cargo fmt --all -- --check

rust-lint:
    cargo clippy --workspace --all-targets -- -D warnings

rust-audit:
    @command -v cargo-deny >/dev/null 2>&1 || { echo "cargo-deny not in PATH — run 'just rust-tools'"; exit 127; }
    cargo deny check
    @command -v cargo-audit >/dev/null 2>&1 || { echo "cargo-audit not in PATH — run 'just rust-tools'"; exit 127; }
    cargo audit

rust-tools:
    cargo install cargo-deny cargo-audit --locked

# local Rust CI lane
rust-ci: rust-fmt-check rust-lint rust-test rust-audit

# --------------------------------------------------------------------------------------------------
# Node binding (@c3-oss/codexcw)

node-build:
    cd bindings/node && npm install && npm run build:debug

node-test:
    cd bindings/node && npm test

node-ci: node-build node-test

# --------------------------------------------------------------------------------------------------
# Python binding (codexcw)

py-build:
    cd bindings/python && uv venv && uv pip install -e ".[dev]"

py-test:
    cd bindings/python && .venv/bin/python -m pytest -q

py-ci: py-build py-test

# --------------------------------------------------------------------------------------------------
# Cross-cutting quality gates

# lint tracked Markdown files
lint-md:
    git ls-files -z -- "*.md" | xargs -0 markdownlint-cli2 --no-globs

# check links in tracked Markdown files
lint-links:
    git ls-files -z -- "*.md" | xargs -0 lychee --config lychee.toml --no-progress --verbose

# scan the current tree for secrets
lint-secrets:
    gitleaks detect --source . --no-git --redact --verbose

# focused non-language quality gates
quality: lint-md lint-links lint-secrets

# local pre-push hook gate
hooks-pre-push: quality

# install all language dev tools
tools: go-tools rust-tools

# --------------------------------------------------------------------------------------------------

# full local CI lane (mirrors .github/workflows/ci.yml)
ci: go-ci rust-ci node-ci py-ci quality
    git diff --exit-code

# remove build outputs across all languages
clean:
    rm -rf {{ BIN }} dist coverage.out coverage.txt coverage.html *.test
    cargo clean
    rm -rf bindings/node/node_modules bindings/node/*.node
    rm -rf bindings/python/.venv bindings/python/python/codexcw/_codexcw*.so
