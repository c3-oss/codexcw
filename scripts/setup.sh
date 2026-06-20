#!/usr/bin/env bash
# Rename the template placeholder to a real application.
#
# Usage:
#   ./scripts/setup.sh <new-module-path> <new-binary-name>
#
# Example:
#   ./scripts/setup.sh github.com/c3-oss/widgetd widgetd
#
# What it does:
#   - Replaces `github.com/c3-oss/go-template` → <new-module-path>
#   - Replaces every standalone token `myapp` → <new-binary-name>
#   - Renames cmd/myapp/ → cmd/<new-binary-name>/
#   - Runs `go mod tidy`
#
# Pre-conditions:
#   - Working tree must be clean (no uncommitted changes).
#   - Must run from the repo root.

set -euo pipefail

if [[ $# -ne 2 ]]; then
    echo "usage: $0 <new-module-path> <new-binary-name>" >&2
    exit 64
fi

NEW_MODULE="$1"
NEW_BINARY="$2"

OLD_MODULE="github.com/c3-oss/go-template"
OLD_BINARY="myapp"

# sanity: repo root
if [[ ! -f "go.mod" ]] || ! grep -q "^module ${OLD_MODULE}$" go.mod; then
    echo "error: must run from repo root, and go.mod must still declare ${OLD_MODULE}" >&2
    echo "       (has setup.sh already been run?)" >&2
    exit 1
fi

# sanity: clean working tree
if [[ -n "$(git status --porcelain 2>/dev/null || true)" ]]; then
    echo "error: working tree is not clean. Commit or stash before running setup." >&2
    exit 1
fi

# sanity: binary name shape
if [[ ! "${NEW_BINARY}" =~ ^[a-z][a-z0-9_-]*$ ]]; then
    echo "error: binary name must be lowercase alphanumeric (with - or _), got '${NEW_BINARY}'" >&2
    exit 1
fi

echo "==> renaming module: ${OLD_MODULE} → ${NEW_MODULE}"
echo "==> renaming binary: ${OLD_BINARY} → ${NEW_BINARY}"

SED_INPLACE=(-i)
if [[ "$(uname -s)" == "Darwin" ]]; then
    SED_INPLACE=(-i '')
fi

# tracked files only, skip binary/vendored content
git ls-files -z | while IFS= read -r -d '' file; do
    case "$file" in
        scripts/setup.sh) continue ;;        # don't mutate this script while running
        LICENSE) continue ;;
        *.png|*.jpg|*.jpeg|*.gif|*.webp|*.ico|*.pdf) continue ;;
    esac
    if ! grep -Iq . "$file" 2>/dev/null; then
        continue
    fi
    sed "${SED_INPLACE[@]}" \
        -e "s|${OLD_MODULE}|${NEW_MODULE}|g" \
        -e "s|\\b${OLD_BINARY}\\b|${NEW_BINARY}|g" \
        "$file"
done

if [[ -d "cmd/${OLD_BINARY}" && "${OLD_BINARY}" != "${NEW_BINARY}" ]]; then
    echo "==> moving cmd/${OLD_BINARY} → cmd/${NEW_BINARY}"
    git mv "cmd/${OLD_BINARY}" "cmd/${NEW_BINARY}"
fi

echo "==> go mod tidy"
go mod tidy

echo
echo "Done. Review the diff with 'git diff', then commit when satisfied:"
echo "  git add -A && git commit -m 'chore(setup): rename template to ${NEW_BINARY}'"
