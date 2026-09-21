# My spells

Open **My spells → Manage spells…** from the menu, or **Settings → Spells → Manage spells…**. Search by name, kind or input. Select a row to edit, duplicate or remove it; double-click also edits. **New spell** opens an empty editor and **Starter spells** offers optional AI prompts. No provider, shell command or HTTP request runs merely by opening these windows.

The manager saves each confirmed change immediately. **Cancel** in the editor discards its draft; **Close** in the manager and **Cancel** in Settings keep changes already saved by the editor or gallery. Removing a spell names that exact spell in a confirmation, defaults to allowing cancellation, and offers **Undo removal** for that manager session. Undo refuses to replace a file changed since the removal, so it cannot erase later edits. Saving another spell from the manager clears removal undo. Stored credentials are retained when spells are removed.

## Authoring

- **AI prompt**: write an instruction. Selected text is appended to it, or use `{text}` within the instruction to place it yourself; either way it is sent exactly once. File spells also expand `{file}` to the selected path. Choose **Review before replacing**, **Replace selection**, or **Show reply**. Optional writing style and provider role are under **Advanced options**.
- **Shell command**: an explicitly authored `cmd.exe` command runs with your Windows account's permissions. Text arrives on stdin and in `SPELL_TEXT`. A file spell runs once per selected file, with `SPELL_FILE` and the file's folder as the working directory. Use `{file}` or `"{file}"` as a standalone argument; Grimoire turns either into `"%SPELL_FILE%"` and disables delayed expansion. It never copies the selected path into shell source. Embedded forms such as `prefix{file}` are rejected; a script can read the environment variable directly. Additional evaluation inside an authored command (`call`, nested shells, `Invoke-Expression`, etc.) is the author's responsibility.
- **HTTP request**: set an HTTP(S) URL, method, non-secret headers and body. Placeholders are `{text}`, `{json}` (JSON-quoted text), `{file}`, `{filename}` and `{filedata}` for raw file bodies. **Show the server reply** enables the result choice. Requests do not follow redirects; use the final destination URL.

Names cannot contain INI delimiters, control characters or leading/trailing spaces. Duplicate names are checked without regard to capitalization. Validation stays in the editor without closing it. Multiline prompts, command text and bodies retain spaces and line endings. Renames replace the original only after validation and a successful atomic save. If another editor changes the original, reopen it before saving.

## HTTP credentials

Expand **Advanced options** and choose a credential name, header (for example `Authorization` or `X-Api-Key`) and optional prefix (for example `Bearer`). Enter a new credential value in the masked field. **Save spell** stores it in Windows Credential Manager as `Grimoire/spells/<name>`; only the reference and header configuration go into `spells.ini`. An empty value reuses an existing name. A nonempty value cannot overwrite an existing name; use a new name for a replacement. Existing secrets are never loaded into the editor.

**Remove reference** clears the draft's link to that credential. Saving applies the removal; cancelling keeps the existing reference. It does not delete a shared credential. Delete an unused stored value explicitly in Windows Credential Manager. Duplicated spells can share a reference safely; deleting either spell does not delete the credential. Credential-backed HTTP spells require HTTPS, and redirects cannot forward credentials elsewhere.

Do not put credentials in the URL, body or ordinary headers. The editor rejects common authentication/cookie header names in the plain-text header field. Legacy plaintext authentication headers are readable for existing spell execution; editing one requires moving its secret into a named credential. Grimoire cannot detect every possible custom secret field, and it does not automatically discover or migrate old secrets.

## Testing

Expand **Try this spell**. AI tests require a configured provider and send the sample to that provider. Shell and HTTP tests require the explicit **Run test** action followed by confirmation of the command/request scope. File tests require choosing an existing disposable fixture; they run once on that chosen file. Tests can change local or remote data according to the authored command/request. **Cancel test**, closing the editor or disposing it requests cancellation; already completed side effects cannot be undone.

The editor captures all input on its UI thread before starting background work. It does not read controls from the worker or update disposed controls. Transport errors use a generic message so server bodies, command errors or secrets do not get copied into a UI diagnostic. Test results are shown only in the editor, not persisted in spell settings.

## Persistence and migration

Existing legacy INI sections remain readable. An unchanged save leaves the file alone; changing one spell preserves the order and raw text of every other section. New or edited sections keep the same legacy keys and escaped values that 2.1 can read. Where the legacy representation would lose whitespace or exact line endings, supplemental `prompt_json`, `command_json`, `body_json` (or corresponding string-field) keys retain the exact value, marked by `exact_format=json-v1`. The new reader uses a supplemental value only while its legacy mirror still matches. If an older application or manual edit changes the legacy key, that change wins over any stale supplemental value. Unknown keys/comments inside a section being edited are replaced by that spell's supported fields.

Older 2.1 applications can still read and run the legacy spell fields; their existing whitespace/newline normalization remains. Named credential references are a new feature that 2.1 cannot use. Before upgrading, keep a private backup of the data folder and restore it if a rollback needs the exact pre-upgrade state. Credentials remain in Windows Credential Manager and are not exported into that backup.

Writes go to a unique temporary file beside the target, flush to disk, then atomically replace the target. Failed writes retain the old file. The same helper now protects general INI saves (settings, recipes and snippets). Spell edits serialize within the process and reject detected external changes immediately before replacement; do not edit the file simultaneously from another process. Undo is an in-memory snapshot, not a permanent backup.

## Evidence and limits

The synthetic Windows regression runner covers add, edit, rename, case-insensitive collision, cancelled and confirmed removal, undo, stale edits, invalid names, multiline/space preservation, failed writes, credential references, cancellation and the actual manager/editor/gallery factories. It uses fake credential storage and fake execution delegates; it never reads real credentials or calls real providers, shell commands or public HTTP destinations for these new spell tests.

The default runner and `--ui-smoke` produce PNGs under `test-results/ui` at 100%, 150% and 200% geometry/font scales. These are actual WinForms surfaces rendered with `DrawToBitmap`, not a proof of monitor-DPI transitions, keyboard/assistive-technology behavior or live desktop acceptance. Actual Windows execution, screenshot inspection, authenticated operation and user acceptance are separate gates; Linux cross-build alone is not evidence of those gates.
