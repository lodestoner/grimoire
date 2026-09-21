# Changelog

All notable changes to Grimoire. Versions follow [semver](https://semver.org).

## 2.2.0-rc.1 — source candidate, 2026-09-20

- Exact .NET 10 SDK and locked dependencies; synthetic Windows regressions and local candidate packages. Public CI uploads text evidence only.
- Optional first-run setup, provider diagnostics and account-role selection; improved spell editing and management.
- Cancellable operations and provider processes with bounded cleanup.
- Per-user installer preserves actual startup choices and user data, requests bounded own-path graceful exit, and refuses a busy legacy instance without killing portable copies.
- **View releases** opens the public releases page. Automatic executable download/install remains unavailable; no public binary release exists yet.
- One version source, portable EXE/ZIP and installer, commit/hash manifest, runtime/package notices, package privacy audit and disposable installer tests.
- Selected text or file contents reach the model exactly once: a prompt that places it with `{text}` no longer also sends it separately, and file prompts expand `{text}` and `{file}` the same way.
- An image only reaches the provider chosen for the role. Grimoire no longer searches for another image-capable provider behind your back; it asks you to configure Vision instead.
- Codex and Gemini CLI actions are temporarily disabled pending selected-input isolation; saved choices remain. Signing, real standard-user desktop acceptance and SmartScreen remain pending. This is not a public binary release.

## 2.1.0 - 2026-09-10

Historical standalone Windows build. Its private release artifact is not part of this new public source repository.

### Added
- Ctrl/Alt + right-click spellbook on highlighted text in any app; file menu on Explorer selections, the Send-to shortcut, or the drop zone.
- AI providers: sign in with Claude Code, Codex CLI, or Gemini CLI (no key), or use an Anthropic, OpenAI, or Gemini API key, or any OpenAI-compatible local server. Separate providers for quick edits, precise polish, and vision.
- Custom spells: AI prompt, shell command, or HTTP call, with an in-app editor and Test button. Starter spell gallery on first run.
- File conversions: images (HEIC and WebP in; JPG, PNG, BMP, TIFF, GIF out; resize, compress, strip metadata), DOCX to Markdown or text, PDF to text, Markdown to HTML or DOCX, CSV and JSON, JSON and YAML, line endings, ffmpeg media (GIF, MP3, compress, MP4, trim), AI summaries and questions on documents, vision on images. Headless `--convert`.
- Clipboard history and snippets.
- Captures to a notes folder, screen snip to OCR, describe, HTML, or PNG, reminders (desktop popup or ntfy), Paperless upload, SSH fleet run.
- Installer (per-user, no admin), portable exe, daily update check with one-click install.
