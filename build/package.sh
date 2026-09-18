#!/usr/bin/env bash
set -euo pipefail
source "$(dirname -- "$0")/common.sh"
[[ $# == 1 ]] || { echo 'Usage: package.sh danmuku|agentbridge' >&2; exit 2; }
select_plugin "$1"
"$FORGE_ROOT/build/build.sh" "$PLUGIN_KEY"
python3 "$FORGE_ROOT/build/package.py" "$PLUGIN_KEY"
