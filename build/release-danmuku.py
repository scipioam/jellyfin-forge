"""Validate Danmuku release inputs and upload tested assets to a GitHub draft."""
import argparse
import hashlib
import json
import os
from pathlib import Path
import re
import subprocess
import tempfile
import xml.etree.ElementTree as ET
import zipfile

ROOT = Path(__file__).resolve().parents[1]


def validate_tag(root, tag):
    match = re.fullmatch(r"danmuku-v((?:0|[1-9][0-9]*)\.(?:0|[1-9][0-9]*)\.(?:0|[1-9][0-9]*))", tag)
    if not match:
        raise ValueError("Expected a Danmuku tag such as danmuku-v0.0.1")
    project = ET.parse(root / "src/Jellyfin.Plugin.Danmuku/Jellyfin.Plugin.Danmuku.csproj")
    version = match[1]
    if project.findtext(".//Version") != version:
        raise ValueError("Tag version does not match Danmuku Version")
    if project.findtext(".//AssemblyVersion") != version + ".0":
        raise ValueError("Danmuku AssemblyVersion must equal Version + .0")
    return version, project.findtext(".//PluginGuid")


def validate_package(root, tag, artifacts):
    version, guid = validate_tag(root, tag)
    archive = artifacts / f"jellyfin-plugin-danmuku-{version}.zip"
    checksum = archive.with_suffix(".zip.sha256")
    with archive.open("rb") as source:
        digest = hashlib.file_digest(source, "sha256").hexdigest()
    if checksum.read_text().strip() != f"{digest}  {archive.name}":
        raise ValueError("Package SHA-256 or checksum filename does not match")
    with zipfile.ZipFile(archive) as package:
        if package.testzip() is not None:
            raise ValueError("Package ZIP integrity check failed")
        manifest = json.loads(package.read("meta.json"))
        if (manifest.get("name"), manifest.get("version"), manifest.get("guid")) != (
            "Danmuku", version + ".0", guid
        ):
            raise ValueError("Package identity does not match the tagged project")
        if "Jellyfin.Plugin.Danmuku.dll" not in package.namelist():
            raise ValueError("Danmuku assembly is missing")
        if any(Path(name).name == "Jellyfin.Plugin.AgentBridge.dll" for name in package.namelist()):
            raise ValueError("Danmuku release must not contain AgentBridge")
    return version, archive, checksum


def gh(*args, check=True):
    return subprocess.run(["gh", *args], check=check, capture_output=True, text=True)


def find_release(repository, tag):
    # The by-tag REST endpoint is documented for published releases. Listing
    # releases with write access also includes drafts; paginate to find older ones.
    result = gh("api", "--paginate", "--slurp",
                f"repos/{repository}/releases?per_page=100", check=False)
    if result.returncode:
        raise RuntimeError(f"Unable to inspect releases; no write attempted: {result.stderr.strip()}")
    matches = [release for page in json.loads(result.stdout) for release in page
               if release.get("tag_name") == tag]
    if len(matches) > 1:
        raise ValueError("Multiple releases use the requested tag")
    return matches[0] if matches else None


def upload_draft(root, tag, artifacts, repository):
    # Validate everything locally before granting any release write operation.
    version, archive, checksum = validate_package(root, tag, artifacts)
    if not re.fullmatch(r"[A-Za-z0-9_.-]+/[A-Za-z0-9_.-]+", repository):
        raise ValueError("Expected repository in owner/name form")
    release = find_release(repository, tag)
    if release is not None:
        if release.get("draft") is not True or release.get("tag_name") != tag:
            raise ValueError("Refusing to modify an already published or mismatched release")
        # Recover an interrupted upload. Only these two generated assets are replaced;
        # preserve edited release notes and unrelated attachments.
        gh("release", "upload", tag, str(archive), str(checksum),
           "--repo", repository, "--clobber")
    else:
        with tempfile.TemporaryDirectory() as directory:
            notes = Path(directory) / "notes.md"
            run_id = os.environ.get("GITHUB_RUN_ID", "")
            run_link = f"https://github.com/{repository}/actions/runs/{run_id}" if run_id.isdigit() else ""
            notes.write_text(
                f"Danmuku {version}\n\n"
                "适用于 Jellyfin Server 12.1 / .NET 10。\n\n"
                "附件为完整常规 CI 验证使用的同一份 ZIP 和 SHA-256 校验文件。"
                "外部私有样本、按需性能长测及人工平台验收不包含在此发布门禁中。\n\n"
                "这是自动生成的草稿，请确认说明和附件后手动发布。\n\n"
                + (f"验证记录：{run_link}\n\n" if run_link else "")
                + f"安装说明：https://github.com/{repository}/blob/{tag}/deploy/README.md\n"
            )
            gh("release", "create", tag, str(archive), str(checksum),
               "--repo", repository, "--verify-tag", "--draft",
               "--title", f"Danmuku {version}", "--notes-file", str(notes))
    # A missing upload or an unexpected publication must never be reported as success.
    release = find_release(repository, tag)
    if release is None or release.get("draft") is not True or release.get("tag_name") != tag:
        raise ValueError("Expected the release to remain a draft")
    assets = {asset["name"]: asset for asset in release.get("assets", [])}
    for path in (archive, checksum):
        if path.name not in assets or assets[path.name].get("size") != path.stat().st_size:
            raise ValueError(f"Release asset missing or incomplete: {path.name}")
    print(release["html_url"])


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("command", choices=("validate", "draft"))
    parser.add_argument("--tag", required=True)
    parser.add_argument("--artifacts", type=Path, default=ROOT / "artifacts")
    parser.add_argument("--repository")
    args = parser.parse_args()
    if args.command == "validate":
        version, _ = validate_tag(ROOT, args.tag)
        print(f"Validated Danmuku {version}")
    else:
        if not args.repository:
            parser.error("draft requires --repository")
        upload_draft(ROOT, args.tag, args.artifacts, args.repository)


if __name__ == "__main__":
    main()
