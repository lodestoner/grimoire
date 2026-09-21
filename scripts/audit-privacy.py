#!/usr/bin/env python3
"""Redacted privacy checks for tracked source, artifacts, and reachable Git history.

The scanner prints only path, line, category, and action. It never prints the
matched value. By default it scans files tracked by Git, excluding its own test
fixtures. Use --path for candidate artifacts and --history for reachable blobs.
"""

from __future__ import annotations

import argparse
import ipaddress
import re
import subprocess
import sys
from dataclasses import dataclass
from pathlib import Path
from urllib.parse import urlsplit


ROOT = Path(__file__).resolve().parents[1]
FIXTURE_PREFIX = "tests/privacy-audit/fixtures/"
LOCAL_HOSTS = {"localhost", "127.0.0.1", "::1"}
PRIVATE_HOST_SUFFIXES = (".local", ".lan", ".internal", ".home", ".corp", ".ts.net")
PUBLIC_HANDLE = re.compile(r"(?i)\b(?:noskybee(?:1012)?|lodestoner)\b")
PUBLIC_ATTRIBUTION = {
    "AGENTS.md": ("Maintainer: `lodestoner`",),
    "README.md": ("github.com/noskybee1012/grimoire", "github.com/lodestoner/grimoire", "maintained by [lodestoner]"),
    "CONTRIBUTING.md": ("maintainer is `lodestoner`",),
    "CHANGELOG.md": ("github.com/noskybee1012/grimoire",),
    "LICENSE": ("noskybee",),
    "RELEASING.md": ("github.com/noskybee1012/grimoire", "noskybee.Grimoire"),
    "app/Grimoire/Brand.cs": ("noskybee1012/grimoire",),
    "app/Grimoire/Grimoire.csproj": ("<Authors>lodestoner</Authors>",),
    "installer/Grimoire.iss": ("noskybee", "github.com/noskybee1012/grimoire", '#define MyAppPublisher "lodestoner"'),
    "packaging/winget/noskybee.Grimoire.yaml": ("noskybee", "github.com/noskybee1012"),
    "docs/superpowers/plans/2026-09-20-windows-release-readiness.md": ("lodestoner/grimoire",),
    "scripts/audit-privacy.py": ("noskybee", "lodestoner"),
}
PUBLIC_PROJECT_MARKERS = (
    "github.com/noskybee1012/grimoire",
    "noskybee1012/grimoire",
    "noskybee.Grimoire",
    "github.com/lodestoner/grimoire",
    "lodestoner/grimoire",
)

URL = re.compile(r"(?i)\b(?:https?|wss?)://[^\s<>)\]}'\"]+")
PRIVATE_IPV4 = re.compile(
    r"(?<![\d.])(?:10\.(?:\d{1,3}\.){2}\d{1,3}|"
    r"192\.168\.(?:\d{1,3}\.)\d{1,3}|"
    r"172\.(?:1[6-9]|2\d|3[01])\.(?:\d{1,3}\.)\d{1,3})(?![\d.])"
)
USER_PATH = re.compile(
    r"(?i)(?:[A-Z]:[\\/](?:Users|Documents and Settings)[\\/][^\\/\s]+|"
    r"/(?:home|Users)/[^/\s]+)"
)
PRIVATE_KEY = re.compile(r"-----BEGIN (?:RSA |EC |OPENSSH )?PRIVATE KEY-----")
KNOWN_TOKEN = re.compile(
    r"(?:github_pat_[A-Za-z0-9_]{20,}|gh[pousr]_[A-Za-z0-9]{20,}|"
    r"sk-[A-Za-z0-9_-]{20,}|xox[baprs]-[A-Za-z0-9-]{20,}|AKIA[0-9A-Z]{16})"
)
SECRET_ASSIGNMENT = re.compile(
    r"(?i)\b(?:api[_-]?key|access[_-]?token|auth[_-]?token|client[_-]?secret|"
    r"password|passwd)\b\s*[:=]\s*(?P<value>[^\s,;#)]+)"
)
HOST_ASSIGNMENT = re.compile(
    r"(?i)\b(?:[a-z0-9_]*(?:host|hostname|server|machine)[a-z0-9_]*)\b"
    r"\s*[:=]\s*[\"'](?P<host>[a-z0-9][a-z0-9.-]*)[\"']"
)
CONFIG_HOST_ASSIGNMENT = re.compile(
    r"(?i)^\s*(?:[a-z0-9_]*(?:host|hostname|server|machine)[a-z0-9_]*)"
    r"\s*[:=]\s*(?P<host>[a-z0-9][a-z0-9.-]*)\s*$"
)
SENSITIVE_HEADER = re.compile(
    r"(?i)\b(?:authorization|proxy-authorization|x-api-key|api-key)\s*:\s*(?P<value>\S+)"
)
EMPTY_OR_PLACEHOLDER = re.compile(
    r"(?i)^(?:[\"']{0,2}|none|null|false|0|<[^>]+>|\$\{[^}]+\}|%[^%]+%|"
    r"\{\{?[^}]+\}?\}|example|sample|changeme|your[_-].*)$"
)


