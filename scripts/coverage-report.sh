#!/usr/bin/env bash
# Generate an HTML coverage report and open it locally.
set -euo pipefail

go test -coverprofile=coverage.out ./...
go tool cover -html=coverage.out -o coverage.html

case "$(uname -s)" in
    Darwin)  open coverage.html ;;
    Linux)   xdg-open coverage.html 2>/dev/null || echo "report at coverage.html" ;;
    *)       echo "report at coverage.html" ;;
esac
