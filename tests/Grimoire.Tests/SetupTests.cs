using System.Drawing;
using System.Windows.Forms;
using Spellbook;

namespace Spellbook.Tests;

internal static class SetupTests
{
    static void Check(bool value, string message) { if (!value) throw new InvalidOperationException(message); }
    static T Control<T>(Form form, string name) where T : Control => (T)form.Controls.Find(name, true).Single();

    public static void State()
    {
        File.Delete(Paths.Settings);
        Check(!Settings.Load().ClipboardHistory, "fresh profiles must not capture clipboard history");
        File.WriteAllText(Paths.Settings, "[clipboard]\nhistory=1\n[general]\nfirst_run_done=1\n");
        var existing = Settings.Load();
        Check(existing.ClipboardHistory && existing.FirstRunDone, "existing choices must survive");
        File.WriteAllText(Paths.Settings, "[clipboard]\nhistory=0\n[general]\nfirst_run_done=1\n");
        Check(!Settings.Load().ClipboardHistory, "explicit existing opt-out was changed");
        File.Delete(Paths.Settings);
        var settings = new Settings();
        int startupWrites = 0;
        using (var form = new SetupWizard(settings, () => true, _ => startupWrites++, _ => { }))
        {
            Check(Control<CheckBox>(form, "Startup").Checked, "installer startup choice not reflected");
            Control<CheckBox>(form, "History").Checked = true;
            form.Close();
        }
        Check(!settings.FirstRunDone && !settings.ClipboardHistory && startupWrites == 0 && !File.Exists(Paths.Settings), "cancel changed settings");
        using (var form = new SetupWizard(settings, () => true, _ => startupWrites++, _ => { }))
        {
            Control<CheckBox>(form, "History").Checked = true;
            Check(form.TryFinish(), "finish should save");
        }
        Check(settings.FirstRunDone && Settings.Load().FirstRunDone && Settings.Load().ClipboardHistory && startupWrites == 0, "finish or unchanged startup incorrect");
        using (var form = new SetupWizard(settings, () => true, _ => startupWrites++, _ => { }))
        {
            Control<CheckBox>(form, "Startup").Checked = false;
            Check(form.TryFinish() && startupWrites == 1, "deliberate startup change not applied once");
        }
        var failed = new Settings();
        using (var form = new SetupWizard(failed, () => false, _ => { }, _ => { }, _ => throw new IOException("synthetic failure")))
            Check(!form.TryFinish() && !failed.FirstRunDone, "save failure completed setup");
    }

    public static void Roles()
    {
        var settings = new Settings();
        using (var form = ProvidersForm.Create(settings, [new FakeProvider()]))
        {
            Check(form.TrySave(), "provider save failed");
            Check(settings.RoleQuick == "" && settings.RolePrecise == "" && settings.RoleVision == "", "save configured blank roles");
        }
        settings.RoleQuick = "removed-provider:original-model";
        using (var form = ProvidersForm.Create(settings, [new FakeProvider()]))
        {
            Check(form.TrySave() && settings.RoleQuick == "removed-provider:original-model", "unknown role was replaced");
            Control<ComboBox>(form, "QuickProvider").SelectedIndex = 1;
            Control<TextBox>(form, "QuickModel").Text = "fixture-model";
            Check(form.TrySave() && settings.RoleQuick == "synthetic:fixture-model", "explicit role was not saved");
        }
        using (var form = ProvidersForm.Create(settings, [new FakeProvider()]))
        {
            Control<ComboBox>(form, "QuickProvider").SelectedIndex = 0;
            form.Close();
        }
        Check(settings.RoleQuick == "synthetic:fixture-model" && Settings.Load().RoleQuick == settings.RoleQuick, "cancel changed role or saved role did not persist");
        using (var form = ProvidersForm.Create(settings, [new FakeProvider()], _ => throw new IOException("synthetic failure")))
        {
            Control<ComboBox>(form, "QuickProvider").SelectedIndex = 0;
            Check(!form.TrySave() && settings.RoleQuick == "synthetic:fixture-model", "failed save changed settings");
        }
    }

    public static void Cancellation()
    {
        using var provider = new BlockingProvider();
        using var form = ProvidersForm.Create(new Settings(), [provider], _ => { });
        ShowOffscreen(form);
        PumpUntil(() => provider.StatusStarted.IsSet, "status worker did not start");
        Control<Button>(form, "syntheticTest").PerformClick();
        PumpUntil(() => provider.TestStarted.IsSet, "test worker did not start");
        form.Close();
        PumpUntil(() => form.PendingWork.IsCompleted, "close did not cancel provider workers");
        Check(provider.Cancelled == 2, "both status and test must receive cancellation");
        Application.DoEvents(); // run any queued continuations after disposal
    }

