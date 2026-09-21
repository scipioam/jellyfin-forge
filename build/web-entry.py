#!/usr/bin/env python3
"""Generate/verify/revoke a versioned, read-only Jellyfin Web entry. Never deploy a service."""
import argparse
import fcntl
import hashlib
import html
import json
import os
from pathlib import Path
import re
import shutil
import subprocess
import tempfile
import uuid

MARKER = 'data-danmuku-entry="m1-v1"'


def digest(data):
    return hashlib.sha256(data).hexdigest()


def run(*args):
    return subprocess.run(args, check=True, capture_output=True).stdout


def verify(output):
    current = output / "current"
    if not current.exists():
        raise ValueError("No managed entry; run prepare first.")
    manifest = json.loads((current / "manifest.json").read_text())
    for name, key in [
        ("index.html", "generatedSha256"),
        ("original.html", "originalSha256"),
    ]:
        if digest((current / name).read_bytes()) != manifest[key]:
            raise ValueError(
                f"{name} was modified outside this tool; refusing to overwrite custom content."
            )
    return manifest


def insert(original, base):
    source = original.decode("utf-8")
    if (
        source.lower().count("</head>") != 1
        or "<html" not in source.lower()
        or "<script" not in source.lower()
        or "Danmuku/Web/" in source
    ):
        raise ValueError(
            "Unrecognized or already modified entry structure; original deployment retained."
        )
    marker = f'<script async {MARKER} src="{html.escape(base, quote=True)}/Danmuku/Web/Bootstrap.js"></script>'
    position = source.lower().index("</head>")
    return (source[:position] + marker + source[position:]).encode("utf-8")


def publish(output, original, generated, manifest):
    generation = output / ("generation-" + uuid.uuid4().hex)
    generation.mkdir()
    try:
        for name, data in [
            ("index.html", generated),
            ("original.html", original),
            ("manifest.json", (json.dumps(manifest, indent=2) + "\n").encode()),
        ]:
            with (generation / name).open("wb") as handle:
                handle.write(data)
                handle.flush()
                os.fsync(handle.fileno())
            (generation / name).chmod(0o644)
        link = output / ("next-" + uuid.uuid4().hex)
        link.symlink_to(generation.name, target_is_directory=True)
        os.replace(link, output / "current")
        # Old immutable generations are intentionally retained for explicit rollback.
    except BaseException:
        if (
            not (output / "current").is_symlink()
            or (output / "current").resolve() != generation
        ):
            shutil.rmtree(generation)
        raise


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("action", choices=["prepare", "verify", "remove"])
    parser.add_argument("--output", type=Path, required=True)
    parser.add_argument("--image")
    parser.add_argument("--web-path")
    parser.add_argument("--base-url")
    args = parser.parse_args()
    output = args.output.resolve()
    output.mkdir(parents=True, exist_ok=True)
    with (output / ".lock").open("a") as lock:
        fcntl.flock(lock, fcntl.LOCK_EX)
        previous = verify(output) if (output / "current").exists() else None
        if args.action == "verify":
            if previous is None:
                raise ValueError("No managed entry.")
            if (
                args.image
                and json.loads(run("docker", "image", "inspect", args.image))[0]["Id"]
                != previous["imageId"]
            ):
                raise ValueError(
                    "Image identity changed; prepare a fresh entry before deployment."
                )
            if (
                args.base_url is not None
                and args.base_url.rstrip("/") != previous["baseUrl"]
            ):
                raise ValueError("Base URL differs from generated entry.")
            if (
                args.web_path is not None
                and args.web_path.rstrip("/") != previous["webPath"]
            ):
                raise ValueError("Web path differs from generated entry.")
            print(json.dumps(previous, indent=2))
            return
        if args.action == "remove":
            if previous is None:
                raise ValueError("No managed entry to revoke.")
            original = (output / "current/original.html").read_bytes()
            publish(
                output,
                original,
                original,
                {**previous, "enabled": False, "generatedSha256": digest(original)},
            )
            print(
                "Entry revoked. Recreate the target container to apply the new read-only mount."
            )
            return
        if not args.image or not args.web_path or args.base_url is None:
            parser.error(
                "prepare requires --image, --web-path and --base-url (use / for root)."
            )
        base = args.base_url.rstrip("/")
        if base and not re.fullmatch(r"(?:/[A-Za-z0-9._~-]+)+", base):
            raise ValueError(
                "Base URL must be an absolute same-origin path, without query or fragment."
            )
        if not args.web_path.startswith("/") or ".." in Path(args.web_path).parts:
            raise ValueError("Web path must be an absolute container directory.")
        image = json.loads(run("docker", "image", "inspect", args.image))[0]
        container = (
            run(
                "docker",
                "create",
                "--name",
                "jellyfin-forge-entry-" + uuid.uuid4().hex[:12],
                args.image,
            )
            .decode()
            .strip()
        )
        try:
            with tempfile.TemporaryDirectory(dir=output) as tmp:
                source = Path(tmp) / "index.html"
                run(
                    "docker",
                    "cp",
                    container + ":" + args.web_path.rstrip("/") + "/index.html",
                    str(source),
                )
                original = source.read_bytes()
        finally:
            run("docker", "rm", container)
        generated = insert(original, base)
        manifest = {
            "image": args.image,
            "imageId": image["Id"],
            "webPath": args.web_path.rstrip("/"),
            "baseUrl": base,
            "originalSha256": digest(original),
            "generatedSha256": digest(generated),
            "enabled": True,
            "marker": "m1-v1",
        }
        if previous != manifest:
            publish(output, original, generated, manifest)
        print(json.dumps(manifest, indent=2))
        print(
            "Mount output/current/index.html read-only at webPath/index.html and recreate the target container."
        )


if __name__ == "__main__":
    try:
        main()
    except (ValueError, OSError, subprocess.CalledProcessError) as error:
        raise SystemExit(str(error)) from error
