#!/usr/bin/env bash
set -euo pipefail
source "$(dirname -- "$0")/common.sh"
[[ $# == 1 ]] || { echo 'Usage: install-dev.sh danmuku|agentbridge' >&2; exit 2; }
select_plugin "$1"
"$FORGE_ROOT/build/package.sh" "$PLUGIN_KEY"
# Stop only this repository's dev service before replacing its plugin files.
compose_dev stop jellyfin
python3 - "$FORGE_ROOT" "$PLUGIN_NAME" "$PLUGIN_KEY" <<'PY'
import pathlib, sys, xml.etree.ElementTree as ET, zipfile
root, name, key = pathlib.Path(sys.argv[1]), sys.argv[2], sys.argv[3]
version = ET.parse(root / f'src/Jellyfin.Plugin.{name}/Jellyfin.Plugin.{name}.csproj').findtext('.//Version')
target = root / 'deploy/docker/dev/config/plugins' / name
target.mkdir(parents=True, exist_ok=True)
with zipfile.ZipFile(root / f'artifacts/jellyfin-plugin-{key}-{version}.zip') as archive:
    archive.extractall(target)
print(f'Installed {name} into {target}')
PY
compose_dev up -d jellyfin
