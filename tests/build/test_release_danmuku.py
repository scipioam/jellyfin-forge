"""Exercise release validation and write boundaries without contacting GitHub."""
import hashlib
import importlib.util
import json
from pathlib import Path
import subprocess
import tempfile
import unittest
from unittest.mock import patch
import zipfile

SPEC = importlib.util.spec_from_file_location(
    "release_danmuku", Path(__file__).resolve().parents[2] / "build/release-danmuku.py")
RELEASE = importlib.util.module_from_spec(SPEC)
SPEC.loader.exec_module(RELEASE)
TAG = "danmuku-v0.0.1"
REPO = "example/forge"


class ReleaseTests(unittest.TestCase):
    def setUp(self):
        self.directory = tempfile.TemporaryDirectory()
        self.addCleanup(self.directory.cleanup)
        self.root = Path(self.directory.name)
        self.project = self.root / "src/Jellyfin.Plugin.Danmuku/Jellyfin.Plugin.Danmuku.csproj"
        self.project.parent.mkdir(parents=True)
        self.project.write_text("<Project><PropertyGroup><Version>0.0.1</Version>"
                                "<AssemblyVersion>0.0.1.0</AssemblyVersion>"
                                "<PluginGuid>test-guid</PluginGuid></PropertyGroup></Project>")
        self.artifacts = self.root / "artifacts"
        self.artifacts.mkdir()
        self.archive = self.artifacts / "jellyfin-plugin-danmuku-0.0.1.zip"
        self.checksum = self.archive.with_suffix(".zip.sha256")
        self.package()

    def package(self, **changes):
        manifest = {"name": "Danmuku", "version": "0.0.1.0", "guid": "test-guid"}
        manifest.update(changes)
        with zipfile.ZipFile(self.archive, "w") as z:
            z.writestr("meta.json", json.dumps(manifest))
            z.writestr("Jellyfin.Plugin.Danmuku.dll", b"test assembly")
        self.write_checksum()

    def write_checksum(self):
        self.checksum.write_text(f"{hashlib.sha256(self.archive.read_bytes()).hexdigest()}  {self.archive.name}\n")

    def snapshot(self, draft=True, complete=True):
        return {"draft": draft, "tag_name": TAG, "html_url": "https://example.test/draft",
                "assets": [{"name": p.name, "size": p.stat().st_size}
                           for p in (self.archive, self.checksum)] if complete else []}

    @staticmethod
    def response(data=None, code=0, error=""):
        return subprocess.CompletedProcess([], code, json.dumps(data) if data else "", error)

    def upload(self):
        RELEASE.upload_draft(self.root, TAG, self.artifacts, REPO)

    def test_accepts_matching_tag_and_tested_package(self):
        version, archive, checksum = RELEASE.validate_package(self.root, TAG, self.artifacts)
        self.assertEqual((version, archive, checksum), ("0.0.1", self.archive, self.checksum))

    def test_rejects_other_plugin_malformed_and_mismatched_tags(self):
        for tag in ("agentbridge-v0.0.1", "v0.0.1", "danmuku-v0.0.2", "danmuku-v00.0.1",
                    "danmuku-v0.0.1-beta", "danmuku-v0.0.1/other", "danmuku-v0.0.1\n"):
            with self.subTest(tag=tag), self.assertRaises(ValueError):
                RELEASE.validate_tag(self.root, tag)

    def test_rejects_mismatched_assembly_version(self):
        self.project.write_text(self.project.read_text().replace("0.0.1.0", "0.2.0.0"))
        with self.assertRaises(ValueError):
            RELEASE.validate_tag(self.root, TAG)

    def test_rejects_tampering_before_contacting_github(self):
        self.archive.write_bytes(self.archive.read_bytes() + b"changed")
        with patch.object(RELEASE, "gh") as gh, self.assertRaises(ValueError):
            self.upload()
        gh.assert_not_called()

    def test_rejects_checksum_for_another_filename(self):
        self.checksum.write_text(self.checksum.read_text().replace(self.archive.name, "other.zip"))
        with self.assertRaises(ValueError):
            RELEASE.validate_package(self.root, TAG, self.artifacts)

    def test_rejects_wrong_package_identity(self):
        for changes in ({"version": "0.2.0.0"}, {"guid": "other-guid"}, {"name": "AgentBridge"}):
            self.package(**changes)
            with self.subTest(changes=changes), self.assertRaises(ValueError):
                RELEASE.validate_package(self.root, TAG, self.artifacts)

    def test_rejects_sibling_assembly(self):
        with zipfile.ZipFile(self.archive, "a") as z:
            z.writestr("Jellyfin.Plugin.AgentBridge.dll", b"wrong plugin")
        self.write_checksum()
        with self.assertRaises(ValueError):
            RELEASE.validate_package(self.root, TAG, self.artifacts)

    def test_rejects_missing_package(self):
        self.archive.unlink()
        with patch.object(RELEASE, "gh") as gh, self.assertRaises(FileNotFoundError):
            self.upload()
        gh.assert_not_called()

    def test_new_release_is_draft_and_requires_existing_tag(self):
        calls = []

        def fake(*args, **kwargs):
            calls.append(args)
            if len(calls) == 1:
                return self.response([[]])
            if args[:2] == ("release", "create"):
                self.assertIn("--draft", args)
                self.assertIn("--verify-tag", args)
                self.assertIn(str(self.archive), args)
                self.assertIn(str(self.checksum), args)
                notes = Path(args[args.index("--notes-file") + 1]).read_text()
                self.assertIn("Danmuku 0.0.1", notes)
                self.assertNotIn("AgentBridge", notes)
                return self.response()
            return self.response([[self.snapshot()]])

        with patch.object(RELEASE, "gh", side_effect=fake):
            self.upload()
        self.assertEqual(len(calls), 3)

    def test_retry_repairs_draft_assets_without_editing_notes_or_publishing(self):
        replies = [self.response([[self.snapshot(complete=False)]]), self.response(), self.response([[self.snapshot()]])]
        with patch.object(RELEASE, "gh", side_effect=replies) as gh:
            self.upload()
        upload = gh.call_args_list[1].args
        self.assertEqual(upload[:3], ("release", "upload", TAG))
        self.assertEqual(upload[3:5], (str(self.archive), str(self.checksum)))
        self.assertIn("--clobber", upload)
        self.assertEqual(gh.call_count, 3)

    def test_published_release_is_never_modified(self):
        with patch.object(RELEASE, "gh", return_value=self.response([[self.snapshot(draft=False)]])) as gh:
            with self.assertRaises(ValueError):
                self.upload()
        self.assertEqual(gh.call_count, 1)

    def test_permission_failure_is_not_treated_as_missing_release(self):
        with patch.object(RELEASE, "gh", return_value=self.response(code=1, error="Forbidden (HTTP 403)")) as gh:
            with self.assertRaises(RuntimeError):
                self.upload()
        self.assertEqual(gh.call_count, 1)

    def test_incomplete_upload_is_not_reported_as_success(self):
        replies = [self.response([[self.snapshot()]]), self.response(), self.response([[self.snapshot(complete=False)]])]
        with patch.object(RELEASE, "gh", side_effect=replies), self.assertRaises(ValueError):
            self.upload()

    def test_failed_upload_is_propagated(self):
        replies = [self.response([[self.snapshot()]]), subprocess.CalledProcessError(1, ["gh", "release", "upload"])]
        with patch.object(RELEASE, "gh", side_effect=replies), self.assertRaises(subprocess.CalledProcessError):
            self.upload()

    def test_finds_draft_on_later_page(self):
        pages = [[{"tag_name": "other"}], [self.snapshot()]]
        with patch.object(RELEASE, "gh", return_value=self.response(pages)) as gh:
            self.assertEqual(RELEASE.find_release(REPO, TAG), self.snapshot())
        self.assertIn("--paginate", gh.call_args.args)
        self.assertIn("--slurp", gh.call_args.args)

    def test_ambiguous_tag_is_not_modified(self):
        pages = [[self.snapshot(), self.snapshot()]]
        with patch.object(RELEASE, "gh", return_value=self.response(pages)) as gh:
            with self.assertRaises(ValueError):
                self.upload()
        self.assertEqual(gh.call_count, 1)


if __name__ == "__main__":
    unittest.main()
