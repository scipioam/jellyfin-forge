#!/usr/bin/env bash
set -euo pipefail
source "$(dirname -- "$0")/common.sh"
"$FORGE_ROOT/build/package.sh" danmuku
"$FORGE_ROOT/build/package.sh" agentbridge
python3 "$FORGE_ROOT/build/smoke-test.py"
