# Public source and verification status

Grimoire `2.2.0-rc.1` is a source candidate. No public binary release is available. The [Build workflow](https://github.com/lodestoner/grimoire/actions/workflows/build.yml) is the source of current Windows CI evidence; read the latest run before claiming a check passed. It builds and audits local packages but uploads text evidence only. This document does not treat private historical run IDs as evidence for the new repository.

## Covered by the workflow when it passes

- Locked restore, version checks, Windows-targeted compile and synthetic app regressions.
- Source privacy and package/license audits; a disposable per-user install, same-version replacement, busy-executable refusal, bounded quit, unrelated portable preservation and retained-data uninstall.
- A Defender scan when available on the hosted runner. Its result and engine state are recorded in the run's text evidence.

The historical 2.1.0 upgrade and old-executable restore test is **NOT RUN** by default because the fresh repository has no old release fixture. It requires an explicitly supplied installer and checksum in a separate authorized disposable-runner test.

## Pending before binary distribution

Real standard-user desktop input, clipboard history/cloud privacy, focus/paste behavior, Explorer/tray use, monitor DPI, sleep/reconnect, provider authentication and actual CLI tool boundaries require hands-on Windows checks. Codex and Gemini CLI actions are temporarily disabled until selected-input isolation is established; saved choices are retained and users must select an alternative explicitly. Signing and clean-download SmartScreen behavior are pending. Security work remains for plain-HTTP credential transport, response/file size limits, active HTML/CSV outputs, cancellation and file-pipe boundaries. See [SECURITY.md](../SECURITY.md) for concrete limits.

A Linux cross-build checks compilation only. CI's hosted account and synthetic forms cannot establish the interactive behavior above. Update this page with observed public workflow evidence after each material change; do not copy private archive hashes or run links into it.
