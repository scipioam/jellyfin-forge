"""Package only plugin-owned files; Jellyfin supplies framework/server dependencies."""
import hashlib
import json
from pathlib import Path
import sys
import xml.etree.ElementTree as ET
import zipfile

root = Path(__file__).resolve().parents[1]
key = sys.argv[1]
name = {"danmuku": "Danmuku", "agentbridge": "AgentBridge"}[key]
assembly = f"Jellyfin.Plugin.{name}"
project = root / "src" / assembly
xml = ET.parse(project / f"{assembly}.csproj")
version = xml.findtext(".//Version")
manifest = {
    "category": "General",
    "guid": xml.findtext(".//PluginGuid"),
    "name": name,
    "description": xml.findtext(".//Description"),
    "owner": "scipioam",
    "version": xml.findtext(".//AssemblyVersion"),
    "targetAbi": "12.1.0.0",
    "status": "Active",
    "autoUpdate": False,
}
if manifest['version'] != version + '.0':
    raise ValueError('AssemblyVersion must equal Version + .0')
out = root / "artifacts"
out.mkdir(exist_ok=True)
archive = out / f"jellyfin-plugin-{key}-{version}.zip"
with zipfile.ZipFile(archive, "w", zipfile.ZIP_DEFLATED) as z:
    z.write(project / "bin/Release/net10.0" / f"{assembly}.dll", f"{assembly}.dll")
    z.writestr("meta.json", json.dumps(manifest, indent=2) + "\n")
    z.write(root / "LICENSE", "LICENSE")
checksum = hashlib.sha256(archive.read_bytes()).hexdigest()
archive.with_suffix('.zip.sha256').write_text(f'{checksum}  {archive.name}\n')
print(archive)
