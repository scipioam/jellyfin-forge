#!/usr/bin/env bash
set -euo pipefail
FORGE_ROOT="$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")/.." && pwd)"
export DOTNET_CLI_TELEMETRY_OPTOUT=1
export DOTNET_NOLOGO=1
cd "$FORGE_ROOT"
select_plugin() {
    case "${1:-}" in
        danmuku) PLUGIN_NAME=Danmuku ;;
        agentbridge) PLUGIN_NAME=AgentBridge ;;
        *) echo 'Expected plugin: danmuku | agentbridge' >&2; exit 2 ;;
    esac
    PLUGIN_KEY="$1"
    PLUGIN_PROJECT="$FORGE_ROOT/src/Jellyfin.Plugin.$PLUGIN_NAME/Jellyfin.Plugin.$PLUGIN_NAME.csproj"
}
compose_dev() {
    JELLYFIN_FORGE_UID="${JELLYFIN_FORGE_UID:-$(id -u)}" \
    JELLYFIN_FORGE_GID="${JELLYFIN_FORGE_GID:-$(id -g)}" \
    docker compose -f "$FORGE_ROOT/deploy/docker/dev/compose.yaml" "$@"
}
