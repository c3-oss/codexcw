set shell := ["/bin/bash", "-c"]

# --------------------------------------------------------------------------------------------------

_help:
    @just --list

# --------------------------------------------------------------------------------------------------
# Rust core

# build the core crate
build:
    cargo build -p codexcw

# run core unit and integration tests
test:
    cargo test -p codexcw

# format every crate
fmt:
    cargo fmt --all

# verify formatting
fmt-check:
    cargo fmt --all -- --check

# clippy across the workspace, warnings as errors
lint:
    cargo clippy --workspace --all-targets -- -D warnings

# dependency advisory and license audit
audit:
    @command -v cargo-deny >/dev/null 2>&1 || { echo "cargo-deny not in PATH — run 'just tools'"; exit 127; }
    cargo deny check
    @command -v cargo-audit >/dev/null 2>&1 || { echo "cargo-audit not in PATH — run 'just tools'"; exit 127; }
    cargo audit

# install Rust dev tools (cargo-deny, cargo-audit)
tools:
    cargo install cargo-deny cargo-audit --locked

# --------------------------------------------------------------------------------------------------
# Node binding (@c3-oss/codexcw)

# build the native addon
build-node:
    cd bindings/node && npm install && npm run build:debug

# run the Node smoke tests
test-node:
    cd bindings/node && npm test

# --------------------------------------------------------------------------------------------------
# Python binding (codexcw)

# build the extension into a local venv with dev dependencies
build-py:
    cd bindings/python && uv venv && uv pip install -e ".[dev]"

# run the Python smoke tests
test-py:
    cd bindings/python && .venv/bin/python -m pytest -q

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

# --------------------------------------------------------------------------------------------------

# local CI lane (mirrors .github/workflows/ci.yml)
ci: fmt-check lint test audit quality build-node test-node build-py test-py
    git diff --exit-code

# remove build outputs
clean:
    cargo clean
    rm -rf bindings/node/node_modules bindings/node/*.node
    rm -rf bindings/python/.venv bindings/python/python/codexcw/_codexcw*.so
    rm -rf dist
