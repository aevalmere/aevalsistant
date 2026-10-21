#!/bin/bash
# Builds and runs the Mac checks: the shared logic from tests/ ported to Swift, plus the card,
# glyph, and Settings page. With a folder argument, PNG renders land there.
set -euo pipefail
cd "$(dirname "$0")/.."
mkdir -p mac/build
swiftc -swift-version 5 -module-name AevalsistantTests \
  mac/Sources/Shared/*.swift mac/Sources/App/*.swift mac/Tests/*.swift -o mac/build/Tests
AEVAL_ASSETS="$PWD/assets" mac/build/Tests "$@"
