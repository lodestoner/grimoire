# Grimoire roadmap

## Current source candidate

The Windows app implements local text and file transforms, custom spells and recipes, optional AI providers, a per-user installer and a portable build. The `2.2.0-rc.1` source candidate has no public binary release. [Verification status](docs/PUBLIC-SOURCE-STATUS.md) separates synthetic CI checks from pending interactive and provider work.

## Before a public binary release

- Establish selected-input isolation for agentic Codex and Gemini CLI adapters before re-enabling them. Validate on Windows with an offline canary fixture and supported CLI versions. Their saved choices remain intact while actions are disabled.
- Fix authenticated remote integrations that permit plain HTTP; bound remote responses and file/conversion work; review active HTML and spreadsheet formula output, cancellation, focus restoration and local file handoff.
- Complete standard-user desktop, input/clipboard, provider sign-in, sleep/reconnect and clean-download SmartScreen acceptance. Decide signing and authenticated update delivery separately.

## Later work

- Evaluate a native ChatGPT sign-in/account-status integration through Codex app-server. The current integration launches the Codex CLI and relies on its existing sign-in; no native Grimoire account flow is implemented.
- Explorer context-menu integration, spell-pack import/export, transcription, localized menus and a separate macOS port.

Vendor CLI capabilities change by version. Any new account or tool integration needs its own scope, compatibility and security review.
