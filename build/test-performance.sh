#!/usr/bin/env bash
set -euo pipefail
source "$(dirname -- "$0")/common.sh"
export DOTNET_PROCESSOR_COUNT="${DOTNET_PROCESSOR_COUNT:-1}" MSBUILDDISABLENODEREUSE=1 UseSharedCompilation=false
"$FORGE_ROOT/build/package.sh" danmuku
python3 "$FORGE_ROOT/tests/integration/m1.py" --performance "$@"
