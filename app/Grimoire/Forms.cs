using System.Drawing.Drawing2D;

namespace Spellbook;

/// <summary>Small non-activating notice near the cursor.</summary>
internal sealed class Toast : Form
{
    static Toast? _inst;
    readonly Label _label;
    readonly System.Windows.Forms.Timer _timer = new();

    Toast()
    {
        FormBorderStyle = FormBorderStyle.None;
        ShowInTaskbar = false;
        TopMost = true;
        StartPosition = FormStartPosition.Manual;
        BackColor = Color.FromArgb(32, 32, 36);
        ForeColor = Color.White;
        AutoSize = true;
        AutoSizeMode = AutoSizeMode.GrowAndShrink;
        Padding = new Padding(12, 9, 12, 9);
        _label = new Label { AutoSize = true, Font = new Font("Segoe UI", 10f), ForeColor = Color.White, BackColor = Color.Transparent, MaximumSize = new Size(640, 0) };
        Controls.Add(_label);
        _timer.Tick += (_, _) => { _timer.Stop(); Hide(); };
    }

    protected override bool ShowWithoutActivation => true;

    protected override CreateParams CreateParams
    {
        get { var cp = base.CreateParams; cp.ExStyle |= Native.WS_EX_NOACTIVATE | Native.WS_EX_TOOLWINDOW | Native.WS_EX_TOPMOST; return cp; }
    }

    /// <summary>Show a message near the cursor; ms=0 keeps it until Dismiss() is called.</summary>
    public static void Show(string msg, int ms)
    {
        if (_inst == null || _inst.IsDisposed) _inst = new Toast();
        var t = _inst;
        if (t.InvokeRequired) { t.BeginInvoke(() => Show(msg, ms)); return; }
        t._timer.Stop();
        t._label.Text = msg;
        var pos = Cursor.Position;
        var scr = Screen.FromPoint(pos).WorkingArea;
        t.Visible = true;
        var sz = t.PreferredSize;
        int x = Math.Min(pos.X + 18, scr.Right - sz.Width - 4), y = Math.Min(pos.Y + 22, scr.Bottom - sz.Height - 4);
        t.Location = new Point(Math.Max(scr.Left, x), Math.Max(scr.Top, y));
        if (ms > 0) { t._timer.Interval = ms; t._timer.Start(); }
    }

    public static void Dismiss()
    {
        if (_inst == null || _inst.IsDisposed) return;
        if (_inst.InvokeRequired) { _inst.BeginInvoke(Dismiss); return; }
        _inst._timer.Stop();
        _inst.Hide();
    }
}

internal static class Ui
{
    public static readonly Font Body = new("Segoe UI", 10f);
    public static readonly Font Mono = new("Consolas", 10.5f);
    public static readonly Font Small = new("Segoe UI", 9f);

    public static Button Btn(string text, int w = 110) => new() { Text = text, Width = w, Height = 32, Font = Body, AutoSize = false };
    public static Label Lbl(string text, int x, int y, int w = 0) => w > 0 ? new Label { Text = text, Left = x, Top = y, Width = w, AutoSize = false } : new Label { Text = text, Left = x, Top = y, AutoSize = true };
    public static LinkLabel Link(string text, string url, int x, int y)
    {
        var l = new LinkLabel { Text = text, Left = x, Top = y, AutoSize = true };
        l.LinkClicked += (_, _) => Util.OpenUrl(url);
        return l;
    }

    public static Form Frame(string title, int w, int h, bool resizable = true)
    {
        var f = new Form
        {
            Text = Brand.Name + " - " + title,
            Font = Body,
            StartPosition = FormStartPosition.CenterScreen,
            ClientSize = new Size(w, h),
            MinimumSize = new Size(Math.Min(w, 480), Math.Min(h, 200)),
            FormBorderStyle = resizable ? FormBorderStyle.Sizable : FormBorderStyle.FixedDialog,
            MaximizeBox = resizable,
            MinimizeBox = false,
            ShowInTaskbar = true,
            TopMost = true,
            Icon = TrayApp.AppIcon,
            AutoScaleMode = AutoScaleMode.Dpi,
        };
        f.Shown += (_, _) => { f.TopMost = false; f.Activate(); };
        return f;
    }
}

/// <summary>Read-only commentary popup: Copy / Close.</summary>
internal static class ResultForm
{
    public static void Show(string title, string body)
    {
        var f = Ui.Frame(title, 820, 560);
        var tb = new TextBox { Multiline = true, ReadOnly = true, ScrollBars = ScrollBars.Both, WordWrap = true, Font = Ui.Mono, Dock = DockStyle.Fill, Text = body.Replace("\r\n", "\n").Replace("\n", "\r\n") };
        var bar = new FlowLayoutPanel { Dock = DockStyle.Bottom, Height = 44, Padding = new Padding(8, 6, 8, 6) };
        var copy = Ui.Btn("Copy"); var close = Ui.Btn("Close");
        copy.Click += (_, _) => { Clip.SetText(tb.Text); Toast.Show("Copied to clipboard", 2500); };
        close.Click += (_, _) => f.Close();
        bar.Controls.AddRange(new Control[] { copy, close });
        f.Controls.Add(tb); f.Controls.Add(bar);
        f.CancelButton = close;
        f.Show();
        tb.Select(0, 0);
    }
}

