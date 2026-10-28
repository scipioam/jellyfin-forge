#!/usr/bin/env bash
set -euo pipefail
source "$(dirname -- "$0")/common.sh"
export MSBUILDDISABLENODEREUSE=1 UseSharedCompilation=false
milestone=m1
performance_args=()
while [[ $# -gt 0 ]]; do
    case "$1" in
        --milestone)
            [[ $# -ge 2 && ( "$2" == m1 || "$2" == m2 ) ]] || { echo "--milestone requires m1 or m2" >&2; exit 2; }
            milestone="$2"; shift 2 ;;
        *) performance_args+=("$1"); shift ;;
    esac
done
"$FORGE_ROOT/build/package.sh" danmuku
python3 "$FORGE_ROOT/tests/integration/$milestone.py" --performance "${performance_args[@]}"
