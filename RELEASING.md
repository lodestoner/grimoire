# Building a source candidate

Grimoire `2.2.0-rc.1` is a development candidate. This repository has no public binary release. Source publication and a green CI build do not certify an interactive Windows desktop, a provider account, code signing or SmartScreen behavior.

## Local Windows build

Use the SDK version in `global.json` (10.0.401), Python 3 and Inno Setup 6. CI pins Inno Setup 6.7.1. From a clean checkout:

```powershell
./build.ps1 -ValidateOnly
./build.ps1
```

The build restores locked dependencies, checks version consistency and prepares an unsigned installer, portable executable/ZIP, notices, checksum list and build manifest under `dist/candidate/`. `-SkipInstaller` omits the installer. The manifest records the commit and working-tree state; a local build is not a published artifact. `build.ps1` does not tag, release, upload or install anything.

The public Build workflow compiles and runs synthetic regressions, source privacy checks, package/license audits, an installer lifecycle test on a disposable Windows runner and a Defender scan when available. It uploads only named text evidence files. No installer or executable is uploaded. See [verification status](docs/PUBLIC-SOURCE-STATUS.md) for what has and has not been exercised.

## Historical upgrade fixture

The default public workflow does not need the private 2.1.0 release. Its clean install, same-version replacement, busy-file refusal, lifecycle and unrelated portable preservation tests run without that archive. The old 2.1.0 upgrade/restore test is optional and marked **NOT RUN** unless a maintainer explicitly supplies both a local 2.1.0 installer and its SHA256 to `scripts/test-install.ps1` on a disposable GitHub-hosted Windows runner. The script checks the digest before installing. Do not copy the historical binary into the public repository or upload it as CI evidence.

## Publication gates

Before a public binary release, complete the remaining security and desktop acceptance work in [SECURITY.md](SECURITY.md) and [verification status](docs/PUBLIC-SOURCE-STATUS.md). Decide a signing and update-delivery mechanism, test a clean download on a real standard-user Windows desktop, and review the exact package and notices. The current manual **View releases** action merely opens the repository releases page; the automatic update setting is inert. Existing older binaries are outside this source candidate and are not changed remotely.