/// <summary>Editable review popup: Replace selection / Copy / Close.</summary>
internal static class EditForm
{
    public static void Show(string title, string body, Action<string> onReplace)
    {
        var f = Ui.Frame(title + " (review)", 820, 560);
        var tb = new TextBox { Multiline = true, ScrollBars = ScrollBars.Both, WordWrap = true, AcceptsReturn = true, AcceptsTab = true, Font = Ui.Mono, Dock = DockStyle.Fill, Text = body.Replace("\r\n", "\n").Replace("\n", "\r\n") };
        var bar = new FlowLayoutPanel { Dock = DockStyle.Bottom, Height = 44, Padding = new Padding(8, 6, 8, 6) };
        var rep = Ui.Btn("Replace selection", 160); var copy = Ui.Btn("Copy"); var close = Ui.Btn("Close");
        rep.Click += (_, _) => { var t = tb.Text; f.Close(); onReplace(t); };
        copy.Click += (_, _) => { Clip.SetText(tb.Text); Toast.Show("Copied to clipboard", 2500); };
        close.Click += (_, _) => f.Close();
        bar.Controls.AddRange(new Control[] { rep, copy, close });
        f.Controls.Add(tb); f.Controls.Add(bar);
        f.CancelButton = close;
        f.Show();
        tb.Select(0, 0);
    }
}

internal static class InputDialog
{
    public static string? Show(string title, string prompt, string def = "", bool multiline = false, bool password = false)
    {
        using var f = Ui.Frame(title, 520, multiline ? 260 : 150, false);
        var lbl = new Label { Text = prompt, AutoSize = false, Left = 12, Top = 10, Width = 496, Height = multiline ? 60 : 44 };
        var tb = new TextBox { Left = 12, Top = lbl.Bottom + 4, Width = 496, Font = Ui.Body, Text = def, Multiline = multiline, ScrollBars = multiline ? ScrollBars.Vertical : ScrollBars.None, AcceptsReturn = multiline, UseSystemPasswordChar = password };
        tb.Height = multiline ? 110 : 28;
        var ok = Ui.Btn("OK"); var cancel = Ui.Btn("Cancel");
        ok.Left = 288; cancel.Left = 398; ok.Top = cancel.Top = tb.Bottom + 12;
        ok.DialogResult = DialogResult.OK; cancel.DialogResult = DialogResult.Cancel;
        f.AcceptButton = ok; f.CancelButton = cancel;
        f.ClientSize = new Size(520, ok.Bottom + 12);
        f.Controls.AddRange(new Control[] { lbl, tb, ok, cancel });
        f.Shown += (_, _) => { tb.Focus(); tb.SelectAll(); };
        return f.ShowDialog() == DialogResult.OK ? tb.Text : null;
    }
}

internal static class FleetForm
{
    public static (List<string> hosts, string cmd)? Show(string initialCmd, List<(string Host, string Desc)> hosts)
    {
        using var f = Ui.Frame("Fleet run", 520, 380, false);
        var y = 10;
        f.Controls.Add(Ui.Lbl("Run on which hosts:", 12, y)); y += 26;
        var boxes = new List<(CheckBox cb, string host)>();
        foreach (var (host, desc) in hosts)
        {
            var cb = new CheckBox { Text = desc.Length > 0 ? $"{host}   ({desc})" : host, Left = 20, Top = y, AutoSize = true };
            boxes.Add((cb, host)); f.Controls.Add(cb); y += 26;
        }
        y += 8;
        f.Controls.Add(Ui.Lbl("Command (runs in each host's login shell):", 12, y)); y += 24;
        var tb = new TextBox { Left = 12, Top = y, Width = 496, Height = 70, Multiline = true, Text = initialCmd, Font = Ui.Mono, ScrollBars = ScrollBars.Vertical };
        f.Controls.Add(tb); y = tb.Bottom + 12;
        var run = Ui.Btn("Run"); var cancel = Ui.Btn("Cancel");
        run.Left = 288; cancel.Left = 398; run.Top = cancel.Top = y;
        run.DialogResult = DialogResult.OK; cancel.DialogResult = DialogResult.Cancel;
        f.AcceptButton = run; f.CancelButton = cancel;
        f.Controls.AddRange(new Control[] { run, cancel });
        f.ClientSize = new Size(520, run.Bottom + 12);
        if (f.ShowDialog() != DialogResult.OK) return null;
        return (boxes.Where(b => b.cb.Checked).Select(b => b.host).ToList(), tb.Text);
    }
}

internal static class ReminderForm
{
    public static (string text, string when, string target)? Show(string initialText, string[] targets)
    {
        using var f = Ui.Frame("Create reminder", 460, 320, false);
        int y = 10;
        f.Controls.Add(Ui.Lbl("Reminder:", 12, y)); y += 24;
        var tb = new TextBox { Left = 12, Top = y, Width = 436, Height = 70, Multiline = true, Text = initialText, ScrollBars = ScrollBars.Vertical };
        f.Controls.Add(tb); y = tb.Bottom + 10;
        f.Controls.Add(Ui.Lbl("When  (+30m / +2h / +1d, 14:30, or yyyy-MM-dd HH:mm):", 12, y)); y += 24;
        var when = new TextBox { Left = 12, Top = y, Width = 220, Text = "+1h" };
        f.Controls.Add(when); y = when.Bottom + 10;
        f.Controls.Add(Ui.Lbl("Where:", 12, y)); y += 24;
        var dd = new ComboBox { Left = 12, Top = y, Width = 260, DropDownStyle = ComboBoxStyle.DropDownList };
        dd.Items.AddRange(targets); dd.SelectedIndex = 0;
        f.Controls.Add(dd); y = dd.Bottom + 16;
        var ok = Ui.Btn("Create"); var cancel = Ui.Btn("Cancel");
        ok.Left = 228; cancel.Left = 338; ok.Top = cancel.Top = y;
        ok.DialogResult = DialogResult.OK; cancel.DialogResult = DialogResult.Cancel;
        f.AcceptButton = ok; f.CancelButton = cancel;
        f.Controls.AddRange(new Control[] { ok, cancel });
        f.ClientSize = new Size(460, ok.Bottom + 12);
        if (f.ShowDialog() != DialogResult.OK) return null;
        return (tb.Text, when.Text, dd.Text);
    }
}

