#!/usr/bin/env python3
"""Inspect allowlisted deliverables and extracted ZIP content, plus app-owned pre-bundle staging."""
import argparse
import hashlib
import importlib.util
import json
from pathlib import Path, PurePosixPath
import sys
import tempfile
import zipfile

ROOT = Path(__file__).resolve().parents[1]
spec = importlib.util.spec_from_file_location("privacy_audit", ROOT / "scripts/audit-privacy.py")
audit = importlib.util.module_from_spec(spec)
sys.modules[spec.name] = audit
spec.loader.exec_module(audit)


def check(candidate: Path, staging: Path, version: str, portable_only=False):
    names = {"README.txt", "LICENSE", "THIRD-PARTY-NOTICES.txt"}
    license_sources = json.loads((ROOT / "packaging/licenses/sources.json").read_text())
    licenses = {"licenses/" + x["file"] for x in license_sources} | {
        "licenses/sources.json", "licenses/Microsoft.NETCore.App.Runtime.win-x64-LICENSE.txt",
        "licenses/Microsoft.NETCore.App.Runtime.win-x64-THIRD-PARTY-NOTICES.txt",
        "licenses/Microsoft.WindowsDesktop.App.Runtime.win-x64-LICENSE.txt"}
    portable = f"Grimoire-{version}-portable.exe"
    archive = f"Grimoire-{version}-portable.zip"
    setup = f"Grimoire-Setup-{version}.exe"
    allowed = names | licenses | {portable, archive, setup, "build-manifest.json", "SHA256SUMS.txt"}
    if portable_only:
        allowed.remove(setup)
    actual = {p.relative_to(candidate).as_posix() for p in candidate.rglob("*") if p.is_file()}
    if actual != allowed:
        raise ValueError("Candidate file allowlist mismatch (missing or unexpected shipped paths).")
    manifest = json.loads((candidate / "build-manifest.json").read_text(encoding="utf-8-sig"))
    if manifest["version"] != version:
        raise ValueError("Manifest version mismatch.")
    manifest_names = [entry["name"] for entry in manifest["files"]]
    if len(manifest_names) != len(set(manifest_names)) or set(manifest_names) != actual - {"build-manifest.json", "SHA256SUMS.txt"}:
        raise ValueError("Manifest does not cover every deliverable exactly once.")
    for entry in manifest["files"]:
        if entry["name"] not in actual or hashlib.sha256((candidate / entry["name"]).read_bytes()).hexdigest() != entry["sha256"]:
            raise ValueError("Manifest file hash mismatch.")
    checksums = {}
    for line in (candidate / "SHA256SUMS.txt").read_text(encoding="ascii").splitlines():
        digest, name = line.split("  ", 1)
        if name in checksums or name not in actual or hashlib.sha256((candidate / name).read_bytes()).hexdigest() != digest:
            raise ValueError("Checksum file hash or path mismatch.")
        checksums[name] = digest
    if set(checksums) != actual - {"SHA256SUMS.txt"}:
        raise ValueError("Checksum file does not cover every deliverable and manifest.")
    findings = []
    for name in names:
        # Project copyright is legitimate public attribution in LICENSE.
        findings.extend(audit.scan(name, (candidate / name).read_bytes(), []))
    with zipfile.ZipFile(candidate / archive) as zipped:
        entries = zipped.namelist()
        if len(entries) != len(set(entries)) or set(entries) != names | licenses | {"Grimoire.exe"}:
            raise ValueError("Portable archive allowlist mismatch.")
        # Never extract unsafe names, even if an allowlist changes later.
        if any(PurePosixPath(name).is_absolute() or ".." in PurePosixPath(name).parts for name in entries):
            raise ValueError("Unsafe ZIP path.")
        with tempfile.TemporaryDirectory(prefix="grimoire-package-audit-") as temporary:
            zipped.extractall(temporary)
            extracted = Path(temporary)
            if hashlib.sha256((extracted / "Grimoire.exe").read_bytes()).digest() != hashlib.sha256((candidate / portable).read_bytes()).digest():
                raise ValueError("Portable ZIP executable differs from standalone artifact.")
            for name in names | licenses:
                if (extracted / name).read_bytes() != (candidate / name).read_bytes():
                    raise ValueError("Portable ZIP documentation/license differs from standalone artifact.")
    # Audit the uncompressed app-owned assembly: UTF-8 resources and printable
    # UTF-16LE managed strings. Runtime/vendor assemblies are inventoried by notices.
    assembly = staging / "Grimoire.dll"
    if not assembly.is_file():
        raise ValueError("Required non-single-file staging assembly is missing.")
    findings.extend(audit.scan("Grimoire.dll", assembly.read_bytes(), []))
    for finding in sorted(set(findings)):
        print(finding.render())
    if findings:
        raise ValueError("Privacy findings in app-owned staged content.")
    print("PASS: candidate/ZIP allowlists, hashes, extracted content and app-owned staging privacy checks.")
    print("Scope: UTF-8 resources and printable ASCII UTF-16LE strings in unbundled Grimoire.dll; generic shipped text.")
    print("Vendor/runtime licenses are inventoried and preserved verbatim; compressed executable internals are not exhaustively certified.")


if __name__ == "__main__":
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--candidate", type=Path, required=True)
    parser.add_argument("--staging", type=Path, required=True)
    parser.add_argument("--version", required=True)
    parser.add_argument("--portable-only", action="store_true", help="local preparation only; no installer acceptance claim")
    args = parser.parse_args()
    try:
        check(args.candidate, args.staging, args.version, args.portable_only)
    except (ValueError, OSError, KeyError, zipfile.BadZipFile) as error:
        print("FAIL:", error)
        sys.exit(1)
