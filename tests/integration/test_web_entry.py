import importlib.util
import tempfile
import unittest
from pathlib import Path

spec = importlib.util.spec_from_file_location(
    "entry", Path(__file__).resolve().parents[2] / "build/web-entry.py"
)
entry = importlib.util.module_from_spec(spec)
spec.loader.exec_module(entry)


class EntryTests(unittest.TestCase):
    def test_root_and_base_are_same_origin_and_exactly_one_marker(self):
        original = (
            b'<html><head><script src="main.js"></script></head><body></body></html>'
        )
        for base in ["", "/jellyfin"]:
            generated = entry.insert(original, base)
            self.assertEqual(1, generated.count(entry.MARKER.encode()))
            self.assertIn(
                ('src="' + base + '/Danmuku/Web/Bootstrap.js"').encode(), generated
            )
            with self.assertRaises(ValueError):
                entry.insert(generated, base)

    def test_unknown_structure_is_rejected(self):
        for original in [
            b"unknown",
            b"<html><head></head></html>",
            b"<html><head><script></script></head></head>",
        ]:
            with self.assertRaises(ValueError):
                entry.insert(original, "")

    def test_generation_is_atomic_and_old_generations_remain_for_rollback(self):
        with tempfile.TemporaryDirectory() as directory:
            output = Path(directory)
            old = b"old"
            new = b"new"
            entry.publish(
                output,
                old,
                new,
                {
                    "originalSha256": entry.digest(old),
                    "generatedSha256": entry.digest(new),
                },
            )
            first = (output / "current").resolve()
            self.assertEqual(entry.verify(output)["generatedSha256"], entry.digest(new))
            entry.publish(
                output,
                new,
                b"upgrade",
                {
                    "originalSha256": entry.digest(new),
                    "generatedSha256": entry.digest(b"upgrade"),
                },
            )
            self.assertEqual((first / "index.html").read_bytes(), new)
            self.assertEqual((output / "current/index.html").read_bytes(), b"upgrade")
            (output / "current/index.html").write_bytes(b"customized")
            with self.assertRaises(ValueError):
                entry.verify(output)
            self.assertEqual(
                (output / "current/index.html").read_bytes(), b"customized"
            )


if __name__ == "__main__":
    unittest.main()