internal static class RoutePickerForm
{
    public static string? Show(string fileName, List<string> folders)
    {
        using var f = Ui.Frame("Route download", 520, 460, false);
        int y = 10;
        f.Controls.Add(new Label { Text = "Downloaded:  " + fileName, Left = 12, Top = y, AutoSize = true, Font = new Font(Ui.Body, FontStyle.Bold) }); y += 26;
        f.Controls.Add(Ui.Lbl("Type to filter, then pick a folder (Enter = Move):", 12, y)); y += 24;
        var filt = new TextBox { Left = 12, Top = y, Width = 496 }; f.Controls.Add(filt); y = filt.Bottom + 6;
        var lb = new ListBox { Left = 12, Top = y, Width = 496, Height = 260, IntegralHeight = false }; f.Controls.Add(lb); y = lb.Bottom + 12;
        void Fill(string q) { lb.BeginUpdate(); lb.Items.Clear(); foreach (var x in folders) if (q.Length == 0 || x.Contains(q, StringComparison.OrdinalIgnoreCase)) lb.Items.Add(x); if (lb.Items.Count > 0) lb.SelectedIndex = 0; lb.EndUpdate(); }
        Fill("");
        filt.TextChanged += (_, _) => Fill(filt.Text);
        filt.KeyDown += (_, e) => { if (e.KeyCode == Keys.Down && lb.Items.Count > 0) { lb.Focus(); e.Handled = true; } };
        var move = Ui.Btn("Move here", 130); var keep = Ui.Btn("Keep in Downloads", 160);
        move.Left = 12; keep.Left = 150; move.Top = keep.Top = y;
        move.DialogResult = DialogResult.OK; keep.DialogResult = DialogResult.Cancel;
        lb.DoubleClick += (_, _) => { if (lb.SelectedItem != null) f.DialogResult = DialogResult.OK; };
        f.AcceptButton = move; f.CancelButton = keep;
        f.Controls.AddRange(new Control[] { move, keep });
        f.ClientSize = new Size(520, move.Bottom + 12);
        f.Shown += (_, _) => filt.Focus();
        if (f.ShowDialog() != DialogResult.OK) return null;
        return lb.SelectedItem?.ToString();
    }
}

/// <summary>A window you can drop files on when Explorer is not the source.</summary>
internal sealed class DropZoneForm : Form
{
    static DropZoneForm? _inst;

    public static void Open(TrayApp app)
    {
        if (_inst == null || _inst.IsDisposed) _inst = new DropZoneForm(app);
        _inst.Show(); _inst.Activate();
    }

    DropZoneForm(TrayApp app)
    {
        Text = Brand.Name + " - drop files";
        Font = Ui.Body; Icon = TrayApp.AppIcon; TopMost = true; ShowInTaskbar = true;
        FormBorderStyle = FormBorderStyle.FixedToolWindow; StartPosition = FormStartPosition.CenterScreen;
        ClientSize = new Size(340, 200); AllowDrop = true;
        var lbl = new Label { Text = "Drop files or folders here\n\nor click to browse", Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleCenter, Font = new Font("Segoe UI", 12f), AllowDrop = true, ForeColor = Color.DimGray };
        Controls.Add(lbl);
        void Handle(DragEventArgs e)
        {
            if (e.Data?.GetData(DataFormats.FileDrop) is string[] paths && paths.Length > 0)
                BeginInvoke(() => app.ShowFileMenu(paths.ToList(), Cursor.Position));
        }
        DragEnter += (_, e) => e.Effect = e.Data?.GetDataPresent(DataFormats.FileDrop) == true ? DragDropEffects.Copy : DragDropEffects.None;
        lbl.DragEnter += (_, e) => e.Effect = e.Data?.GetDataPresent(DataFormats.FileDrop) == true ? DragDropEffects.Copy : DragDropEffects.None;
        DragDrop += (_, e) => Handle(e);
        lbl.DragDrop += (_, e) => Handle(e);
        lbl.Click += (_, _) =>
        {
            using var dlg = new OpenFileDialog { Multiselect = true, Title = "Pick files for " + Brand.Name };
            if (dlg.ShowDialog(this) == DialogResult.OK && dlg.FileNames.Length > 0) app.ShowFileMenu(dlg.FileNames.ToList(), Cursor.Position);
        };
    }
}

/// <summary>AI providers: vendor sign-in or Windows Credential Manager; roles may remain unconfigured.</summary>
internal static class ProvidersForm
{
    public static ProviderDialog Create(Settings settings, IProvider[]? providers = null, Action<Settings>? save = null)
        => new(settings, providers ?? Providers.All, save ?? (s => s.Save()), providers != null);

    public static void Show(Settings settings)
    {
        using var f = Create(settings);
        if (f.ShowDialog() == DialogResult.OK) Toast.Show("Providers saved", 2500);
    }
}