    public static void Render(string directory)
    {
        Directory.CreateDirectory(directory);
        var errors = new List<string>();
        void RenderForm(Form form, string filename)
        {
            try { Capture(form, Path.Combine(directory, filename)); }
            catch (Exception e) { errors.Add(e.Message); }
        }
        foreach (float scale in new[] { 1f, 1.5f, 2f })
        {
            using (var wizard = new SetupWizard(new Settings(), () => true, _ => throw new InvalidOperationException("Unexpected registry write"), _ => { }))
            {
                ScaleForFixture(wizard, scale);
                ShowOffscreen(wizard);
                for (int page = 0; page < 3; page++)
                {
                    wizard.ShowPage(page);
                    RenderForm(wizard, $"setup-step-{page + 1}-{scale * 100:0}.png");
                }
            }
            var providers = Providers.All.Select(p => (IProvider)new FakeProvider(p.Id, p.Label, p.Kind)).ToArray();
            using (var form = ProvidersForm.Create(new Settings(), providers, _ => { }))
            {
                ScaleForFixture(form, scale);
                ShowOffscreen(form);
                PumpUntil(() => form.PendingWork.IsCompleted, "synthetic statuses did not finish");
                RenderForm(form, $"providers-accounts-{scale * 100:0}.png");
                Control<TabControl>(form, "ProviderTabs").SelectedIndex = 1;
                RenderForm(form, $"providers-roles-{scale * 100:0}.png");
            }
        }
        File.WriteAllText(Path.Combine(directory, "README.txt"), "Actual forms, synthetic data, DrawToBitmap only. Sizes and fonts are scaled to 100/150/200%; this is not a real monitor-DPI, keyboard, screen-reader, or interactive desktop acceptance test. No credentials, external requests, clipboard capture, hooks, or registry writes.\n");
        Check(errors.Count == 0, string.Join(Environment.NewLine, errors));
    }

    internal static void ShowOffscreen(Form form)
    {
        form.StartPosition = FormStartPosition.Manual; form.Location = new Point(-20000, -20000);
        form.ShowInTaskbar = false; form.Show(); Application.DoEvents();
    }
    internal static void PumpUntil(Func<bool> done, string message)
    {
        var timeout = System.Diagnostics.Stopwatch.StartNew();
        while (!done() && timeout.Elapsed < TimeSpan.FromSeconds(10)) { Application.DoEvents(); Thread.Sleep(10); }
        Check(done(), message); Application.DoEvents();
    }
    static IEnumerable<Control> Tree(Control root)
    {
        yield return root;
        foreach (Control child in root.Controls) foreach (var c in Tree(child)) yield return c;
    }
    internal static void ScaleForFixture(Form form, float scale)
    {
        if (scale == 1) return;
        var fonts = Tree(form).Select(c => (Control: c, Font: c.Font)).ToArray();
        form.AutoScaleMode = AutoScaleMode.None;
        form.Scale(new SizeF(scale, scale));
        foreach (var (control, font) in fonts) control.Font = new Font(font.FontFamily, font.Size * scale, font.Style);
    }
    internal static void Capture(Form form, string path)
    {
        form.PerformLayout(); Application.DoEvents();
        using var bitmap = new Bitmap(form.Width, form.Height);
        form.DrawToBitmap(bitmap, new Rectangle(Point.Empty, bitmap.Size));
        bitmap.Save(path, System.Drawing.Imaging.ImageFormat.Png);
        foreach (var control in Tree(form).Where(c => c.Visible && c.Parent != null))
        {
            var parent = control.Parent!;
            if (parent is ScrollableControl { AutoScroll: true }) continue;
            Check(control.Left >= -1 && control.Top >= -1 && control.Right <= parent.ClientSize.Width + 1 && control.Bottom <= parent.ClientSize.Height + 1,
                $"{Path.GetFileName(path)}: {control.Name}/{control.GetType().Name} {control.Bounds} outside {parent.GetType().Name} {parent.ClientSize}");
            if (control is Label label)
                Check(label.PreferredHeight <= label.Height + 2, $"{Path.GetFileName(path)}: label clipped: {label.Text}");
        }
    }

    sealed class BlockingProvider : IProvider, IDisposable
    {
        public ManualResetEventSlim StatusStarted = new(), TestStarted = new();
        public int Cancelled;
        public string Id => "synthetic";
        public string Label => "Synthetic blocking provider";
        public string Kind => "api";
        public bool SupportsImages => true;
        public string DefaultModel => "fixture";
        public string[] ModelHints => [];
        public bool IsReady() => throw new InvalidOperationException("Readiness should not be read");
        string Wait(ManualResetEventSlim started)
        {
            var token = OperationContext.Token; started.Set(); token.WaitHandle.WaitOne();
            Interlocked.Increment(ref Cancelled); token.ThrowIfCancellationRequested(); return "unreachable";
        }
        public string Status() => Wait(StatusStarted);
        public string Complete(AiRequest request, int timeoutMs) => Wait(TestStarted);
        public void Dispose() { StatusStarted.Dispose(); TestStarted.Dispose(); }
    }

    sealed class FakeProvider(string id = "synthetic", string label = "Synthetic provider (offline fixture)", string kind = "api") : IProvider
    {
        public string Id => id;
        public string Label => label;
        public string Kind => kind;
        public bool SupportsImages => true;
        public string DefaultModel => "fixture";
        public string[] ModelHints => [];
        public string Status() => "Offline fixture";
        public bool IsReady() => throw new InvalidOperationException("Readiness should not be read");
        public string Complete(AiRequest request, int timeoutMs) => "OK";
    }
}
