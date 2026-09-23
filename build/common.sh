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
    local compose_args=(-f "$FORGE_ROOT/deploy/docker/dev/compose.yaml")
    local entry_root="$FORGE_ROOT/deploy/docker/dev/web-entry"
    if [[ -f "$entry_root/current/index.html" ]]; then
        python3 "$FORGE_ROOT/build/web-entry.py" verify --image jellyfin/jellyfin:12.1 --output "$entry_root" >/dev/null
        export JELLYFIN_FORGE_WEB_ENTRY="$entry_root/current/index.html"
        JELLYFIN_FORGE_WEB_TARGET="$(python3 -c 'import json,sys; print(json.load(open(sys.argv[1]))["webPath"] + "/index.html")' "$entry_root/current/manifest.json")"
        export JELLYFIN_FORGE_WEB_TARGET
        compose_args+=(-f "$FORGE_ROOT/deploy/docker/dev/compose.web-entry.yaml")
    fi
    JELLYFIN_FORGE_UID="${JELLYFIN_FORGE_UID:-$(id -u)}" \
    JELLYFIN_FORGE_GID="${JELLYFIN_FORGE_GID:-$(id -g)}" \
    docker compose "${compose_args[@]}" "$@"
}