internal sealed class ProviderDialog : Form
{
    readonly Settings _settings;
    readonly Action<Settings> _save;
    readonly CancellationTokenSource _lifetime = new();
    readonly List<Task> _workers = [];
    readonly Label _error;
    readonly TextBox _endpoint;
    readonly RoleChoice _quick, _precise, _vision;
    bool _closing;
    internal Task PendingWork => Task.WhenAll(_workers);

    internal ProviderDialog(Settings settings, IProvider[] providers, Action<Settings> save, bool synthetic)
    {
        _settings = settings; _save = save;
        DialogLayout.Frame(this, "AI providers", new Size(800, 720), new Size(720, 540));
        var shell = DialogLayout.Column(); shell.Dock = DockStyle.Fill; shell.Padding = new Padding(16);
        shell.RowStyles.Add(new RowStyle(SizeType.AutoSize)); shell.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        shell.RowStyles.Add(new RowStyle(SizeType.AutoSize)); shell.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        Controls.Add(shell);
        shell.Controls.Add(DialogLayout.Text("AI is optional. Local text tools work without a provider."), 0, 0);
        var tabs = new TabControl { Name = "ProviderTabs", Dock = DockStyle.Fill };
        shell.Controls.Add(tabs, 0, 1);
        var accounts = new TabPage("Accounts") { AutoScroll = true, Padding = new Padding(10) };
        var roles = new TabPage("Which to use") { AutoScroll = true, Padding = new Padding(10) };
        tabs.TabPages.AddRange([accounts, roles]);
        var accountContent = DialogLayout.Column(); accountContent.Dock = DockStyle.Top; accounts.Controls.Add(accountContent);
        accountContent.Controls.Add(DialogLayout.Text("Install a vendor client and sign in, or set an API key. Test sends a short sample prompt and may use paid quota. Closing cancels running tests."));
        var statuses = new List<(IProvider Provider, Label Status)>();
        foreach (var provider in providers)
        {
            var disabledReason = Providers.DisabledReason(provider.Id);
            var card = DialogLayout.Column(); card.Dock = DockStyle.Top; card.Margin = new Padding(0, 0, 0, 10);
            var heading = new TableLayoutPanel { ColumnCount = 2, Dock = DockStyle.Top, AutoSize = true, Margin = Padding.Empty };
            heading.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 65)); heading.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 35));
            var title = DialogLayout.Text(provider.Label); title.Margin = new Padding(3, 3, 3, 2);
            var status = DialogLayout.Text(disabledReason ?? (provider.Id == "local" ? "Test when ready" : "Checking…"));
            status.Name = provider.Id + "Status"; status.ForeColor = Color.DimGray; status.Margin = new Padding(3, 3, 3, 2);
            heading.Controls.Add(title, 0, 0);
            if (disabledReason == null) heading.Controls.Add(status, 1, 0);
            else heading.SetColumnSpan(title, 2);
            card.Controls.Add(heading);
            // Longer availability explanations need the full card width, not the short-status column.
            if (disabledReason != null) card.Controls.Add(status);
            if (disabledReason == null) statuses.Add((provider, status));
            var actions = new FlowLayoutPanel { AutoSize = true, Dock = DockStyle.Top, WrapContents = true, Margin = Padding.Empty };
            var install = DialogLayout.Button(provider.Kind == "cli" ? "Install" : "Get key", provider.Id + "Install");
            var auth = DialogLayout.Button(provider.Kind == "cli" ? "Sign in" : "Set key…", provider.Id + "Auth");
            install.Enabled = disabledReason == null && !synthetic && provider.Id != "local";
            auth.Enabled = disabledReason == null && !synthetic;
            install.Click += (_, _) => { if (disabledReason == null) InstallOrKey(provider); };
            auth.Click += (_, _) =>
            {
                if (disabledReason != null) return;
                try
                {
                    if (provider.Kind == "cli")
                    {
                        var command = provider.Id switch { "claude-cli" => "claude auth login", "codex-cli" => "codex login", "gemini-cli" => "gemini", _ => "" };
                        if (command.Length > 0) Providers.RunInConsole(command, "Sign in: " + provider.Label);
                    }
                    else
                    {
                        var key = InputDialog.Show(provider.Label, "API key (saved immediately in Windows Credential Manager):", "", false, true);
                        if (key == null) return;
                        Credentials.Set(provider.Id, key.Trim()); status.Text = key.Trim().Length == 0 ? "No key" : "Key set";
                    }
                }
                catch (Exception e) { ShowError("Account setup failed. Check the provider instructions and try again.", e); }
            };
            var test = DialogLayout.Button("Test", provider.Id + "Test");
            var model = new TextBox { Name = provider.Id + "TestModel", AccessibleName = provider.Label + " test model", Width = 200, PlaceholderText = "Model (blank = default)", Margin = new Padding(4, 9, 4, 4) };
            test.Enabled = model.Enabled = disabledReason == null;
            actions.Controls.AddRange([install, auth, test, model]); card.Controls.Add(actions); accountContent.Controls.Add(card);
            CancellationTokenSource? running = null;
            test.Click += async (_, _) =>
            {
                if (disabledReason != null) return;
                if (running != null) { running.Cancel(); return; }
                var cts = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token); running = cts;
                test.Text = "Cancel"; status.Text = "Testing…";
                string selectedModel = model.Text.Trim();
                var worker = Task.Run(() => { using var scope = OperationContext.Push(cts.Token); return Ai.Test(provider, selectedModel); });
                _workers.Add(worker);
                try
                {
                    var result = await worker;
                    if (!CanUpdate || cts.IsCancellationRequested) return;
                    status.Text = result.StartsWith("[x]", StringComparison.Ordinal) ? "Test failed" : "Responded";
                    status.ForeColor = result.StartsWith("[x]", StringComparison.Ordinal) ? Color.Firebrick : Color.ForestGreen;
                }
                catch (OperationCanceledException) { if (CanUpdate) status.Text = "Cancelled"; }
                catch (Exception e) { if (CanUpdate) { status.Text = "Test failed"; ShowError("Provider test failed. Check sign-in, model and saved endpoint, then try again.", e); } }
                finally
                {
                    running = null; cts.Dispose();
                    if (CanUpdate) test.Text = "Test";
                }
            };
        }
        var roleContent = DialogLayout.Column(); roleContent.Dock = DockStyle.Top; roles.Controls.Add(roleContent);
        roleContent.Controls.Add(DialogLayout.Text("Choose a provider for each AI role. Models can be left blank for the provider default."));
        roleContent.Controls.Add(DialogLayout.Text("None means no separate choice for that role, not a disabled action: Precise and Vision then use the Quick provider. Vision needs a provider that reads images; nothing is ever sent to a provider you did not choose. With every role set to None, nothing leaves this computer."));
        _quick = new RoleChoice("Quick edits", "Quick", settings.RoleQuick, providers);
        _precise = new RoleChoice("Precise polish", "Precise", settings.RolePrecise, providers);
        _vision = new RoleChoice("Vision", "Vision", settings.RoleVision, providers);
        roleContent.Controls.Add(_quick.Panel); roleContent.Controls.Add(_precise.Panel); roleContent.Controls.Add(_vision.Panel);
        roleContent.Controls.Add(DialogLayout.Text("Local / compatible server endpoint (optional)"));
        _endpoint = new TextBox { Name = "LocalEndpoint", AccessibleName = "Local or compatible server endpoint", Text = settings.EndpointLocal, Dock = DockStyle.Top, Margin = new Padding(3, 3, 3, 12) };
        roleContent.Controls.Add(_endpoint);
        roleContent.Controls.Add(DialogLayout.Text("Save an endpoint before testing it. A compatible server may be local or remote; selected text and images are sent to the endpoint you configure."));
        roleContent.Controls.Add(DialogLayout.Text("Save applies roles and the endpoint. Close discards those edits. API keys and vendor sign-ins take effect immediately and remain after Close."));
        _error = DialogLayout.Text(""); _error.Name = "ProviderError"; _error.ForeColor = Color.Firebrick; _error.Visible = false;
        shell.Controls.Add(_error, 0, 2);
        var buttons = new FlowLayoutPanel { AutoSize = true, Dock = DockStyle.Fill, FlowDirection = FlowDirection.RightToLeft };
        var close = DialogLayout.Button("Close", "CloseProviders"); close.DialogResult = DialogResult.Cancel;
        var saveButton = DialogLayout.Button("Save", "SaveProviders");
        saveButton.Click += (_, _) => { if (TrySave()) { DialogResult = DialogResult.OK; Close(); } };
        buttons.Controls.AddRange([close, saveButton]); shell.Controls.Add(buttons, 0, 3);
        AcceptButton = saveButton; CancelButton = close;
        Shown += (_, _) =>
        {
            foreach (var (provider, status) in statuses)
                if (provider.Id != "local") _workers.Add(RefreshStatus(provider, status));
        };
    }

    bool CanUpdate => !_closing && !IsDisposed && !Disposing;
    async Task RefreshStatus(IProvider provider, Label status)
    {
        var token = _lifetime.Token;
        try
        {
            var result = await Task.Run(() => { using var scope = OperationContext.Push(token); token.ThrowIfCancellationRequested(); return provider.Status(); });
            if (CanUpdate) status.Text = result;
        }
        catch (OperationCanceledException) { }
        catch { if (CanUpdate) status.Text = "Check sign-in or key"; }
    }

    internal bool TrySave()
    {
        var draft = _settings.Copy();
        draft.RoleQuick = _quick.Value; draft.RolePrecise = _precise.Value; draft.RoleVision = _vision.Value; draft.EndpointLocal = _endpoint.Text.Trim();
        try
        {
            _save(draft);
            _settings.RoleQuick = draft.RoleQuick; _settings.RolePrecise = draft.RolePrecise; _settings.RoleVision = draft.RoleVision; _settings.EndpointLocal = draft.EndpointLocal;
            return true;
        }
        catch (Exception e) { ShowError("Provider settings could not be saved. Check access to your settings folder and try again.", e); return false; }
    }
    void ShowError(string message, Exception e) { _error.Text = message; _error.Visible = true; Log.Error("settings", e); }

    static void InstallOrKey(IProvider provider)
    {
        var url = provider.Id switch
        {
            "claude-cli" => ClaudeCliProvider.InstallUrl, "codex-cli" => CodexCliProvider.InstallUrl,
            "gemini-cli" => GeminiCliProvider.InstallUrl, "anthropic" => AnthropicApiProvider.KeyUrl,
            "openai" => OpenAiApiProvider.KeyUrl, "gemini" => GeminiApiProvider.KeyUrl, _ => "",
        };
        if (url.Length > 0) Util.OpenUrl(url);
    }

    protected override void OnFormClosing(FormClosingEventArgs e)
    {
        base.OnFormClosing(e);
        if (!e.Cancel) { _closing = true; _lifetime.Cancel(); }
    }
    protected override void Dispose(bool disposing)
    {
        if (disposing && !_closing) { _closing = true; _lifetime.Cancel(); }
        if (disposing && !IsDisposed)
            _ = PendingWork.ContinueWith(_ => _lifetime.Dispose(), TaskScheduler.Default);
        base.Dispose(disposing);
    }

    sealed record ProviderOption(string Id, string Label) { public override string ToString() => Label; }
    sealed class RoleChoice
    {
        internal readonly TableLayoutPanel Panel;
        readonly ComboBox _provider;
        readonly TextBox _model;
        readonly string _original;
        readonly string _originalId;
        readonly string _originalModel;
        internal RoleChoice(string caption, string name, string spec, IProvider[] providers)
        {
            _original = spec;
            var colon = spec.IndexOf(':'); _originalId = (colon < 0 ? spec : spec[..colon]).Trim(); _originalModel = colon < 0 ? "" : spec[(colon + 1)..];
            Panel = DialogLayout.Column(); Panel.Dock = DockStyle.Top; Panel.Margin = new Padding(0, 0, 0, 10);
            Panel.Controls.Add(DialogLayout.Text(caption, true));
            _provider = new ComboBox { Name = name + "Provider", AccessibleName = caption + " provider", DropDownStyle = ComboBoxStyle.DropDownList, Dock = DockStyle.Top };
            _provider.Items.Add(new ProviderOption("", "None / not configured"));
            foreach (var p in providers) _provider.Items.Add(new ProviderOption(p.Id, p.Label));
            int selected = _provider.Items.Cast<ProviderOption>().ToList().FindIndex(x => x.Id.Equals(_originalId, StringComparison.OrdinalIgnoreCase));
            if (selected < 0) selected = _provider.Items.Add(new ProviderOption(_originalId, "Unavailable: " + _originalId));
            _provider.SelectedIndex = selected;
            _model = new TextBox { Name = name + "Model", AccessibleName = caption + " model", Text = _originalModel, PlaceholderText = "Model (blank = default)", Dock = DockStyle.Top, Margin = new Padding(3, 6, 3, 3), Enabled = selected != 0 };
            _provider.SelectedIndexChanged += (_, _) => _model.Enabled = _provider.SelectedIndex != 0;
            Panel.Controls.Add(_provider); Panel.Controls.Add(_model);
        }
        internal string Value
        {
            get
            {
                var id = ((ProviderOption)_provider.SelectedItem!).Id;
                if (id == _originalId && _model.Text == _originalModel) return _original;
                return id.Length == 0 ? "" : id + ":" + _model.Text.Trim();
            }
        }
    }
}

