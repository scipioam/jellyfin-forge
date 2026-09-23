#!/usr/bin/env bash
set -euo pipefail
source "$(dirname -- "$0")/common.sh"
[[ $# == 1 ]] || { echo 'Usage: uninstall-dev.sh danmuku|agentbridge' >&2; exit 2; }
select_plugin "$1"
compose_dev stop jellyfin
if [[ "$PLUGIN_KEY" == danmuku && -f "$FORGE_ROOT/deploy/docker/dev/web-entry/current/index.html" ]]; then
    python3 "$FORGE_ROOT/build/web-entry.py" remove --output "$FORGE_ROOT/deploy/docker/dev/web-entry"
fi
rm -rf -- "$FORGE_ROOT/deploy/docker/dev/config/plugins/$PLUGIN_NAME"
compose_dev up -d --force-recreate jellyfin
