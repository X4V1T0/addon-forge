#!/usr/bin/env bash
# Publishes AddonForge.Web into ./dist for the Dockerfile to package.
# -r linux-musl-x64 matters even though this isn't self-contained: it makes
# `dotnet publish` fetch the musl-linked SQLite native library that the
# Alpine-based runtime image needs (glibc builds won't load there).
set -euo pipefail

CONFIGURATION="${1:-Release}"
ROOT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
DIST_PATH="$ROOT_DIR/dist"

rm -rf "$DIST_PATH"

dotnet publish "$ROOT_DIR/src/AddonForge.Web" \
    -c "$CONFIGURATION" \
    -r linux-musl-x64 \
    --self-contained false \
    -o "$DIST_PATH"

echo "Published to $DIST_PATH. Run 'docker build .' to package the addon image, or bump config.yaml's version first."