internal static class SettingsForm
{
    public static bool Show(Settings s)
    {
        using var f = Ui.Frame("Settings", 620, 500, false);
        var tabs = new TabControl { Left = 10, Top = 10, Width = 600, Height = 420, Font = Ui.Body };
        f.Controls.Add(tabs);

        // ---- General ----
        var gen = new TabPage("General"); tabs.TabPages.Add(gen);
        int y = 14;
        var cCtrl = new CheckBox { Text = "Ctrl + Right-click opens the " + Brand.Name + " menu", Left = 16, Top = y, AutoSize = true, Checked = s.TriggerCtrl }; y += 28;
        var cAlt = new CheckBox { Text = "Alt + Right-click opens the " + Brand.Name + " menu", Left = 16, Top = y, AutoSize = true, Checked = s.TriggerAlt }; y += 28;
        var cQuick = new CheckBox { Text = "Quick hotkeys (Ctrl+Alt+F menu, Ctrl+Alt+U/L/T/S/J/W/G/C/P)", Left = 16, Top = y, AutoSize = true, Checked = s.QuickHotkeys }; y += 28;
        var cPaste = new CheckBox { Text = "Ctrl+Shift+V pastes as plain text everywhere  (off: terminals keep their own paste)", Left = 16, Top = y, AutoSize = true, Checked = s.PastePlainHotkey }; y += 28;
        var cHist = new CheckBox { Text = "Keep clipboard history (last 30 text copies, memory only)", Left = 16, Top = y, AutoSize = true, Checked = s.ClipboardHistory }; y += 28;
        var cStart = new CheckBox { Text = "Start " + Brand.Name + " with Windows", Left = 16, Top = y, AutoSize = true, Checked = Startup.IsEnabled() }; y += 28;
        var cUpd = new CheckBox { Text = "Automatic update checks unavailable", Enabled = false, Left = 16, Top = y, AutoSize = true, Checked = s.UpdateCheck }; y += 36;
        gen.Controls.AddRange(new Control[] { cCtrl, cAlt, cQuick, cPaste, cHist, cStart, cUpd });
        gen.Controls.Add(Ui.Lbl("Notes folder for captures (optional subfolders):", 16, y)); y += 24;
        var eNc = new TextBox { Left = 16, Top = y, Width = 560, Text = s.NextcloudRoot }; gen.Controls.Add(eNc); y += 34;
        gen.Controls.Add(Ui.Lbl("Downloads folder (watched by the router):", 16, y)); y += 24;
        var eDl = new TextBox { Left = 16, Top = y, Width = 560, Text = s.DownloadsDir }; gen.Controls.Add(eDl); y += 34;
        gen.Controls.Add(Ui.Lbl("Screenshot folder (Snip -> save PNG):", 16, y)); y += 24;
        var eSs = new TextBox { Left = 16, Top = y, Width = 560, Text = s.ScreenshotDir }; gen.Controls.Add(eSs); y += 34;
        var cRouter = new CheckBox { Text = "Download router ON - offer to file new downloads", Left = 16, Top = y, AutoSize = true, Checked = s.RouterEnabled };
        gen.Controls.Add(cRouter);

        // ---- Integrations ----
        var integ = new TabPage("Integrations"); tabs.TabPages.Add(integ);
        y = 14;
        integ.Controls.Add(new Label { Text = "ntfy push notifications (phone)", Left = 16, Top = y, AutoSize = true, Font = new Font(Ui.Body, FontStyle.Bold) }); y += 26;
        integ.Controls.Add(Ui.Lbl("Server URL:", 16, y + 4)); var eNtfy = new TextBox { Left = 140, Top = y, Width = 436, Text = s.NtfyUrl, PlaceholderText = "https://ntfy.sh or your own" }; integ.Controls.Add(eNtfy); y += 32;
        integ.Controls.Add(Ui.Lbl("Topic:", 16, y + 4)); var eTopic = new TextBox { Left = 140, Top = y, Width = 240, Text = s.NtfyTopic }; integ.Controls.Add(eTopic);
        var bNtfyTok = Ui.Btn(Credentials.Has("ntfy") ? "Token set..." : "Token...", 120); bNtfyTok.Left = 456; bNtfyTok.Top = y - 3; integ.Controls.Add(bNtfyTok);
        bNtfyTok.Click += (_, _) => { var k = InputDialog.Show("ntfy", "Access token (optional, stored in Credential Manager):", "", false, true); if (k != null) { Credentials.Set("ntfy", k.Trim()); bNtfyTok.Text = k.Trim().Length > 0 ? "Token set..." : "Token..."; } };
        y += 44;
        integ.Controls.Add(new Label { Text = "Paperless-ngx", Left = 16, Top = y, AutoSize = true, Font = new Font(Ui.Body, FontStyle.Bold) }); y += 26;
        integ.Controls.Add(Ui.Lbl("Server URL:", 16, y + 4)); var ePl = new TextBox { Left = 140, Top = y, Width = 300, Text = s.PaperlessUrl, PlaceholderText = "https://paperless.example.com" }; integ.Controls.Add(ePl);
        var bPlTok = Ui.Btn(Credentials.Has("paperless") ? "Token set..." : "Token...", 120); bPlTok.Left = 456; bPlTok.Top = y - 3; integ.Controls.Add(bPlTok);
        bPlTok.Click += (_, _) => { var k = InputDialog.Show("Paperless", "API token (stored in Credential Manager):", "", false, true); if (k != null) { Credentials.Set("paperless", k.Trim()); bPlTok.Text = k.Trim().Length > 0 ? "Token set..." : "Token..."; } };
        y += 44;
        integ.Controls.Add(new Label { Text = "SSH fleet (Fleet: run command)", Left = 16, Top = y, AutoSize = true, Font = new Font(Ui.Body, FontStyle.Bold) }); y += 26;
        integ.Controls.Add(Ui.Lbl("Hosts as alias:description; alias:description  (ssh config aliases):", 16, y)); y += 24;
        var eFleet = new TextBox { Left = 16, Top = y, Width = 560, Text = s.FleetHosts }; integ.Controls.Add(eFleet); y += 40;
        integ.Controls.Add(new Label { Text = "Anything else (webhooks, Obsidian, Slack, Home Assistant...) is a custom spell: My spells -> Edit spells file.", Left = 16, Top = y, Width = 560, Height = 44, ForeColor = Color.DimGray, Font = Ui.Small });

        // ---- Style ----
        var st = new TabPage("Style"); tabs.TabPages.Add(st);
        st.Controls.Add(new Label { Text = "House-style profiles injected into styled AI edits (auto-picked by code vs prose):", Left = 16, Top = 14, Width = 560, Height = 40 });
        var bP = Ui.Btn("Edit prose style", 220); bP.Left = 16; bP.Top = 60; bP.Click += (_, _) => Util.OpenInEditor(Paths.StyleProse);
        var bC = Ui.Btn("Edit code style", 220); bC.Left = 16; bC.Top = 100; bC.Click += (_, _) => Util.OpenInEditor(Paths.StyleCode);
        var bD = Ui.Btn("Open data folder", 220); bD.Left = 16; bD.Top = 160; bD.Click += (_, _) => Util.OpenUrl(Paths.DataDir);
        st.Controls.AddRange(new Control[] { bP, bC, bD });
        st.Controls.Add(new Label { Text = "Data folder: " + Paths.DataDir, Left = 16, Top = 204, Width = 560, Height = 40, ForeColor = Color.DimGray });

        // ---- Recipes ----
        var rc = new TabPage("Recipes"); tabs.TabPages.Add(rc);
        rc.Controls.Add(Ui.Lbl("Recipes (steps run left to right; steps can be transforms, AI steps, or spell names):", 16, 14));
        var lb = new ListBox { Left = 16, Top = 40, Width = 420, Height = 300, IntegralHeight = false }; rc.Controls.Add(lb);
        void RefreshR() { lb.Items.Clear(); foreach (var r in Recipes.All()) lb.Items.Add(r.Name); }
        RefreshR();
        var bNew = Ui.Btn("New", 120); bNew.Left = 450; bNew.Top = 40;
        var bEd = Ui.Btn("Edit", 120); bEd.Left = 450; bEd.Top = 80;
        var bDel = Ui.Btn("Delete", 120); bDel.Left = 450; bDel.Top = 120;
        var bFile = Ui.Btn("Edit file", 120); bFile.Left = 450; bFile.Top = 180;
        bNew.Click += (_, _) => { RecipeEditor.Edit(""); RefreshR(); };
        bEd.Click += (_, _) => { if (lb.SelectedItem is string n) { RecipeEditor.Edit(n); RefreshR(); } else Toast.Show("Pick a recipe to edit", 2500); };
        bDel.Click += (_, _) => { if (lb.SelectedItem is string n) { Recipes.Delete(n); RefreshR(); Toast.Show("Deleted recipe '" + n + "'", 2500); } else Toast.Show("Pick a recipe to delete", 2500); };
        bFile.Click += (_, _) => Util.OpenInEditor(Paths.Recipes);
        rc.Controls.AddRange(new Control[] { bNew, bEd, bDel, bFile });

        // ---- Spells ----
        var sp = new TabPage("Spells"); tabs.TabPages.Add(sp);
        sp.Controls.Add(new Label { Text = "Create, find, edit, duplicate and remove AI prompts, shell commands and HTTP requests in My spells.\n\nSpell changes save immediately in their own window. Cancelling Settings does not undo saved spell changes.", Left = 16, Top = 16, Width = 550, Height = 110 });
        var manageSpells = Ui.Btn("Manage spells…", 170); manageSpells.Left = 16; manageSpells.Top = 140;
        manageSpells.Click += (_, _) => SpellManager.Show(); sp.Controls.Add(manageSpells);

        var save = Ui.Btn("Save"); var cancel = Ui.Btn("Cancel");
        save.Left = 390; cancel.Left = 500; save.Top = cancel.Top = 442;
        save.DialogResult = DialogResult.OK; cancel.DialogResult = DialogResult.Cancel;
        f.AcceptButton = save; f.CancelButton = cancel;
        f.Controls.AddRange(new Control[] { save, cancel });
        f.ClientSize = new Size(620, save.Bottom + 12);
        if (f.ShowDialog() != DialogResult.OK) return false;

        s.TriggerCtrl = cCtrl.Checked; s.TriggerAlt = cAlt.Checked; s.QuickHotkeys = cQuick.Checked; s.PastePlainHotkey = cPaste.Checked; s.ClipboardHistory = cHist.Checked; s.UpdateCheck = cUpd.Checked;
        s.NextcloudRoot = eNc.Text.Trim(); s.DownloadsDir = eDl.Text.Trim(); s.ScreenshotDir = eSs.Text.Trim(); s.RouterEnabled = cRouter.Checked;
        s.NtfyUrl = eNtfy.Text.Trim(); s.NtfyTopic = eTopic.Text.Trim(); s.PaperlessUrl = ePl.Text.Trim(); s.FleetHosts = eFleet.Text.Trim();
        try { Startup.Set(cStart.Checked); } catch (Exception e) { Toast.Show("[x] startup: " + e.Message, 4000); }
        s.Save();
        return true;
    }
}

