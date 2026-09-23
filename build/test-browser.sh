#!/usr/bin/env bash
set -euo pipefail
source "$(dirname -- "$0")/common.sh"
export DOTNET_PROCESSOR_COUNT="${DOTNET_PROCESSOR_COUNT:-1}" MSBUILDDISABLENODEREUSE=1 UseSharedCompilation=false
milestone=m1
browser_args=()
while [[ $# -gt 0 ]]; do
    case "$1" in
        --milestone)
            [[ $# -ge 2 && ( "$2" == m1 || "$2" == m2 ) ]] || { echo "--milestone requires m1 or m2" >&2; exit 2; }
            milestone="$2"
            shift 2
            ;;
        *) browser_args+=("$1"); shift ;;
    esac
done
node --test "$FORGE_ROOT/tests/browser/danmuku-layout.test.cjs"
"$FORGE_ROOT/build/package.sh" danmuku
python3 "$FORGE_ROOT/tests/integration/$milestone.py" --browser "${browser_args[@]}"
