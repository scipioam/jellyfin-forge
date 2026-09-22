#!/usr/bin/env python3
"""M1 real HTTP fixture. Own resources only, fixed port, serial browsers, restore prior dev state."""
import argparse
import contextlib
import importlib.util
import json
import os
from pathlib import Path
import secrets
import signal
import sys
import socket
import subprocess
import time
import urllib.error
import urllib.parse
import urllib.request
import uuid
import xml.etree.ElementTree as ET
import zipfile

ROOT = Path(__file__).resolve().parents[2]
PLUGIN = "6f79690c-c1d0-4738-b241-09aaa2c570e7"
AUTH = (
    'MediaBrowser Client="M1", Device="Test", DeviceId="forge-m1-http", Version="0.2.0"'
)


def run(*args, **kwargs):
    return subprocess.run(args, check=True, text=True, **kwargs)


def check(value, message):
    if not value:
        raise AssertionError(message)


class Instance:
    def __init__(self, prefix=""):
        self.prefix = prefix
        self.base = "http://127.0.0.1:18096" + prefix
        self.project = "jellyfin-forge-m1-" + uuid.uuid4().hex[:10]
        self.data = ROOT / "artifacts/m1/integration" / self.project
        self.command = ["docker", "compose", "-f", str(self.data / "compose.json")]
        self.restore = []
        self.results = []

    def request(
        self, path, body=None, token=None, method=None, expected=200, raw=False
    ):
        headers = {"Authorization": AUTH + (', Token="' + token + '"' if token else "")}
        if body is not None and not isinstance(body, bytes):
            body = json.dumps(body).encode()
            headers["Content-Type"] = "application/json"
        elif isinstance(body, bytes):
            headers["Content-Type"] = "application/octet-stream"
        request = urllib.request.Request(
            self.base + path, data=body, headers=headers, method=method
        )
        try:
            with urllib.request.urlopen(request, timeout=120) as response:
                status, content = response.status, response.read()
        except urllib.error.HTTPError as error:
            status, content = error.code, error.read()
        check(
            status == expected,
            f'{method or "GET"} {path}: expected {expected}, got {status}: {content[:250]!r}',
        )
        if raw:
            return content
        try:
            return json.loads(content) if content else None
        except json.JSONDecodeError:
            return content.decode()

    def wait(self, predicate, seconds=240):
        deadline = time.monotonic() + seconds
        while time.monotonic() < deadline:
            try:
                result = predicate()
                if result:
                    return result
            except (OSError, AssertionError):
                pass
            time.sleep(1)
        raise TimeoutError("Fixture condition timed out")

    def __enter__(self):
        try:
            self.restore = run(
                "docker",
                "ps",
                "-q",
                "--filter",
                "label=com.docker.compose.project=jellyfin-forge-dev",
                capture_output=True,
            ).stdout.split()
            if self.restore:
                run("docker", "stop", *self.restore, capture_output=True)
            with socket.socket() as probe:
                probe.setsockopt(socket.SOL_SOCKET, socket.SO_REUSEADDR, 1)
                probe.bind(("127.0.0.1", 18096))
            for part in ["config", "cache", "media"]:
                (self.data / part).mkdir(parents=True)
            # Test-only non-root service identity; persisted config/cache remain outside Git.
            service_uid = os.getuid() or 1000
            for part in ["config", "cache"]:
                os.chown(self.data / part, service_uid, os.getgid())
            version = ET.parse(
                ROOT / "src/Jellyfin.Plugin.Danmuku/Jellyfin.Plugin.Danmuku.csproj"
            ).findtext(".//Version")
            with zipfile.ZipFile(
                ROOT / f"artifacts/jellyfin-plugin-danmuku-{version}.zip"
            ) as z:
                z.extractall(self.data / "config/plugins/Danmuku")
            media = ROOT / "artifacts/m1/synthetic.mp4"
            if not media.exists():
                clip = ROOT / "artifacts/m1/clip.mp4"
                run(
                    "ffmpeg",
                    "-y",
                    "-hide_banner",
                    "-loglevel",
                    "error",
                    "-f",
                    "lavfi",
                    "-i",
                    "testsrc2=size=640x360:rate=24",
                    "-t",
                    "10",
                    "-c:v",
                    "libx264",
                    "-threads",
                    "1",
                    "-preset",
                    "ultrafast",
                    "-crf",
                    "30",
                    "-pix_fmt",
                    "yuv420p",
                    str(clip),
                )
                run(
                    "ffmpeg",
                    "-y",
                    "-hide_banner",
                    "-loglevel",
                    "error",
                    "-stream_loop",
                    "-1",
                    "-i",
                    str(clip),
                    "-t",
                    "3700",
                    "-c",
                    "copy",
                    str(media),
                )
            (self.data / "media/Synthetic.mp4").symlink_to(
                media
            )  # replaced with direct read-only bind below
            entry = self.data / "entry"
            run(
                "python3",
                str(ROOT / "build/web-entry.py"),
                "prepare",
                "--image",
                "jellyfin/jellyfin:12.1",
                "--web-path",
                "/jellyfin/jellyfin-web",
                "--base-url",
                self.prefix or "/",
                "--output",
                str(entry),
                capture_output=True,
            )
            if self.prefix:
                (self.data / "config/config").mkdir(exist_ok=True)
                (self.data / "config/config/network.xml").write_text(
                    "<NetworkConfiguration><BaseUrl>"
                    + self.prefix
                    + "</BaseUrl><EnableIPv6>false</EnableIPv6></NetworkConfiguration>"
                )
                os.chown(self.data / "config/config", service_uid, os.getgid())
                os.chown(
                    self.data / "config/config/network.xml", service_uid, os.getgid()
                )
            service = {
                "image": "jellyfin/jellyfin:12.1",
                "user": f"{service_uid}:{os.getgid()}",
                "cpus": 1,
                "mem_limit": "1g",
                "environment": {"DOTNET_PROCESSOR_COUNT": "1"},
                "ports": ["127.0.0.1:18096:8096"],
                "volumes": [
                    f"{self.data}/config:/config",
                    f"{self.data}/cache:/cache",
                    f"{media}:/media/Synthetic.mp4:ro",
                    f"{entry}/current/index.html:/jellyfin/jellyfin-web/index.html:ro",
                ],
            }
            (self.data / "compose.json").write_text(
                json.dumps({"name": self.project, "services": {"jellyfin": service}})
            )
            run(*self.command, "up", "-d", capture_output=True)
            self.wait(lambda: self.request("/System/Info/Public"))
            cfg = self.wait(lambda: self.request("/Startup/Configuration"))
            self.request(
                "/Startup/Configuration",
                {
                    "ServerName": "M1 isolated",
                    "UICulture": "en-US",
                    "MetadataCountryCode": "US",
                    "PreferredMetadataLanguage": "en",
                },
                expected=204,
            )
            self.wait(lambda: self.request("/Startup/User"))
            self.password = secrets.token_urlsafe(24)
            self.request(
                "/Startup/User",
                {"Name": "m1-admin", "Password": self.password},
                expected=204,
            )
            self.request(
                "/Startup/RemoteAccess", {"EnableRemoteAccess": False}, expected=204
            )
            self.request("/Startup/Complete", {}, expected=204)
            login = self.request(
                "/Users/AuthenticateByName",
                {"Username": "m1-admin", "Pw": self.password},
            )
            self.admin = login["AccessToken"]
            self.admin_id = login["User"]["Id"]
            # Startup can already be scanning an empty root. A refresh requested
            # during that scan is coalesced and can miss the newly added folder.
            self.wait(lambda: not any(t.get("Key") == "RefreshLibrary" and t.get("State") in ("Running", "Cancelling")
                                     for t in self.request("/ScheduledTasks", token=self.admin)))
            self.request(
                "/Library/VirtualFolders?name=M1&collectionType=movies&paths=%2Fmedia&refreshLibrary=true",
                {},
                self.admin,
                expected=204,
            )
            self.request("/Library/Refresh", {}, self.admin, expected=204)
            self.item = self.wait(
                lambda: next(
                    iter(
                        self.request(
                            "/Items?Recursive=true&IncludeItemTypes=Movie",
                            token=self.admin,
                        )["Items"]
                    ),
                    None,
                )
            )["Id"]
            u = self.request(
                "/Users/New",
                {"Name": "m1-viewer", "Password": self.password},
                self.admin,
            )
            self.viewer_id = u["Id"]
            self.viewer = self.request(
                "/Users/AuthenticateByName",
                {"Username": "m1-viewer", "Pw": self.password},
            )["AccessToken"]
            restricted = self.request(
                "/Users/New",
                {"Name": "m1-restricted", "Password": self.password},
                self.admin,
            )
            policy = restricted["Policy"]
            policy["EnableAllFolders"] = False
            policy["EnabledFolders"] = []
            self.request(
                "/Users/" + restricted["Id"] + "/Policy",
                policy,
                self.admin,
                expected=204,
            )
            self.restricted = self.request(
                "/Users/AuthenticateByName",
                {"Username": "m1-restricted", "Pw": self.password},
            )["AccessToken"]
            config = self.request(
                "/Plugins/" + PLUGIN + "/Configuration", token=self.admin
            )
            config["WebEnabled"] = config["EnableWebSupport"] = True
            self.request(
                "/Plugins/" + PLUGIN + "/Configuration",
                config,
                self.admin,
                expected=204,
            )
            credentials = {
                "base": self.base,
                "item": self.item,
                "admin": {
                    "username": "m1-admin",
                    "password": self.password,
                    "token": self.admin,
                },
                "viewer": {
                    "username": "m1-viewer",
                    "password": self.password,
                    "token": self.viewer,
                },
            }
            (self.data / "credentials.json").write_text(json.dumps(credentials))
            (self.data / "credentials.json").chmod(0o600)
            return self
        except BaseException:
            self.__exit__(None, None, None)
            raise

    def __exit__(self, *_):
        try:
            if (self.data / "compose.json").exists():
                try:
                    log = run(*self.command, "logs", "--no-color", capture_output=True)
                    (self.data / "server.log").write_text(log.stdout + log.stderr)
                finally:
                    run(*self.command, "down", "--remove-orphans", capture_output=True)
        finally:
            if self.restore:
                run("docker", "start", *self.restore, capture_output=True)

    def deployment_tests(self):
        entry = self.data / "entry"
        tool = str(ROOT / "build/web-entry.py")
        check(
            run(
                *self.command, "exec", "-T", "jellyfin", "id", "-u", capture_output=True
            ).stdout.strip()
            != "0",
            "server must be non-root",
        )
        run(
            "python3",
            tool,
            "verify",
            "--image",
            "jellyfin/jellyfin:12.1",
            "--base-url",
            self.prefix or "/",
            "--output",
            str(entry),
            capture_output=True,
        )
        before = (entry / "current/index.html").read_bytes()
        run(
            "python3",
            tool,
            "prepare",
            "--image",
            "jellyfin/jellyfin:12.1",
            "--web-path",
            "/jellyfin/jellyfin-web",
            "--base-url",
            self.prefix or "/",
            "--output",
            str(entry),
            capture_output=True,
        )
        check(
            (entry / "current/index.html").read_bytes() == before,
            "duplicate prepare changed entry",
        )
        (entry / "current/index.html").write_bytes(before + b"<!-- custom -->")
        rejected = subprocess.run(
            [
                "python3",
                tool,
                "prepare",
                "--image",
                "jellyfin/jellyfin:12.1",
                "--web-path",
                "/jellyfin/jellyfin-web",
                "--base-url",
                self.prefix or "/",
                "--output",
                str(entry),
            ],
            capture_output=True,
        )
        check(
            rejected.returncode != 0
            and (entry / "current/index.html").read_bytes()
            == before + b"<!-- custom -->",
            "unknown modification overwritten",
        )
        (entry / "current/index.html").write_bytes(before)
        config = self.request("/Plugins/" + PLUGIN + "/Configuration", token=self.admin)
        upgrade = self.data / "upgrade"
        upgrade.mkdir()
        (upgrade / "index.html").write_bytes(
            (entry / "current/original.html")
            .read_bytes()
            .replace(b"</head>", b'<meta name="m1-upgrade-probe" content="1"></head>')
        )
        (upgrade / "Dockerfile").write_text(
            "FROM jellyfin/jellyfin:12.1\nCOPY index.html /jellyfin/jellyfin-web/index.html\n"
        )
        image = self.project + "-entry-upgrade"
        run("docker", "build", "-t", image, str(upgrade), capture_output=True)
        compose_path = self.data / "compose.json"
        compose = json.loads(compose_path.read_text())
        try:
            run(
                "python3",
                tool,
                "prepare",
                "--image",
                image,
                "--web-path",
                "/jellyfin/jellyfin-web",
                "--base-url",
                self.prefix or "/",
                "--output",
                str(entry),
                capture_output=True,
            )
            compose["services"]["jellyfin"]["image"] = image
            compose_path.write_text(json.dumps(compose))
            run(*self.command, "up", "-d", "--force-recreate", capture_output=True)
            self.wait(lambda: self.request("/Plugins", token=self.admin))
            upgraded = self.request("/web/index.html", raw=True)
            check(
                b"m1-upgrade-probe" in upgraded
                and upgraded.count(b"data-danmuku-entry") == 1,
                "new image entry not applied",
            )
            check(
                self.request("/Plugins/" + PLUGIN + "/Configuration", token=self.admin)
                == config,
                "upgrade lost configuration",
            )
        finally:
            run(
                "python3",
                tool,
                "prepare",
                "--image",
                "jellyfin/jellyfin:12.1",
                "--web-path",
                "/jellyfin/jellyfin-web",
                "--base-url",
                self.prefix or "/",
                "--output",
                str(entry),
                capture_output=True,
            )
            compose["services"]["jellyfin"]["image"] = "jellyfin/jellyfin:12.1"
            compose_path.write_text(json.dumps(compose))
            run(*self.command, "up", "-d", "--force-recreate", capture_output=True)
            self.wait(lambda: self.request("/Plugins", token=self.admin))
            run("docker", "image", "rm", image, capture_output=True)
        check(
            b"m1-upgrade-probe" not in self.request("/web/index.html", raw=True),
            "entry rollback failed",
        )
        bindings = self.request(
            "/Danmuku/Media/" + self.item + "/Bindings", token=self.admin
        )
        run("python3", tool, "remove", "--output", str(entry), capture_output=True)
        run(*self.command, "up", "-d", "--force-recreate", capture_output=True)
        self.wait(lambda: self.request("/Plugins", token=self.admin))
        check(
            b"data-danmuku-entry" not in self.request("/web/index.html", raw=True),
            "marker remained after revocation and recreation",
        )
        check(
            self.request("/Plugins/" + PLUGIN + "/Configuration", token=self.admin)
            == config,
            "configuration lost after entry revoke",
        )
        check(
            self.request("/Danmuku/Media/" + self.item + "/Bindings", token=self.admin)[
                "FileIds"
            ]
            == bindings["FileIds"],
            "bindings lost after entry revoke",
        )
        run(*self.command, "stop", "jellyfin", capture_output=True)
        program = self.data / "config/plugins/Danmuku"
        backup = self.data / "program-backup"
        program.rename(backup)
        try:
            run(*self.command, "up", "-d", "--force-recreate", capture_output=True)
            self.wait(lambda: self.request("/Plugins", token=self.admin))
            self.request("/Danmuku/Health", token=self.admin, expected=404)
            check(
                b"data-danmuku-entry" not in self.request("/web/index.html", raw=True),
                "uninstalled entry remained",
            )
            check(
                (self.data / "config/data/Danmuku/danmuku.db").is_file(),
                "uninstall deleted business data",
            )
        finally:
            run(*self.command, "stop", "jellyfin", capture_output=True)
            backup.rename(program)
        run(
            "python3",
            tool,
            "prepare",
            "--image",
            "jellyfin/jellyfin:12.1",
            "--web-path",
            "/jellyfin/jellyfin-web",
            "--base-url",
            self.prefix or "/",
            "--output",
            str(entry),
            capture_output=True,
        )
        run(*self.command, "up", "-d", "--force-recreate", capture_output=True)
        self.wait(lambda: self.request("/Plugins", token=self.admin))
        check(
            self.request("/web/index.html", raw=True).count(b"data-danmuku-entry") == 1,
            "entry not restored exactly once",
        )
        check(
            self.request("/Plugins/" + PLUGIN + "/Configuration", token=self.admin)
            == config,
            "reinstall lost configuration",
        )
        check(
            self.request("/Danmuku/Media/" + self.item + "/Bindings", token=self.admin)[
                "FileIds"
            ]
            == bindings["FileIds"],
            "reinstall lost bindings",
        )
        self.results.append(
            "non-root, entry idempotency, custom modification protection, revoke/recreate/re-enable preserve config and bindings"
        )
        (self.data / "deployment-results.json").write_text(
            json.dumps(self.results, indent=2)
        )

    def batch(self, names, operation="append", replace=None):
        binding = self.request(
            "/Danmuku/Media/" + self.item + "/Bindings", token=self.admin
        )
        return self.request(
            "/Danmuku/ImportBatches",
            {
                "BatchId": uuid.uuid4().hex,
                "MediaId": self.item,
                "ExpectedVersion": binding["Version"],
                "FileNames": names,
                "Operation": operation,
                "ReplaceFileId": replace,
            },
            self.admin,
        )

    def task(self, batch, slot, content):
        return self.request(
            "/Danmuku/ImportBatches/" + batch["BatchId"] + "/Files/" + str(slot),
            content,
            self.admin,
            expected=202,
        )

    def terminal(self, id):
        return self.wait(
            lambda: (
                lambda task: (
                    task
                    if task["Status"]
                    not in ["Queued", "Processing", "Parsing", "Saving"]
                    else None
                )
            )(self.request("/Danmuku/Imports/" + id, token=self.admin))
        )

    def upload_transport_tests(self):
        def slot(batch):
            return self.request(
                "/Danmuku/Imports?batchId=" + batch["BatchId"], token=self.admin
            )["batch"]["Slots"][0]

        def send(connection, batch, body, length):
            route = (
                self.prefix + "/Danmuku/ImportBatches/" + batch["BatchId"] + "/Files/0"
            )
            headers = (
                "POST "
                + route
                + " HTTP/1.1\r\nHost: 127.0.0.1:18096\r\nAuthorization: "
                + AUTH
                + ', Token="'
                + self.admin
                + '"\r\nContent-Type: application/octet-stream\r\nContent-Length: '
                + str(length)
                + "\r\nConnection: close\r\n\r\n"
            ).encode()
            connection.sendall(headers + body)

        interrupted = self.batch(["interrupted.json"])
        with socket.create_connection(("127.0.0.1", 18096), timeout=30) as connection:
            send(connection, interrupted, b"[{", 4096)
            self.wait(lambda: slot(interrupted)["Status"] == "Receiving")
        self.wait(lambda: slot(interrupted)["Status"] == "Failed")
        check(slot(interrupted)["TaskId"] is None, "partial upload created a task")
        lost = self.batch(["lost-response.json"])
        body = b'[{"progress":0,"content":"accepted before response was lost"}]'
        with socket.create_connection(("127.0.0.1", 18096), timeout=30) as connection:
            send(connection, lost, body, len(body))
            accepted = self.wait(lambda: slot(lost) if slot(lost)["TaskId"] else None)
            # Never read the upload response. Observe acceptance via an independent request.
        replay = self.task(lost, 0, body)
        check(
            replay["TaskId"] == accepted["TaskId"],
            "lost response replay created another task",
        )
        check(
            self.terminal(replay["TaskId"])["Status"] == "Completed",
            "accepted upload did not survive disconnection",
        )
        self.results.append(
            "real HTTP partial upload failure and unread accepted response replay passed"
        )

    def independent_import_tests(self):
        route = "/Danmuku/ImportBatches"
        binding_route = "/Danmuku/Media/" + self.item + "/Bindings"
        before = self.request(binding_route, token=self.admin)
        body = {"BatchId": uuid.uuid4().hex, "Operation": "import", "FileNames": ["independent.json"]}
        self.request(route, body, expected=401)
        self.request(route, body, self.viewer, expected=403)
        for extra in [{"MediaId": self.item}, {"ExpectedVersion": 0}, {"ReplaceFileId": "file"}]:
            self.request(route, body | extra, self.admin, expected=422)
        batch = self.request(route, body, self.admin)
        task = self.task(batch, 0, b'[{"progress":1000,"content":"independent HTTP"}]')
        result = self.terminal(task["TaskId"])
        check(result["Status"] == "Completed" and result["Operation"] == "import", "independent import completion")
        check(result.get("MediaId") is None, "independent import media must be null")
        check(self.request(binding_route, token=self.admin) == before, "independent import changed media")
        replay = self.request(route, body, self.admin)
        check(replay["Slots"][0]["FileId"] == result["FileId"], "independent retry lost file identity")
        self.request(route, body | {"Operation":"append", "MediaId":self.item, "ExpectedVersion":before["Version"]}, self.admin, expected=409)
        self.request("/Danmuku/Imports/" + task["TaskId"] + "/Resume", {"ExpectedVersion":0}, self.admin, expected=422)
        self.request("/Danmuku/Files/" + result["FileId"], token=self.admin, method="DELETE", expected=200)
        self.results.append("independent import permissions, invalid parameters, idempotency, no media mutation and unsupported resume")

    def http_tests(self):
        self.independent_import_tests()
        for route in [
            "/Danmuku/Files",
            "/Danmuku/Files/missing/Original",
            "/Danmuku/Files/missing/Bindings",
            "/Danmuku/Media",
            "/Danmuku/Imports",
            "/Danmuku/WebIntegration",
            "/Danmuku/BindingChecks/missing",
        ]:
            self.request(route, expected=401)
            self.request(route, token=self.viewer, expected=403)
        for route, method in [
            ("/Danmuku/Files/missing", "DELETE"),
            ("/Danmuku/Media/" + self.item + "/Bindings", "PUT"),
            ("/Danmuku/BindingChecks", "POST"),
            ("/Danmuku/ImportBatches", "POST"),
            ("/Danmuku/ImportBatches/missing/Files/0", "POST"),
            ("/Danmuku/Imports/missing/ConfirmSkip", "POST"),
            ("/Danmuku/Imports/missing/Cancel", "POST"),
            ("/Danmuku/Imports/missing/Resume", "POST"),
            ("/Danmuku/ImportBatches/missing/ConfirmSkip", "POST"),
            ("/Danmuku/ImportBatches/missing/Cancel", "POST"),
        ]:
            self.request(route, {}, method=method, expected=401)
            self.request(route, {}, self.viewer, method=method, expected=403)
        playback = "/Danmuku/Playback/" + self.item + "?renderVersion=m1-density-v1&playbackId=" + uuid.uuid4().hex
        self.request(playback, expected=401)
        self.request(playback, token=self.restricted, expected=403)
        self.request("/Danmuku/Files?limit=101", token=self.admin, expected=422)
        self.request("/Danmuku/Web/BootstrapState")
        self.request("/Danmuku/Web/Assets/missing", expected=404)
        config = self.request("/Plugins/" + PLUGIN + "/Configuration", token=self.admin)
        bad = {**config, "LongLoadLimit": 20001}
        # Jellyfin maps plugin validation ArgumentException to a bad-request response.
        self.request(
            "/Plugins/" + PLUGIN + "/Configuration", bad, self.admin, expected=400
        )
        check(
            self.request("/Plugins/" + PLUGIN + "/Configuration", token=self.admin)
            == config,
            "rejected configuration changed old values",
        )
        for field in ["LowRenderLimit", "MediumRenderLimit", "HighRenderLimit", "OverlapRenderLimit"]:
            missing = dict(config)
            missing.pop(field)
            self.request("/Plugins/" + PLUGIN + "/Configuration", missing, self.admin, expected=400)
            check(self.request("/Plugins/" + PLUGIN + "/Configuration", token=self.admin) == config, "missing render limit changed config")
        self.request(playback.replace("renderVersion=m1-density-v1&", ""), token=self.admin, expected=409)
        self.request(playback.replace("m1-density-v1", "future"), token=self.admin, expected=409)
        batch = self.batch(["abnormal.json", "pending.xml"])
        task = self.task(
            batch,
            0,
            json.dumps(
                [{"progress": 0, "content": "normal"}]
                + [{"content": "missing time"}] * 101
            ).encode(),
        )
        final = self.terminal(task["TaskId"])
        check(final["Status"] == "AwaitingConfirmation", "expected confirmation")
        result = self.request(
            "/Danmuku/Imports?batchId=" + batch["BatchId"], token=self.admin
        )
        check(len(result["batch"]["Slots"]) == 2, "pending slot omitted")
        check(
            "TaskId" in result["batch"]["Slots"][1]
            and result["batch"]["Slots"][1]["TaskId"] is None,
            "pending taskId must be explicitly null",
        )
        errors = self.request(
            "/Danmuku/Imports/" + task["TaskId"] + "/Errors?startIndex=50",
            token=self.admin,
        )
        check(
            errors["TotalCount"] == 101 and len(errors["Items"]) == 50,
            "error pagination",
        )
        self.request(
            "/Danmuku/ImportBatches/" + batch["BatchId"] + "/ConfirmSkip",
            {},
            self.admin,
        )
        final = self.terminal(task["TaskId"])
        check(
            final["Status"] == "Completed" and final["SkippedComments"] == 101,
            "batch confirmation missed errors",
        )
        self.request(
            "/Danmuku/ImportBatches/" + batch["BatchId"] + "/Cancel", {}, self.admin
        )
        body = self.request(playback, token=self.viewer)
        check(body["selectedCount"] == 1, "playback count")
        self.request(playback, token=self.admin, expected=409)
        self.request("/Danmuku/Files/" + final["FileId"], token=self.admin)
        self.request(
            "/Danmuku/Files/" + final["FileId"] + "/Original",
            token=self.admin,
            raw=True,
        )
        self.request(
            "/Danmuku/Files/" + final["FileId"],
            token=self.admin,
            method="DELETE",
            expected=409,
        )
        config["EnableWebSupport"] = config["WebEnabled"] = False
        self.request(
            "/Plugins/" + PLUGIN + "/Configuration", config, self.admin, expected=204
        )
        check(
            self.request(playback, token=self.viewer)["status"] == "Disabled",
            "disabled cache bypass",
        )
        check(self.request(playback.replace("renderVersion=m1-density-v1&", ""), token=self.viewer)["status"] == "Disabled", "disabled must precede render contract mismatch")
        self.request(playback.replace("renderVersion=m1-density-v1&", ""), token=self.restricted, expected=403)
        config["EnableWebSupport"] = config["WebEnabled"] = True
        self.request(
            "/Plugins/" + PLUGIN + "/Configuration", config, self.admin, expected=204
        )
        check(
            self.request(playback, token=self.viewer) == body,
            "retry changed collection",
        )
        replacement = self.batch(["replacement.json"], "replace", final["FileId"])
        replacement_task = self.task(
            replacement,
            0,
            b'[{"progress":0,"content":"replacement"},{"content":"bad"}]',
        )
        check(
            self.terminal(replacement_task["TaskId"])["Status"]
            == "AwaitingConfirmation",
            "replacement confirmation",
        )
        current = self.request(
            "/Danmuku/Media/" + self.item + "/Bindings", token=self.admin
        )
        self.request(
            "/Danmuku/Media/" + self.item + "/Bindings",
            {
                "ExpectedVersion": current["Version"],
                "FileIds": current["FileIds"],
                "ActiveFileId": current["ActiveFileId"],
            },
            self.admin,
            method="PUT",
        )
        self.request(
            "/Danmuku/Imports/" + replacement_task["TaskId"] + "/ConfirmSkip",
            {},
            self.admin,
        )
        check(
            self.terminal(replacement_task["TaskId"])["Status"]
            == "AwaitingConflictResolution",
            "expected version conflict",
        )
        refreshed = self.request(
            "/Danmuku/Media/" + self.item + "/Bindings", token=self.admin
        )
        self.request(
            "/Danmuku/Imports/" + replacement_task["TaskId"] + "/Resume",
            {"ExpectedVersion": refreshed["Version"]},
            self.admin,
        )
        replaced = self.terminal(replacement_task["TaskId"])
        check(
            replaced["Status"] == "Completed" and replaced["ResultCode"] == "Replaced",
            "replacement was not atomic",
        )
        self.request(
            "/Danmuku/Files/" + final["FileId"], token=self.admin, method="DELETE"
        )
        check(
            self.request(playback, token=self.viewer) == body,
            "deletion changed frozen response",
        )
        viewer = self.request("/Users/" + self.viewer_id, token=self.admin)
        policy = viewer["Policy"]
        policy["EnableMediaPlayback"] = False
        self.request(
            "/Users/" + self.viewer_id + "/Policy", policy, self.admin, expected=204
        )
        self.request(playback, token=self.viewer, expected=403)
        policy["EnableMediaPlayback"] = True
        self.request(
            "/Users/" + self.viewer_id + "/Policy", policy, self.admin, expected=204
        )
        self.request("/Sessions/Logout", {}, self.viewer, expected=204)
        self.request(playback, token=self.viewer, expected=401)
        self.viewer = self.request(
            "/Users/AuthenticateByName", {"Username": "m1-viewer", "Pw": self.password}
        )["AccessToken"]
        restart_playback = (
            "/Danmuku/Playback/" + self.item + "?renderVersion=m1-density-v1&playbackId=" + uuid.uuid4().hex
        )
        self.request(restart_playback, token=self.admin)
        run(*self.command, "restart", "jellyfin", capture_output=True)
        self.wait(lambda: self.request("/Plugins", token=self.admin))
        # The original owner token was revoked; metadata still cannot be reused by its new login session.
        self.request(playback, token=self.viewer, expected=409)
        self.request(restart_playback, token=self.admin, expected=410)
        self.upload_transport_tests()
        checkjob = self.request("/Danmuku/BindingChecks", {}, self.admin)["taskId"]
        self.wait(
            lambda: self.request(
                "/Danmuku/BindingChecks/" + checkjob, token=self.admin
            )["Status"]
            == "Completed"
        )
        self.results.append(
            "HTTP admin/viewer/restricted/anonymous, config rejection, import confirmation, pagination, retry and disable passed"
        )
        (self.data / "http-results.json").write_text(json.dumps(self.results, indent=2))


def main():
    signal.signal(signal.SIGTERM, lambda *_: sys.exit(128 + signal.SIGTERM))
    parser = argparse.ArgumentParser()
    parser.add_argument("--base-url", default="")
    parser.add_argument("--browser", action="store_true")
    parser.add_argument("--performance", action="store_true")
    parser.add_argument("--preflight", action="store_true")
    args = parser.parse_args()
    with Instance(args.base_url) as instance:
        print("Fixture:", instance.data, flush=True)
        instance.http_tests()
        print("HTTP passed", flush=True)
        instance.deployment_tests()
        print("Deployment passed", flush=True)
        if args.browser or args.performance:
            for engine in ["chromium"] if args.performance else ["chromium", "firefox"]:
                run(
                    "node",
                    str(ROOT / "tests/browser/m1.cjs"),
                    str(instance.data),
                    engine,
                    *(["--performance"] if args.performance else []),
                    *(["--preflight"] if args.preflight else []),
                )
        print("PASS", instance.project, flush=True)


if __name__ == "__main__":
    main()
