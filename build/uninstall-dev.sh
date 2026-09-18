#!/usr/bin/env bash
set -euo pipefail
source "$(dirname -- "$0")/common.sh"
[[ $# == 1 ]] || { echo 'Usage: uninstall-dev.sh danmuku|agentbridge' >&2; exit 2; }
select_plugin "$1"
compose_dev stop jellyfin
rm -rf -- "$FORGE_ROOT/deploy/docker/dev/config/plugins/$PLUGIN_NAME"
compose_dev up -d jellyfin
