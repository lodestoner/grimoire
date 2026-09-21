# First-time setup

Grimoire works without an AI account or personal server. On a fresh profile, the tray and local text tools are available immediately, and a three-step setup window opens:

1. **Welcome:** how to open the menu and use local tools.
2. **Optional AI:** skip this step, or explicitly open the provider dialog to configure an existing account, API key, or compatible server. The wizard itself does not discover, authenticate, test or contact providers.
3. **Preferences:** choose menu chords, hotkeys, clipboard history and Windows startup. **View releases** opens the public repository's releases page, which may be empty; automatic update checks are unavailable. **Finish** saves preferences and marks setup complete. Clipboard history starts disabled on a fresh profile; it begins only after a saved opt-in. The saved historical update-check preference is preserved but disabled.

**Not now**, Escape, or the window close button leaves setup incomplete and the tray usable. Resume with **Set up Grimoire** in the tray menu. A completed profile does not reopen the wizard automatically. Existing preferences and completed setup remain in place. An unchanged startup checkbox never rewrites the Run entry, preserving the installer's startup selection and executable path.

AI provider settings and credentials are deliberately separate from setup preferences. Explicitly saved provider choices, API keys and vendor sign-ins remain if setup is cancelled. The provider dialog saves roles and its endpoint only on **Save**; **Close** discards those edits. Keys are written immediately to Windows Credential Manager. Each role offers **None / not configured**, and unavailable saved providers/models survive an unchanged save. **None** means no separate choice for that role rather than a disabled action: Precise and Vision then use the provider configured for Quick edits. An image is only ever sent to the provider chosen for that role; if it cannot read images, Grimoire says so and asks you to configure Vision rather than using a different provider. With every role left at None, nothing is sent anywhere. Opening the provider dialog does not test AI; account status checks run asynchronously, and the compatible-server endpoint is contacted only by an explicit test. Save a changed endpoint before testing it.

Codex and Gemini CLI actions are temporarily disabled pending selected-input isolation. Existing choices remain saved so they can be reconsidered later; choose another provider explicitly for current work. Grimoire does not silently reroute a disabled provider's request.

Provider tests send a small sample prompt and may incur provider usage. **Cancel** on the test button or closing the dialog cancels its active workers. Install and sign-in actions are explicit. Local text tools and a skipped AI setup require none of them.

Starter spells remain available later under **My spells → Starter spells**. Settings can be changed later from the tray.

## Offline Windows verification

The default synthetic regression runner includes setup state, provider role preservation, failure paths, cancellation on close, and actual-form rendering. The fixture sets an isolated `GRIMOIRE_HOME` and injects startup/provider dependencies. It never uses real credentials, CLI discovery, vendor sign-in, clipboard capture, global hooks, or the user's startup registry entry.

Run all regressions on Windows:

```powershell
dotnet run --project tests/Grimoire.Tests/Grimoire.Tests.csproj -c Release -- --report test-results/windows-regression.txt
```

Run just setup/provider checks and rendering:

```powershell
dotnet run --project tests/Grimoire.Tests/Grimoire.Tests.csproj -c Release -- --ui-smoke --report test-results/ui-smoke.txt
```

PNG artifacts under `test-results/ui/` are drawn from actual forms with `DrawToBitmap`, using synthetic labels and scaled control/font sizes at 100%, 150% and 200%. They are app-only images, never captures of a user's desktop. They remain on the runner and are not uploaded by public CI. Bounds assertions distinguish a scrollable viewport from controls overflowing their layout parent.

These fixtures do not prove real monitor DPI transitions, screen-reader output, keyboard/focus behavior, authenticated providers, installed startup, or Explorer/tray usability. Those remain manual Windows acceptance checks. A Linux cross-build proves compilation only; screenshot and runtime evidence require the Windows runner.
