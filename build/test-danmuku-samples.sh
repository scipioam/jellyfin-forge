#!/usr/bin/env bash
set -euo pipefail
source "$(dirname -- "$0")/common.sh"
: "${DANMUKU_SAMPLE_DIR:?Set DANMUKU_SAMPLE_DIR to the external fixtures directory}"
export DOTNET_PROCESSOR_COUNT="${DOTNET_PROCESSOR_COUNT:-1}" MSBUILDDISABLENODEREUSE=1 UseSharedCompilation=false
"$FORGE_ROOT/build/test.sh" danmuku
