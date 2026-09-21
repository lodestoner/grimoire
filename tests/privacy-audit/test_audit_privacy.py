#!/usr/bin/env python3
import subprocess
import sys
import unittest
import tempfile
from pathlib import Path


ROOT = Path(__file__).resolve().parents[2]
SCANNER = ROOT / "scripts" / "audit-privacy.py"
FIXTURES = Path(__file__).parent / "fixtures"


class PrivacyAuditTests(unittest.TestCase):
    def run_audit(self, fixture: str) -> subprocess.CompletedProcess[str]:
        return subprocess.run(
            [sys.executable, str(SCANNER), "--path", str(FIXTURES / fixture)],
            cwd=ROOT,
            capture_output=True,
            text=True,
        )

    def test_rejects_private_defaults_without_echoing_values(self) -> None:
        result = self.run_audit("reject")
        self.assertEqual(1, result.returncode)
        self.assertIn("private-network-address", result.stdout)
        self.assertIn("absolute-user-path", result.stdout)
        self.assertIn("credential-default", result.stdout)
        self.assertIn("private-host-default", result.stdout)
        self.assertIn("credential-header", result.stdout)
        fixture_text = (FIXTURES / "reject" / "private-defaults.ini").read_text()
        for line in fixture_text.splitlines():
            if "=" in line or ":" in line:
                value = line.split("=", 1)[-1].split(": ", 1)[-1]
                if value:
                    self.assertNotIn(value, result.stdout)

    def test_managed_utf16_private_string_is_rejected(self) -> None:
        # A leaked managed UTF-16 user string must not evade the staging audit.
        with tempfile.TemporaryDirectory() as directory:
            path = Path(directory) / "Grimoire.dll"
            value = "https://" + ".".join(["192", "168", "77", "88"]) + "/synthetic"
            path.write_bytes(b"MZ\x00" + value.encode("utf-16-le") + b"\x00\x00")
            result = subprocess.run([sys.executable, str(SCANNER), "--path", str(path)],
                                    capture_output=True, text=True)
            self.assertEqual(1, result.returncode, result.stdout + result.stderr)
            self.assertIn("private-network-address", result.stdout)
            self.assertNotIn(value, result.stdout)

    def test_allows_empty_optional_config_localhost_and_public_attribution(self) -> None:
        result = self.run_audit("pass")
        self.assertEqual(0, result.returncode, result.stdout + result.stderr)
        self.assertEqual("PASS: no privacy findings in the scanned inputs.\n", result.stdout)


if __name__ == "__main__":
    unittest.main()
