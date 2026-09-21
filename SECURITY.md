# Security and vulnerability reports

Grimoire `2.2.0-rc.1` is public source under development, with no public binary release or production certification. To report a vulnerability, use [GitHub private vulnerability reporting](https://github.com/lodestoner/grimoire/security/advisories/new). If that form is unavailable, open a public issue containing only a non-sensitive summary and ask for a private reporting route. Do not put credentials, private documents, raw logs or exploit details involving your own data in an issue.

## Known source-candidate limits

- Codex and Gemini CLI actions are temporarily disabled. Their current process options do not prove that an agent can read only selected text or files; selected content could prompt reads of other accessible files. Saved choices remain intact, and an alternative provider must be chosen explicitly. API adapters do not expose local agent tools. Claude CLI tool restrictions also need live supported-version verification.
- Configurable ntfy, Paperless and OpenAI-compatible destinations may use plain HTTP even with credentials. Credential Manager protects stored values, not network transport. Use a trusted HTTPS endpoint; local model endpoints need a separate loopback policy.
- Remote responses and selected file/conversion inputs lack complete application size and expansion limits. Large or hostile content can exhaust resources or delay cancellation. FFmpeg and some parsers have separate cancellation limits.
- Markdown-to-HTML conversion preserves embedded active HTML. CSV conversion preserves formula-leading cells. Do not open converted untrusted files in a browser or spreadsheet without reviewing them.
- Interactive paste can target the wrong foreground window if focus changes. The local file-handoff pipe also lacks a client-side owner check and message-size bound. These need Windows acceptance and fixes before a general binary release.
- User-authored command/HTTP spells and SSH commands run with the user's permissions or contact destinations they configure. Plaintext settings, snippets and user-authored headers should not contain secrets. API/integration credentials entered through Grimoire use Windows Credential Manager.

These are known limits of this source candidate, not a claim that every vulnerability has been found. The [verification status](docs/PUBLIC-SOURCE-STATUS.md) records which checks have actually run.
