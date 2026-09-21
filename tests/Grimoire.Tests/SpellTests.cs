using System.Windows.Forms;
using Spellbook;

namespace Spellbook.Tests;

internal static class SpellTests
{
    static void Check(bool value, string message) { if (!value) throw new InvalidOperationException(message); }
    static T C<T>(Form form, string name) where T : Control => (T)form.Controls.Find(name, true).Single();
    static Spell Prompt(string name = "A sample spell") => new(name, "prompt", "  Keep spaces.\r\n\r\nAnd lines.  ", "review", "none", "quick", "", "", "POST", "", "{text}", false, false);
    static Spell Http(string name = "Example request") => Prompt(name) with { Type = "http", Url = "https://example.invalid/fixture", Method = "POST", Headers = "Content-Type: application/json", Body = "{\"text\": {json}}", ShowResponse = true };
    static string PathFor(string name) => Paths.File_(name + ".ini");
    static void Fails(Action action, string message)
    {
        try { action(); } catch (InvalidOperationException) { return; } catch (IOException) { return; } catch (UnauthorizedAccessException) { return; }
        throw new InvalidOperationException(message);
    }
    public static void Store()
    {
        var path = PathFor("spell-store-fixture");
        const string untouched = "[Untouched]\r\n; keep this comment and spacing\r\ntype = prompt\r\nprompt = Original\\ntext\r\ncustom = keep me\r\n\r\n";
        File.WriteAllText(path, "; keep header\r\n" + untouched);
        var store = new SpellStore(path); var a = Prompt(); store.Save(a);
        Check(store.All()[1] == a, "multiline/space-preserving round-trip failed");
        Check(File.ReadAllText(path).Contains(untouched), "add changed unrelated section bytes");
        var oldReader = Ini.Load(path);
        Check(oldReader.Get(a.Name, "prompt") == "Keep spaces.\\n\\nAnd lines.", "older reader cannot use edited prompt");
        oldReader.Set(a.Name, "prompt", "Edited by older app"); oldReader.Save(path);
        Check(store.All()[1].Prompt == "Edited by older app", "supplemental exact field shadowed an older edit");
        store.Save(a, store.All()[1]);
        var legacyCommand = Prompt("Legacy-compatible command") with { Type = "command", Command = "  fixture-tool \"argument\"\r\nsecond  " };
        store.Save(legacyCommand);
        Check(Ini.Load(path).Get(legacyCommand.Name, "command") == "fixture-tool \"argument\"\\nsecond", "older reader cannot use edited command");
        Check(store.All().Last().Command == legacyCommand.Command, "exact command text lost");
        store.Delete(legacyCommand.Name);
        // Reset to the original raw layout after exercising an older app's normalizing save.
        File.WriteAllText(path, "; keep header\r\n" + untouched); store.Save(a);
        var before = File.ReadAllText(path); store.Save(a, a); Check(File.ReadAllText(path) == before, "unchanged save rewrote file");
        Fails(() => store.Save(a with { Name = a.Name.ToUpperInvariant() }), "case-insensitive duplicate allowed");
        var renamed = a with { Name = "Renamed sample", Prompt = " Edited\n\n prompt ", Command = "one\r\ntwo" };
        store.Save(renamed, a);
        Check(store.All().Select(s => s.Name).SequenceEqual(new[] { "Untouched", renamed.Name }), "rename moved or lost a section");
        Check(store.All()[1] == renamed && File.ReadAllText(path).Contains(untouched), "rename changed unrelated content");
        var other = Prompt("Another spell"); store.Save(other);
        before = File.ReadAllText(path);
        Fails(() => store.Save(renamed with { Name = "ANOTHER SPELL" }, renamed), "rename collision allowed");
        Check(File.ReadAllText(path) == before, "collision changed original");
        foreach (var invalid in new[] { "", " spaced ", "Bad]name", "Bad[name", "Bad=name", "Bad\nname", "Bad\rname", "Bad;name", "Bad#name" })
            Fails(() => store.Save(Prompt(invalid)), "invalid name allowed");
        Check(File.ReadAllText(path) == before, "invalid names changed file");
        var failing = new SpellStore(path, (_, _) => throw new IOException("synthetic failure"));
        Fails(() => failing.Save(renamed with { Name = "Should not appear" }, renamed), "write did not fail");
        Check(File.ReadAllText(path) == before && !Directory.GetFiles(Path.GetDirectoryName(path)!, ".grimoire-*.tmp").Any(), "failed write changed file or leaked temp");
        using (var locked = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read))
            Fails(() => store.Save(renamed with { Prompt = "Must not replace a locked file" }, renamed), "locked destination unexpectedly replaced");
        Check(File.ReadAllText(path) == before, "actual locked-target failure changed original");
        var undo = store.Delete(renamed.Name);
        Check(!store.All().Any(s => s.Name == renamed.Name), "delete failed");
        store.Undo(undo); Check(File.ReadAllText(path) == before, "undo did not restore exact original");
        undo = store.Delete(renamed.Name); store.Save(Prompt("Later addition"));
        var latest = File.ReadAllText(path); Fails(() => store.Undo(undo), "stale undo should fail"); Check(File.ReadAllText(path) == latest, "stale undo lost new spell");
        Fails(() => store.Save(other with { Name = "Stale" }, other with { Prompt = "stale value" }), "stale editor overwrote current value");
        var editedOther = other with { Prompt = "Updated independently" }; store.Save(editedOther, other);
        Check(store.All().Any(s => s.Name == "Later addition"), "edit lost unrelated addition");
        var ini = Ini.Load(path); before = File.ReadAllText(path); ini.Set("Untouched", "extra", "data");
        Fails(() => ini.Save(path, (_, _) => throw new IOException("synthetic failure")), "INI failure fixture did not fail");
        Check(File.ReadAllText(path) == before, "failed general INI save truncated original");
        Check(Spells.PrepareCommand("tool {file}") == "tool \"%SPELL_FILE%\"", "bare file argument unsafe");
        Check(Spells.PrepareCommand("tool \"{file}\"") == "tool \"%SPELL_FILE%\"", "legacy quoted file argument doubled quotes");
        Fails(() => Spells.PrepareCommand("tool prefix{file}"), "embedded file source allowed");
        var dangerousPath = @"C:\synthetic fixtures\file & %name!^.txt";
        var psi = Spells.CommandStartInfo("tool \"{file}\"", "sample", dangerousPath);
        Check(psi.Arguments.Contains("/v:off") && !psi.Arguments.Contains(dangerousPath) && psi.Environment["SPELL_FILE"] == dangerousPath,
            "file path reached shell source or delayed expansion remained enabled");
        Check(SpellStore.Validate(Http() with { Headers = "Author" + "ization: never-store-this" }) != null, "inline credential header accepted");
        Check(SpellStore.Validate(Http() with { Url = "file:///fixture" }) != null, "non-HTTP URL accepted");
    }
    public static void Editor()
    {
        var path = PathFor("editor-fixture"); File.WriteAllText(path, ""); var store = new SpellStore(path); var original = Prompt(); store.Save(original);
        var vault = new FakeCredentials(); var before = File.ReadAllText(path);
        using (var form = SpellEditor.Create(store, original, credentials: vault))
        {
            Check(form.Build() == original, "unchanged actual editor changed content");
            C<TextBox>(form, "Prompt").Text = "Cancelled draft"; form.Close();
        }
        Check(File.ReadAllText(path) == before, "cancel saved draft");
        using (var form = SpellEditor.Create(store, original, credentials: vault))
        {
            C<TextBox>(form, "SpellName").Text = "Bad]name";
            Check(!form.TrySave() && form.DialogResult == DialogResult.None, "invalid save closed editor");
            C<TextBox>(form, "SpellName").Text = "Renamed in actual form";
            Check(form.TrySave(), "actual form rename failed");
        }
        var http = Http() with { CredentialReference = "synthetic-shared" };
        using (var form = SpellEditor.Create(store, initial: http, credentials: vault))
        {
            Check(C<TextBox>(form, "CredentialValue").UseSystemPasswordChar && C<TextBox>(form, "CredentialValue").Text == "", "credential input is not masked and empty");
            C<TextBox>(form, "CredentialValue").Text = "synthetic fixture value";
            Check(form.TrySave(), "credential-backed spell did not save");
        }
        Check(!File.ReadAllText(path).Contains("synthetic fixture value") && vault.Values.Count == 1, "secret leaked into INI");
        var saved = store.All().Single(s => s.Name == http.Name);
        using (var form = SpellEditor.Create(store, initial: saved with { Name = "Duplicate request" }, credentials: vault)) Check(form.TrySave(), "duplicate could not reuse reference");
        using (var form = SpellEditor.Create(store, saved, credentials: vault))
        {
            C<TextBox>(form, "CredentialValue").Text = "replacement must be rejected";
            Check(!form.TrySave() && vault.Values[http.CredentialReference] == "synthetic fixture value", "shared credential overwritten");
            C<TextBox>(form, "CredentialValue").Clear(); C<TextBox>(form, "CredentialReference").Clear();
            Check(form.TrySave(), "remove reference failed");
        }
        store.Delete("Duplicate request"); Check(vault.Values.Count == 1 && vault.Deletes == 0, "duplicate deletion removed shared credential");
        var failing = new SpellStore(path, (_, _) => throw new IOException("synthetic write failure")); before = File.ReadAllText(path);
        using (var form = SpellEditor.Create(failing, initial: Http("Failed request") with { CredentialReference = "rollback-fixture" }, credentials: vault))
        {
            C<TextBox>(form, "CredentialValue").Text = "ephemeral fixture";
            Check(!form.TrySave(), "failed write reported save");
        }
        Check(vault.Values.Count == 1 && File.ReadAllText(path) == before, "failed save lost old data or leaked new credential");
    }
    public static void Manager()
    {
        var path = PathFor("manager-fixture"); File.WriteAllText(path, ""); var store = new SpellStore(path); store.Save(Prompt("Named precisely")); store.Save(Http());
        bool approve = false; string? asked = null;
        using var form = SpellManager.Create(store, name => { asked = name; return approve; }); SetupTests.ShowOffscreen(form);
        Check(!C<Button>(form, "Remove").Enabled && !C<Button>(form, "Edit").Enabled, "selection-dependent buttons enabled without selection");
        C<TextBox>(form, "Search").Text = "precisely";
        var list = C<ListView>(form, "Spells"); Check(list.Items.Count == 1 && list.Items[0].Tag is Spell, "search or typed row failed");
        list.Items[0].Selected = true; Application.DoEvents();
        Check(!form.RemoveSelected() && asked == "Named precisely" && store.All().Count == 2, "cancel removal changed data or named wrong spell");
        approve = true; Check(form.RemoveSelected() && store.All().Count == 1 && C<Button>(form, "Undo").Enabled, "remove failed");
        Check(form.UndoRemoval() && store.All().Count == 2, "manager undo failed");
        C<TextBox>(form, "Search").Text = "no matches here"; Check(list.Items.Count == 0 && !C<Button>(form, "Edit").Enabled, "search selection remained stale");
    }
    public static void Cancellation()
    {
        var path = PathFor("test-cancellation"); var store = new SpellStore(path); var vault = new FakeCredentials();
        using var started = new ManualResetEventSlim(); bool cancelled = false; int uiThread = Environment.CurrentManagedThreadId;
        using var form = SpellEditor.Create(store, initial: Prompt(), credentials: vault, run: (spell, text, file) =>
        {
            Check(Environment.CurrentManagedThreadId != uiThread && text == "Captured fixture text" && file == null, "worker arguments not captured before scheduling");
            var token = OperationContext.Token; started.Set(); token.WaitHandle.WaitOne(); cancelled = token.IsCancellationRequested; token.ThrowIfCancellationRequested(); return "unreachable";
        }, aiConfigured: () => true);
        SetupTests.ShowOffscreen(form); C<CheckBox>(form, "Try").Checked = true; C<TextBox>(form, "Sample").Text = "Captured fixture text";
        C<Button>(form, "Test").PerformClick(); SetupTests.PumpUntil(() => started.IsSet, "spell test did not start");
        C<TextBox>(form, "Sample").Text = "Changed while running"; form.Close();
        SetupTests.PumpUntil(() => form.PendingWork.IsCompleted, "closing editor did not cancel worker"); Check(cancelled, "worker did not observe cancellation"); Application.DoEvents();
        using var unconfigured = SpellEditor.Create(store, initial: Prompt(), credentials: vault, run: (_, _, _) => throw new Exception("Must not run"), aiConfigured: () => false);
        SetupTests.ShowOffscreen(unconfigured); C<CheckBox>(unconfigured, "Try").Checked = true; C<Button>(unconfigured, "Test").PerformClick();
        Check(C<Label>(unconfigured, "Error").Text.Contains("No AI provider"), "missing provider test explanation absent");
    }
    public static void Render(string directory)
    {
        Directory.CreateDirectory(directory); var path = PathFor("render-spells"); File.WriteAllText(path, ""); var store = new SpellStore(path);
        store.Save(Prompt("Make this clearer")); store.Save(Prompt("Inspect a file") with { Type = "command", Command = "fixture-tool {file}", Files = true, Mode = "show" }); store.Save(Http("Send a sample request"));
        var failures = new List<string>();
        void Capture(Form form, string name, float scale)
        {
            try { SetupTests.ScaleForFixture(form, scale); SetupTests.ShowOffscreen(form); SetupTests.Capture(form, System.IO.Path.Combine(directory, $"{name}-{scale * 100:0}.png")); }
            catch (Exception e) { failures.Add(e.Message); }
        }
        foreach (float scale in new[] { 1f, 1.5f, 2f })
        {
            using (var manager = SpellManager.Create(store, _ => false)) Capture(manager, "spells-manager", scale);
            using (var empty = SpellManager.Create(new SpellStore(PathFor("empty-spells")), _ => false)) Capture(empty, "spells-empty", scale);
            foreach (var spell in store.All())
            {
                using var editor = SpellEditor.Create(store, spell, credentials: new FakeCredentials(), run: (_, _, _) => "Offline fixture", aiConfigured: () => false);
                Capture(editor, "spell-editor-" + spell.Type, scale);
            }
            using (var credentials = SpellEditor.Create(store, initial: Http() with { CredentialReference = "synthetic-reference" }, credentials: new FakeCredentials(), run: (_, _, _) => "Offline fixture", aiConfigured: () => false))
            {
                C<CheckBox>(credentials, "Advanced").Checked = true; C<TextBox>(credentials, "CredentialValue").Text = "synthetic masked fixture";
                SetupTests.ScaleForFixture(credentials, scale); SetupTests.ShowOffscreen(credentials);
                var section = C<TableLayoutPanel>(credentials, "CredentialPanel"); var parent = section.Parent;
                while (parent is not Panel { AutoScroll: true }) parent = parent?.Parent ?? throw new InvalidOperationException("missing editor scroll panel");
                var viewport = (Panel)parent;
                var masked = C<TextBox>(credentials, "CredentialValue"); var remove = C<Button>(credentials, "RemoveCredential");
                // Shown focuses the name field. Pump that work before measuring the actual
                // distance between the masked value and removal button at the current scale.
                masked.Focus(); credentials.PerformLayout(); Application.DoEvents();
                System.Drawing.Rectangle Bounds(Control control) => viewport.RectangleToClient(control.RectangleToScreen(control.ClientRectangle));
                void CaptureCredentialView(string suffix, Control focus, params Control[] required)
                {
                    focus.Focus(); credentials.PerformLayout(); Application.DoEvents();
                    int fieldTop = Bounds(focus).Top - viewport.AutoScrollPosition.Y;
                    viewport.AutoScrollPosition = new System.Drawing.Point(0, Math.Max(0, fieldTop - 4));
                    Application.DoEvents();
                    void CheckCredentialViewport()
                    {
                        foreach (var control in required)
                        {
                            var bounds = Bounds(control);
                            Check(control.Visible && viewport.ClientRectangle.Contains(bounds),
                                $"credential screenshot {suffix} at {scale * 100:0}% does not show {control.Name}: {bounds} in {viewport.ClientRectangle}");
                        }
                        Check(masked.UseSystemPasswordChar && remove.Text == "Remove reference", "credential masking/removal label changed");
                    }
                    CheckCredentialViewport();
                    SetupTests.Capture(credentials, System.IO.Path.Combine(directory, $"spell-credentials{suffix}-{scale * 100:0}.png"));
                    CheckCredentialViewport();
                }
                try
                {
                    if (Bounds(remove).Bottom - Bounds(masked).Top + 8 <= viewport.ClientSize.Height)
                        CaptureCredentialView("", masked, masked, remove);
                    else
                    {
                        // A single viewport cannot contain both controls. Each explicitly named
                        // view still proves its own control is fully visible, with masking checked.
                        CaptureCredentialView("-value", masked, masked);
                        CaptureCredentialView("-removal", remove, remove);
                    }
                }
                catch (Exception e) { failures.Add(e.Message); }
            }
            using (var gallery = StarterSpellsForm.Create(store)) Capture(gallery, "spell-starters", scale);
        }
        Check(failures.Count == 0, string.Join(Environment.NewLine, failures));
    }
    sealed class FakeCredentials : ISpellCredentials
    {
        internal readonly Dictionary<string, string> Values = new(); internal int Deletes;
        public string? Get(string name) => Values.GetValueOrDefault(name);
        public void Set(string name, string value) => Values.Add(name, value);
        public void Delete(string name) { Deletes++; Values.Remove(name); }
    }
}