internal static class RecipeEditor
{
    public static void Edit(string name)
    {
        const string avail = "Transforms: clean, join, title, sentence, upper, lower, curly, straight, emdash, ellipsis, deaccent, dblspace, slug, snake, kebab, constant, sortaz, unique, trimlines, numberlines, b64enc, urlenc, ...\nAI: grammar, tighten, clarity, headline, housestyle   -   or the name of one of your spells";
        var cur = name.Length > 0 ? Recipes.Steps(name) : "clean,grammar,curly";
        var steps = InputDialog.Show("Recipe steps", "Steps in order, comma-separated:\n\n" + avail, cur, true);
        if (string.IsNullOrWhiteSpace(steps)) return;
        steps = string.Join(",", steps.Split(',').Select(x => x.Trim()).Where(x => x.Length > 0));
        var newName = InputDialog.Show("Recipe name", "Recipe name:", name);
        if (string.IsNullOrWhiteSpace(newName)) return;
        Recipes.Save(name, newName.Trim(), steps);
        Toast.Show("Saved recipe '" + newName.Trim() + "'", 3000);
    }
}

/// <summary>Draws the tray/app icon at runtime (an open book on a disc) so no asset files are needed.</summary>
internal static class IconFactory
{
    public static Icon Make(int size = 32)
    {
        using var bmp = new Bitmap(size, size);
        using (var g = Graphics.FromImage(bmp))
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.Clear(Color.Transparent);
            float s = size / 32f;
            using var back = new SolidBrush(Color.FromArgb(Brand.AccentArgb));
            using var page = new SolidBrush(Color.FromArgb(245, 240, 225));
            using var ink = new Pen(Color.FromArgb(120, 100, 80), 1.2f * s);
            g.FillEllipse(back, 0, 0, size - 1, size - 1);
            var left = new PointF[] { new(6 * s, 9 * s), new(15.5f * s, 11 * s), new(15.5f * s, 24 * s), new(6 * s, 22 * s) };
            var right = new PointF[] { new(26 * s, 9 * s), new(16.5f * s, 11 * s), new(16.5f * s, 24 * s), new(26 * s, 22 * s) };
            g.FillPolygon(page, left); g.FillPolygon(page, right);
            for (int i = 0; i < 3; i++)
            {
                float yy = (13.5f + i * 3.2f) * s;
                g.DrawLine(ink, 8 * s, yy, 13.5f * s, yy + 0.6f * s);
                g.DrawLine(ink, 18.5f * s, yy + 0.6f * s, 24 * s, yy);
            }
        }
        var h = bmp.GetHicon();
        try { return (Icon)Icon.FromHandle(h).Clone(); }
        finally { Native.DestroyIcon(h); }
    }
}
