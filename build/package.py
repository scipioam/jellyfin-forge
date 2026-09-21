"""Package only plugin-owned files; Jellyfin supplies framework/server dependencies."""
import hashlib
import json
import os
from pathlib import Path
import sys
import xml.etree.ElementTree as ET
import zipfile

root = Path(__file__).resolve().parents[1]
key = sys.argv[1]
name = {"danmuku": "Danmuku", "agentbridge": "AgentBridge"}[key]
assembly = f"Jellyfin.Plugin.{name}"
project = root / "src" / assembly
build_dir = project / "bin/Release/net10.0"
xml = ET.parse(project / f"{assembly}.csproj")
version = xml.findtext(".//Version")
plugin_guid = xml.findtext(".//PluginGuid")
manifest = {
    "category": "General",
    "guid": plugin_guid,
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

# Danmuku direct/transitive runtime packages, pinned and present in packages.lock.json.
DANMUKU_LOCKED_PACKAGES = {
    "Microsoft.Data.Sqlite": "10.0.12",
    "Microsoft.Data.Sqlite.Core": "10.0.12",
    "SQLitePCLRaw.bundle_e_sqlite3": "2.1.12",
    "SQLitePCLRaw.core": "2.1.12",
    "SQLitePCLRaw.lib.e_sqlite3": "2.1.12",
    "SQLitePCLRaw.provider.e_sqlite3": "2.1.12",
}
# assembly name -> (package id, package version, path inside package)
DANMUKU_MANAGED = {
    "Microsoft.Data.Sqlite.dll": ("Microsoft.Data.Sqlite.Core", "10.0.12", "lib/net8.0/Microsoft.Data.Sqlite.dll"),
    "SQLitePCLRaw.core.dll": ("SQLitePCLRaw.core", "2.1.12", "lib/netstandard2.0/SQLitePCLRaw.core.dll"),
    "SQLitePCLRaw.batteries_v2.dll": ("SQLitePCLRaw.bundle_e_sqlite3", "2.1.12", "lib/netstandard2.0/SQLitePCLRaw.batteries_v2.dll"),
    "SQLitePCLRaw.provider.e_sqlite3.dll": ("SQLitePCLRaw.provider.e_sqlite3", "2.1.12", "lib/net6.0/SQLitePCLRaw.provider.e_sqlite3.dll"),
}
# rid -> package relative native asset; RID directories are kept intact on purpose.
DANMUKU_NATIVE = {
    "linux-x64": "runtimes/linux-x64/native/libe_sqlite3.so",
    "linux-arm64": "runtimes/linux-arm64/native/libe_sqlite3.so",
}
DANMUKU_LICENSES = {
    "Microsoft.Data.Sqlite.Core": ("MIT", "https://licenses.nuget.org/MIT"),
    "SQLitePCLRaw.bundle_e_sqlite3": ("Apache-2.0", "https://licenses.nuget.org/Apache-2.0"),
    "SQLitePCLRaw.core": ("Apache-2.0", "https://licenses.nuget.org/Apache-2.0"),
    "SQLitePCLRaw.lib.e_sqlite3": ("Apache-2.0", "https://licenses.nuget.org/Apache-2.0"),
    "SQLitePCLRaw.provider.e_sqlite3": ("Apache-2.0", "https://licenses.nuget.org/Apache-2.0"),
}
FORBIDDEN_ARCHIVE_NAMES = (
    "Jellyfin.Controller.dll",
    "Jellyfin.Model.dll",
    "Jellyfin.Plugin.AgentBridge.dll",
    f"Jellyfin.Plugin.{name}.runtimeconfig.json",
)


def sha256(path):
    return hashlib.sha256(path.read_bytes()).hexdigest()


def nuget_package_dir(package, package_version):
    cache = Path(os.environ.get("NUGET_PACKAGES") or Path.home() / ".nuget/packages")
    directory = cache / package.lower() / package_version
    if not directory.is_dir():
        raise FileNotFoundError(f"NuGet package not found in cache: {directory}")
    return directory


def verify_locked_versions():
    locked = json.loads((project / "packages.lock.json").read_text())["dependencies"]["net10.0"]
    for package, expected in DANMUKU_LOCKED_PACKAGES.items():
        resolved = locked.get(package, {}).get("resolved")
        if resolved != expected:
            raise ValueError(f"packages.lock.json mismatch for {package}: {resolved} != {expected}")


def build_danmuku_dependency_manifest():
    managed = []
    native = []
    for entry_name, (package, package_version, package_path) in sorted(DANMUKU_MANAGED.items()):
        source = nuget_package_dir(package, package_version) / package_path
        managed.append({
            "assembly": entry_name,
            "sha256": sha256(source),
            "package": package,
            "packageVersion": package_version,
            "packagePath": package_path,
            "license": {"id": DANMUKU_LICENSES[package][0], "url": DANMUKU_LICENSES[package][1]},
        })
    for rid, package_path in sorted(DANMUKU_NATIVE.items()):
        source = nuget_package_dir("SQLitePCLRaw.lib.e_sqlite3", "2.1.12") / package_path
        native.append({
            "rid": rid,
            "archivePath": package_path,
            "sha256": sha256(source),
            "package": "SQLitePCLRaw.lib.e_sqlite3",
            "packageVersion": "2.1.12",
            "packagePath": package_path,
            "license": {"id": DANMUKU_LICENSES["SQLitePCLRaw.lib.e_sqlite3"][0],
                        "url": DANMUKU_LICENSES["SQLitePCLRaw.lib.e_sqlite3"][1]},
        })
    return managed, native


def validate_danmuku_archive(archive_path):
    """Assert the archive has no cross-RID flattening and no forbidden payload."""
    with zipfile.ZipFile(archive_path) as archive:
        names = archive.namelist()
        basenames = [Path(n).name for n in names]
        expected_root = {
            f"{assembly}.dll",
            f"{assembly}.deps.json",
            "dependencies.json",
            "meta.json",
            "LICENSE",
            *DANMUKU_MANAGED.keys(),
        }
        roots = {Path(n).name for n in names if "/" not in n}
        if roots != expected_root:
            raise ValueError(f"unexpected root files: {sorted(roots ^ expected_root)}")
        expected_native = {f"{DANMUKU_NATIVE[rid]}" for rid in DANMUKU_NATIVE}
        actual_native = {n for n in names if n.startswith("runtimes/")}
        if actual_native != expected_native:
            raise ValueError(f"unexpected RID assets: {sorted(actual_native ^ expected_native)}")
        extra = set(names) - expected_root - actual_native
        if extra:
            raise ValueError(f"unexpected archive entries: {sorted(extra)}")
        flattened = [n for n in names if n in ("libe_sqlite3.so", "e_sqlite3.dll", "libe_sqlite3.dylib")]
        if flattened:
            raise ValueError(f"native assets must not be flattened into the package root: {flattened}")
        for forbidden in FORBIDDEN_ARCHIVE_NAMES:
            if forbidden in names:
                raise ValueError(f"forbidden file packaged: {forbidden}")
        if any(b.startswith(("System.", "Microsoft.AspNetCore.")) for b in basenames):
            raise ValueError("shared framework files must not be packaged")
        for managed_entry in DANMUKU_MANAGED:
            if managed_entry not in names:
                raise ValueError(f"missing managed dependency: {managed_entry}")
        native_hashes = {n: hashlib.sha256(archive.read(n)).hexdigest() for n in actual_native}
        if len(set(native_hashes.values())) != len(native_hashes):
            raise ValueError("native assets for different RIDs are identical (possible flattening overwrite)")
        deps = json.loads(archive.read(f"{assembly}.deps.json"))
        targets = deps["targets"][".NETCoreApp,Version=v10.0"]
        lib_entry = next((entry for lib, entry in targets.items()
                          if lib.split("/")[0] == "SQLitePCLRaw.lib.e_sqlite3"), None)
        if lib_entry is None:
            raise ValueError("deps.json has no SQLitePCLRaw.lib.e_sqlite3 target entry")
        runtime_targets = lib_entry.get("runtimeTargets", {})
        for rid, package_path in DANMUKU_NATIVE.items():
            target = runtime_targets.get(package_path)
            if target is None or target.get("rid") != rid or target.get("assetType") != "native":
                raise ValueError(f"deps.json does not map {package_path} as native asset for {rid}")
    return names


def package_agentbridge(archive):
    with zipfile.ZipFile(archive, "w", zipfile.ZIP_DEFLATED) as z:
        z.write(build_dir / f"{assembly}.dll", f"{assembly}.dll")
        z.writestr("meta.json", json.dumps(manifest, indent=2) + "\n")
        z.write(root / "LICENSE", "LICENSE")


def package_danmuku(archive):
    verify_locked_versions()
    deps_file = build_dir / f"{assembly}.deps.json"
    if not deps_file.is_file():
        raise FileNotFoundError(f"plugin deps.json missing (build first): {deps_file}")
    managed, native = build_danmuku_dependency_manifest()
    dependency_manifest = {
        "schemaVersion": 1,
        "plugin": {
            "key": key,
            "name": name,
            "version": version,
            "guid": plugin_guid,
            "targetAbi": manifest["targetAbi"],
        },
        "generatedFrom": {
            "lockFile": f"src/{assembly}/packages.lock.json",
            "depsFile": f"src/{assembly}/bin/Release/net10.0/{assembly}.deps.json",
        },
        "managed": managed,
        "native": native,
        "excluded": [
            "Jellyfin.Controller (ExcludeAssets=runtime, provided by server)",
            "Jellyfin.Model (ExcludeAssets=runtime, provided by server)",
            "Microsoft.AspNetCore.App shared framework",
            ".NET shared framework",
            "Jellyfin.Plugin.AgentBridge",
        ],
    }
    with zipfile.ZipFile(archive, "w", zipfile.ZIP_DEFLATED) as z:
        z.write(build_dir / f"{assembly}.dll", f"{assembly}.dll")
        z.write(deps_file, f"{assembly}.deps.json")
        for entry_name, (package, package_version, package_path) in sorted(DANMUKU_MANAGED.items()):
            z.write(nuget_package_dir(package, package_version) / package_path, entry_name)
        for rid, package_path in sorted(DANMUKU_NATIVE.items()):
            z.write(nuget_package_dir("SQLitePCLRaw.lib.e_sqlite3", "2.1.12") / package_path, package_path)
        z.writestr("dependencies.json", json.dumps(dependency_manifest, indent=2) + "\n")
        z.writestr("meta.json", json.dumps(manifest, indent=2) + "\n")
        z.write(root / "LICENSE", "LICENSE")
    validate_danmuku_archive(archive)


out = root / "artifacts"
out.mkdir(exist_ok=True)
archive = out / f"jellyfin-plugin-{key}-{version}.zip"
if key == "danmuku":
    package_danmuku(archive)
else:
    package_agentbridge(archive)
checksum = hashlib.sha256(archive.read_bytes()).hexdigest()
archive.with_suffix('.zip.sha256').write_text(f'{checksum}  {archive.name}\n')
print(archive)