@dataclass(frozen=True, order=True)
class Finding:
    path: str
    line: int
    category: str
    action: str
    commit: str = ""

    def render(self) -> str:
        location = f"{self.path}:{self.line}"
        if self.commit:
            location = f"{self.commit}:{location}"
        return f"{location}:{self.category}: {self.action}"


def git(*args: str, binary: bool = False) -> bytes | str:
    result = subprocess.run(
        ["git", *args], cwd=ROOT, check=True, capture_output=True,
        text=not binary,
    )
    return result.stdout


def tracked_files() -> list[tuple[str, bytes]]:
    names = str(git("ls-files", "-z")).split("\0")
    files: list[tuple[str, bytes]] = []
    for name in names:
        if not name or name.startswith(FIXTURE_PREFIX):
            continue
        path = ROOT / name
        if path.is_file():
            files.append((name, path.read_bytes()))
    return files


def explicit_files(paths: list[str]) -> list[tuple[str, bytes]]:
    files: list[tuple[str, bytes]] = []
    for raw in paths:
        target = Path(raw).expanduser().resolve()
        candidates = sorted(p for p in target.rglob("*") if p.is_file()) if target.is_dir() else [target]
        for path in candidates:
            if ".git" in path.parts or not path.is_file():
                continue
            try:
                label = str(path.relative_to(ROOT))
            except ValueError:
                label = path.name
            files.append((label, path.read_bytes()))
    return files


def history_files() -> list[tuple[str, str, bytes]]:
    seen: set[tuple[str, str]] = set()
    files: list[tuple[str, str, bytes]] = []
    commits = str(git("rev-list", "--all")).splitlines()
    for commit in commits:
        tree = bytes(git("ls-tree", "-r", "-z", commit, binary=True))
        for row in tree.split(b"\0"):
            if not row:
                continue
            meta, raw_path = row.split(b"\t", 1)
            path = raw_path.decode("utf-8", errors="replace")
            if path.startswith(FIXTURE_PREFIX):
                continue
            blob = meta.split()[2].decode("ascii")
            key = (blob, path)
            if key in seen:
                continue
            seen.add(key)
            data = bytes(git("cat-file", "blob", blob, binary=True))
            files.append((commit[:12], path, data))
    return files


def public_attribution_allowed(path: str, line: str) -> bool:
    allowed = (*PUBLIC_PROJECT_MARKERS, *PUBLIC_ATTRIBUTION.get(path, ()))
    if any(value.lower() in line.lower() for value in allowed):
        return True
    return Path(path).suffix.lower() in {".exe", ".dll", ".msi", ".msix", ".zip"}


def suspicious_host(host: str) -> bool:
    host = host.strip("[]").lower().rstrip(".")
    if not host or host in LOCAL_HOSTS:
        return False
    try:
        return ipaddress.ip_address(host).is_private
    except ValueError:
        return "." not in host or host.endswith(PRIVATE_HOST_SUFFIXES)


