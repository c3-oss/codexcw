set shell := ["/bin/bash", "-c"]

BIN := "bin"
DEFAULT_BINARY := "myapp"
CLI := "bin/" + DEFAULT_BINARY

# --------------------------------------------------------------------------------------------------

_help:
    @just --list

# --------------------------------------------------------------------------------------------------

# install Go-based dev tools (govulncheck, gosec) into ./bin
tools:
    @mkdir -p {{ BIN }}
    GOBIN="$PWD/{{ BIN }}" go install golang.org/x/vuln/cmd/govulncheck@latest
    GOBIN="$PWD/{{ BIN }}" go install github.com/securego/gosec/v2/cmd/gosec@latest

# build all binaries under cmd/ into bin/
build:
    @mkdir -p {{ BIN }}
    go build -o {{ BIN }}/ ./cmd/...

# alias of `build` (parity with multi-binary projects)
build-all: build

# build then run bin/<DEFAULT_BINARY> with the given args
run *ARGS:
    @just build
    @{{ CLI }} {{ ARGS }}

# run go test ./...
test:
    go test ./...

# run the test suite with the race detector
test-race:
    go test -race -count=1 ./...

# coverage profile + per-function totals
cover:
    go test -coverprofile=coverage.out ./...
    go tool cover -func=coverage.out | tail -20

# go vet ./...
vet:
    go vet ./...

# golangci-lint
lint:
    golangci-lint run ./...

# lint tracked Markdown files
lint-md:
    git ls-files -z -- "*.md" | xargs -0 markdownlint-cli2 --no-globs

# check links in tracked Markdown files
lint-links:
    git ls-files -z -- "*.md" | xargs -0 lychee --config lychee.toml --no-progress --verbose

# check the current tree for secrets
lint-secrets:
    gitleaks detect --source . --no-git --redact --verbose

# static security analysis (gosec)
lint-sec:
    @command -v gosec >/dev/null 2>&1 || { echo "gosec not in PATH — run 'just tools' to install it"; exit 127; }
    gosec -quiet ./...

# Go vulnerability database scan (govulncheck)
lint-vuln:
    @command -v govulncheck >/dev/null 2>&1 || { echo "govulncheck not in PATH — run 'just tools' to install it"; exit 127; }
    govulncheck ./...

# focused non-Go quality gates
quality: lint-md lint-links lint-secrets

# local pre-push hook gate
hooks-pre-push: quality

# go mod tidy
tidy:
    go mod tidy

# verify go.mod/go.sum are already tidy
tidy-check:
    go mod tidy
    git diff --exit-code -- go.mod go.sum

# local CI lane (mirrors .github/workflows/ci.yml)
ci: tidy-check vet lint lint-sec lint-vuln test-race build
    git diff --exit-code

# goreleaser dry run for local release validation
snapshot:
    @command -v goreleaser >/dev/null 2>&1 || { echo "goreleaser is required for just snapshot"; exit 127; }
    goreleaser release --snapshot --clean

# build the local Docker image
docker-build:
    docker build -t {{ DEFAULT_BINARY }}:local --target {{ DEFAULT_BINARY }} .

# rename the placeholder app — usage: just setup github.com/c3-oss/foo foo
setup MODULE BINARY:
    bash scripts/setup.sh {{ MODULE }} {{ BINARY }}

# remove build outputs
clean:
    rm -rf {{ BIN }} dist coverage.out coverage.txt coverage.html *.test
