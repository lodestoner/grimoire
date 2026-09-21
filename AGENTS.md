# Grimoire contributor notes

This is the public Windows source repository. Maintainer: `lodestoner`. External contributors use their own Git identity. Keep user data, credentials, local logs, build output and private review notes out of commits.

Use the SDK pinned in `global.json` and locked NuGet restores. Run `python3 scripts/audit-privacy.py` and `python3 -m unittest discover -s tests/privacy-audit` for source checks. Windows CI builds the app, runs synthetic regressions and tests the installer on a disposable hosted runner; do not run `scripts/test-install.ps1` on an everyday Windows profile. A Linux cross-build proves compilation only.

Changes to provider or clipboard behavior need tests that exercise the privacy boundary, not just command-line flags. Document actual verification and pending interactive Windows checks. No public binary release exists yet; do not claim a local or CI package is signed, production ready or available for download.
