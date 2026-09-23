#!/usr/bin/env python3
"""M2 isolated real HTTP checks; fixed project port and M1 lifecycle restoration."""
import argparse
import hashlib
import json
from pathlib import Path
import signal
import sys
import uuid

from m1 import Instance, ROOT, check, run


def lower_keys(value):
    if isinstance(value, dict):
        return {k.lower(): lower_keys(v) for k, v in value.items()}
    if isinstance(value, list):
        return [lower_keys(v) for v in value]
    return value


class M2Instance(Instance):
    def __init__(self, prefix=""):
        super().__init__(prefix)
        self.project = "jellyfin-forge-m2-" + uuid.uuid4().hex[:10]
        self.data = ROOT / "artifacts/m2/integration" / self.project
        self.command = ["docker", "compose", "-f", str(self.data / "compose.json")]
        self.additional_media_names = ["M2-Second.mp4"]

    def __enter__(self):
        super().__enter__()
        try:
            second = self.wait(lambda: next((item for item in self.request(
                "/Items?Recursive=true&IncludeItemTypes=Movie", token=self.admin)["Items"]
                if item["Id"] != self.item), None))
            self.second = second["Id"]
            credentials_path = self.data / "credentials.json"
            credentials = json.loads(credentials_path.read_text())
            credentials["secondItem"] = self.second
            credentials_path.write_text(json.dumps(credentials))
            return self
        except BaseException:
            self.__exit__(None, None, None)
            raise

    def api(self, path, body=None, method=None, expected=200, token=True):
        return lower_keys(self.request(path, body, self.admin if token is True else token,
                                       method=method, expected=expected))

    def plan_http_tests(self):
        media = "/Danmuku/Media/" + self.item
        plans = media + "/CombinePlans"
        for token, status in [(None, 401), (self.viewer, 403)]:
            self.api(plans, expected=status, token=token)
            self.api(plans, {}, expected=status, token=token)
        batch = self.batch(["m2-source.json"])
        task = self.task(batch, 0, json.dumps([
            {"progress": 600000, "content": "start"},
            {"progress": 720000, "content": "12:00"},
            {"progress": 1199999, "content": "end-1"},
            {"progress": 1200000, "content": "end"},
        ]).encode())
        imported = self.terminal(task["TaskId"])
        check(imported["Status"] == "Completed", "source import")
        file = imported["FileId"]
        segment = {"fileId": file, "sourceStartMs": 600000, "sourceEndMs": 1200000, "targetStartMs": 1200000}
        preview = self.api(plans + "/Preview", {"segments": [segment]})
        check(preview["totalcount"] == 3, "half-open range count")
        check(preview["segments"][0]["firsttargetms"] == 1200000, "mapped range")
        state = self.api(media + "/Bindings")
        pid = uuid.uuid4().hex
        plan = self.api(plans, {"planId": pid, "name": "combine-1", "segments": [segment],
                                 "activate": True, "expectedMediaVersion": state["version"]})
        check(plan["version"] == 1, "initial plan version")
        active = self.api(media + "/Bindings")
        check(active["activeplanid"] == pid, "plan activation")
        self.api(media + "/Bindings", {"expectedVersion": active["version"], "fileIds": [], "activeFileId": None}, "PUT", 409)
        self.api(media + "/Bindings", {"expectedVersion": active["version"], "fileIds": [], "activeFileId": None, "selectionIntent": "preserve"}, "PUT", 422)
        state = self.api(media + "/Bindings", {"expectedVersion": active["version"], "fileIds": [], "selectionIntent": "preserve"}, "PUT")
        check(state["activeplanid"] == pid, "preserve plan on unbind")
        playback = "/Danmuku/Playback/" + self.item + "?renderVersion=m2-speed-v1&playbackId=" + uuid.uuid4().hex
        self.api(playback, token=self.restricted, expected=403)
        frozen = self.api(playback, token=self.viewer)
        check(frozen["sourcekind"] == "plan" and frozen["planversion"] == 1, "playback plan metadata")
        check([i["timems"] for i in frozen["items"]] == [1200000, 1320000, 1799999], "mapped playback range")
        references = self.api("/Danmuku/Files/" + file + "/References")
        check(references["totalcount"] == 1 and references["items"][0]["kind"] == "plan", "plan source reference")
        self.api("/Danmuku/Files/" + file, method="DELETE", expected=409)
        self.api(plans + "/" + pid, {"name": "stale", "segments": [segment], "expectedPlanVersion": 0}, "PUT", 409)
        self.api(plans, {"planId": uuid.uuid4().hex, "name": "combine-1", "segments": [segment]}, expected=409)
        self.api("/Danmuku/Media/" + uuid.uuid4().hex + "/CombinePlans/" + pid, expected=404)
        self.api(plans + "/Preview", {"segments": [segment] * 51}, expected=422)
        self.api(plans, {"planId": uuid.uuid4().hex, "name": "x" * (129 * 1024), "segments": [segment]}, expected=413)
        self.api(plans + "/" + pid + "/Delete", {"expectedPlanVersion": 1, "expectedMediaVersion": state["version"]}, expected=422)
        self.api(plans + "/" + pid + "/Delete", {"expectedPlanVersion": 1, "expectedMediaVersion": state["version"], "replacement": {"kind": "disabled"}}, expected=204)
        check(self.api(playback, token=self.viewer) == frozen, "retry survives plan deletion")
        check(not self.api(plans)["items"], "deleted plan")
        check(self.api("/Danmuku/Files/" + file)["status"] == "Published", "source retained")
        self.results.append("P3 administrator lifecycle, range preview, legacy and preserve bindings, references, conflicts, body cap and permissions")


def main():
    signal.signal(signal.SIGTERM, lambda *_: sys.exit(128 + signal.SIGTERM))
    parser = argparse.ArgumentParser()
    parser.add_argument("--base-url", choices=["", "/jellyfin"], default="")
    parser.add_argument("--browser", action="store_true")
    args = parser.parse_args()
    instance = M2Instance(args.base_url)
    package = ROOT / "artifacts/jellyfin-plugin-danmuku-0.0.2.zip"
    result = {"packageSha256": hashlib.sha256(package.read_bytes()).hexdigest(), "baseUrl": args.base_url,
              "scope": "M2 HTTP and selected browser workflows; not the full acceptance matrix", "status": "failed"}
    try:
        with instance:
            print("Fixture:", instance.data, flush=True)
            instance.plan_http_tests()
            if args.browser:
                # The HTTP lifecycle leaves the source unbound; explicitly bind it for speed checks.
                media = "/Danmuku/Media/" + instance.item
                state = instance.api(media + "/Bindings")
                file = instance.api("/Danmuku/Files")["items"][0]["fileid"]
                instance.api(media + "/Bindings", {"expectedVersion": state["version"], "fileIds": [file],
                    "selectionIntent": "set", "selection": {"kind": "file", "id": file}}, "PUT")
                for engine in ["chromium", "firefox"]:
                    run("node", str(ROOT / "tests/browser/m2.cjs"), str(instance.data), engine)
            result.update(status="passed", checks=instance.results)
            print("M2 HTTP and requested browser checks passed", flush=True)
    finally:
        instance.data.mkdir(parents=True, exist_ok=True)
        (instance.data / "http-results.json").write_text(json.dumps(result, indent=2))


if __name__ == "__main__":
    main()