def scan(path: str, data: bytes, deny_terms: list[str], commit: str = "") -> list[Finding]:
    text = data.decode("utf-8", errors="replace")
    # Managed #US strings are UTF-16LE at arbitrary byte offsets. This augments
    # staging inspection; it does not unpack compressed single-file bundles.
    if Path(path).suffix.lower() in {".dll", ".exe"}:
        strings = re.findall(rb"(?:[\x20-\x7e]\x00){5,}", data)
        text += "\n" + "\n".join(value.decode("utf-16-le") for value in strings)
    findings: list[Finding] = []
    config_text = Path(path).suffix.lower() in {".ini", ".cfg", ".conf", ".toml", ".yaml", ".yml"}
    for number, line in enumerate(text.splitlines() or [text], 1):
        categories: set[tuple[str, str]] = set()
        if PRIVATE_IPV4.search(line):
            categories.add(("private-network-address", "replace the hardcoded address with user configuration"))
        if USER_PATH.search(line):
            categories.add(("absolute-user-path", "replace the user-specific path with a known-folder lookup or configuration"))
        if PRIVATE_KEY.search(line):
            categories.add(("private-key", "remove the key and rotate it before distribution"))
        if KNOWN_TOKEN.search(line):
            categories.add(("credential-token", "remove the credential and rotate it before distribution"))
        for match in SECRET_ASSIGNMENT.finditer(line):
            value = match.group("value").strip("\"'")
            if value and not EMPTY_OR_PLACEHOLDER.match(value):
                categories.add(("credential-default", "leave shipped credentials empty and store user secrets outside release files"))
        for match in SENSITIVE_HEADER.finditer(line):
            value = match.group("value").strip("\"'")
            if value and not EMPTY_OR_PLACEHOLDER.match(value):
                categories.add(("credential-header", "do not ship authentication headers in plain text"))
        for match in URL.finditer(line):
            host = urlsplit(match.group(0)).hostname or ""
            if suspicious_host(host):
                categories.add(("private-network-target", "replace the hardcoded target with optional user configuration"))
        for match in HOST_ASSIGNMENT.finditer(line):
            host = match.group("host")
            if suspicious_host(host):
                categories.add(("private-host-default", "leave the shipped host empty and require user configuration"))
        if config_text:
            match = CONFIG_HOST_ASSIGNMENT.match(line)
            if match and suspicious_host(match.group("host")):
                categories.add(("private-host-default", "leave the shipped host empty and require user configuration"))
        if PUBLIC_HANDLE.search(line) and not public_attribution_allowed(path, line):
            categories.add(("owner-identifier", "remove the owner identifier or add a narrow public-attribution allowlist entry"))
        for term in deny_terms:
            if term.casefold() in line.casefold():
                categories.add(("review-identifier", "remove the known private identifier before distribution"))
        findings.extend(Finding(path, number, category, action, commit) for category, action in categories)
    return findings


def parse_args() -> argparse.Namespace:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--path", action="append", default=[], help="scan a file or artifact directory instead of tracked files")
    parser.add_argument("--history", action="store_true", help="scan all unique blobs reachable from Git refs")
    parser.add_argument("--deny-term", action="append", default=[], help="case-insensitive private identifier to reject; matched text is never printed")
    return parser.parse_args()


def main() -> int:
    args = parse_args()
    findings: set[Finding] = set()
    if args.history:
        for commit, path, data in history_files():
            findings.update(scan(path, data, args.deny_term, commit))
    else:
        inputs = explicit_files(args.path) if args.path else tracked_files()
        for path, data in inputs:
            findings.update(scan(path, data, args.deny_term))
    for finding in sorted(findings):
        print(finding.render())
    if findings:
        print(f"FAIL: {len(findings)} privacy finding(s); matched values were redacted.")
        return 1
    print("PASS: no privacy findings in the scanned inputs.")
    return 0


if __name__ == "__main__":
    sys.exit(main())
