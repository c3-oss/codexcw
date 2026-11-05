#!/usr/bin/env bash
set -euo pipefail
cd "$(dirname "$0")/.."
# Keep the native pnpm independent of globally installed tools and Corepack.
version="$(node -p 'require("./package.json").packageManager.replace(/^pnpm@/, "")')"
prefix="$PWD/.devbox/pnpm"
if [ ! -x "$prefix/bin/pnpm" ] || [ "$("$prefix/bin/pnpm" --version)" != "$version" ]; then
  npm install --global --prefix "$prefix" --no-package-lock --no-audit --no-fund "pnpm@$version"
fi
