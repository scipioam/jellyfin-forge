#!/usr/bin/env bash
set -euo pipefail
source "$(dirname -- "$0")/common.sh"
if [[ $# == 0 ]]; then
    target="$FORGE_ROOT/jellyfin-forge.sln"
else
    [[ $# == 1 ]] || exit 2
    select_plugin "$1"
    target="$PLUGIN_PROJECT"
fi
dotnet restore "$target" --locked-mode
dotnet build "$target" -c Release --no-restore
