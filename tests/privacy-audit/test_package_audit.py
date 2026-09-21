"""Package regressions use tiny synthetic files, never real user/installer data."""
import hashlib
import importlib.util
import json
from pathlib import Path
import tempfile
import unittest
import zipfile

ROOT = Path(__file__).resolve().parents[2]
spec = importlib.util.spec_from_file_location("package_audit", ROOT / "scripts/audit-package.py")
package = importlib.util.module_from_spec(spec)
spec.loader.exec_module(package)


class PackageAuditTests(unittest.TestCase):
    def prepare(self, root):
        candidate, staging = root / "candidate", root / "staging"
        candidate.mkdir(); staging.mkdir()
        (staging / "Grimoire.dll").write_bytes(b"synthetic managed assembly")
        names = {"README.txt", "LICENSE", "THIRD-PARTY-NOTICES.txt", "licenses/sources.json",
                 "licenses/Microsoft.NETCore.App.Runtime.win-x64-LICENSE.txt",
                 "licenses/Microsoft.NETCore.App.Runtime.win-x64-THIRD-PARTY-NOTICES.txt",
                 "licenses/Microsoft.WindowsDesktop.App.Runtime.win-x64-LICENSE.txt"}
        names |= {"licenses/" + x["file"] for x in json.loads((ROOT / "packaging/licenses/sources.json").read_text())}
        for name in names:
            path = candidate / name
            path.parent.mkdir(parents=True, exist_ok=True)
            path.write_text("synthetic text", encoding="utf-8")
        (candidate / "Grimoire-2.2.0-rc.1-portable.exe").write_bytes(b"synthetic executable")
        (candidate / "Grimoire-Setup-2.2.0-rc.1.exe").write_bytes(b"synthetic installer")
        with zipfile.ZipFile(candidate / "Grimoire-2.2.0-rc.1-portable.zip", "w") as archive:
            archive.writestr("Grimoire.exe", b"synthetic executable")
            for name in names: archive.write(candidate / name, name)
        self.hashes(candidate)
        return candidate, staging

    def hashes(self, candidate):
        files = [{"name": p.relative_to(candidate).as_posix(), "sha256": hashlib.sha256(p.read_bytes()).hexdigest()}
                 for p in candidate.rglob("*") if p.is_file() and p.name not in {"build-manifest.json", "SHA256SUMS.txt"}]
        (candidate / "build-manifest.json").write_text(json.dumps({"version": "2.2.0-rc.1", "files": files}))
        (candidate / "SHA256SUMS.txt").write_text("\n".join(
            hashlib.sha256(p.read_bytes()).hexdigest() + "  " + p.relative_to(candidate).as_posix()
            for p in candidate.rglob("*") if p.is_file() and p.name != "SHA256SUMS.txt") + "\n")

    def test_accepts_only_matching_extracted_portable_content(self):
        with tempfile.TemporaryDirectory() as temporary:
            candidate, staging = self.prepare(Path(temporary))
            package.check(candidate, staging, "2.2.0-rc.1")
            (candidate / "Grimoire-2.2.0-rc.1-portable.exe").write_bytes(b"different executable")
            self.hashes(candidate)
            with self.assertRaisesRegex(ValueError, "differs"):
                package.check(candidate, staging, "2.2.0-rc.1")

    def test_rejects_unexpected_source_in_artifact(self):
        with tempfile.TemporaryDirectory() as temporary:
            candidate, staging = self.prepare(Path(temporary))
            (candidate / "private-planning.md").write_text("synthetic non-shippable file")
            self.hashes(candidate)
            with self.assertRaisesRegex(ValueError, "allowlist"):
                package.check(candidate, staging, "2.2.0-rc.1")

    def test_rejects_incomplete_manifest(self):
        with tempfile.TemporaryDirectory() as temporary:
            candidate, staging = self.prepare(Path(temporary))
            (candidate / "build-manifest.json").write_text(json.dumps({"version": "2.2.0-rc.1", "files": []}))
            with self.assertRaisesRegex(ValueError, "Manifest"):
                package.check(candidate, staging, "2.2.0-rc.1")

    def test_rejects_modified_checksums(self):
        with tempfile.TemporaryDirectory() as temporary:
            candidate, staging = self.prepare(Path(temporary))
            (candidate / "SHA256SUMS.txt").write_text("0" * 64 + "  LICENSE\n")
            with self.assertRaisesRegex(ValueError, "Checksum"):
                package.check(candidate, staging, "2.2.0-rc.1")


if __name__ == "__main__":
    unittest.main()
