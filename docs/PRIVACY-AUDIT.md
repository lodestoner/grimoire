# Privacy and source audit

Grimoire's local transforms and file conversions do not require the maintainer's server or credentials. AI and integrations are optional. There is no telemetry service or automatic executable update path in this source candidate.

## Data flow

- Clipboard history is opt-in, holds up to 30 text entries in memory, and clears on exit or user request. Clipboard privacy marker handling is under test; see [verification status](PUBLIC-SOURCE-STATUS.md).
- An AI action sends the selected text, file or image, composed prompt and selected style profile to the chosen provider. Claude CLI uses the signed-in CLI account and requests restricted tools/session settings. Actual supported-version behavior still needs Windows verification. Codex and Gemini CLI actions are temporarily disabled because their current adapters do not establish a selected-input file-read boundary; saved provider choices remain intact, and Grimoire requires an explicit alternative rather than reroute silently. API adapters use user-supplied credentials in Windows Credential Manager and do not expose local agent tools. The default local-model endpoint is loopback; no provider is selected automatically.
- ntfy and Paperless require user-entered destinations. Their tokens are in Credential Manager, but configured remote HTTP transport can still expose tokens and content in transit. SSH hosts, notes folders and download routing start empty or off. User-authored shell spells and SSH commands run with the user's Windows permissions. HTTP spells send the configured template data; ordinary headers in `spells.ini` are plaintext. Use the named credential field for secrets.
- **View releases** opens the public repository's releases page in a browser; it may be empty. The saved historical automatic-check preference is inert. Grimoire does not fetch or replace its executable.
- `grimoire.log` stays in the data folder. It records timestamps, action categories, character counts, timings and window handles. The error logger writes allowlisted categories, exception types and numeric codes, not exception messages, provider response bodies or file paths. Normal provider responses may be shown transiently in the UI. Review logs before sharing them.

## Source and package checks

From the repository root:

```powershell
python scripts/audit-privacy.py
python -m unittest discover -s tests/privacy-audit
```

The scanner reports paths, lines and categories without echoing matched values. It checks tracked text and managed-string patterns for private network defaults, absolute profile paths, host defaults, credential-shaped values, sensitive headers, keys and known token formats. Its rejection fixtures intentionally contain synthetic private-shaped data and are excluded from the default tracked scan. Use `--path` to check a particular directory. Use `--history` only when auditing the Git history of the repository being published; the new repository must start with a fresh root commit, not import the private archive.

`build.ps1` creates a portable package and unbundled staging tree. `scripts/audit-package.py` verifies the candidate and ZIP contents against exact file lists, checks hashes and scans app-owned unbundled strings. `scripts/package-notices.py` inventories resolved package/runtime licenses. These checks do not establish that compressed executable internals, arbitrary binaries, image pixels, untracked files, local app data or external accounts contain no private material. Review the exact staged source and generated artifacts separately. Build workflow packages remain on the runner; only named text evidence files are uploaded.

For remaining runtime security and desktop limits, see [SECURITY.md](../SECURITY.md) and [verification status](PUBLIC-SOURCE-STATUS.md).
