namespace Spellbook;

/// <summary>The real first-run surface. Dependencies are injectable for offline Windows tests.</summary>
internal sealed class SetupWizard : Form
{
    readonly Settings _settings;
    readonly Action<bool> _setStartup;
    readonly Action<Settings> _save;
    readonly bool _initialStartup;
    readonly CheckBox _ctrl, _alt, _quick, _paste, _history, _startup, _updates;
    readonly TableLayoutPanel[] _pages;
    readonly Label _progress, _error;
    readonly Button _back, _next;
    int _page;

    public SetupWizard(Settings settings, Func<bool>? readStartup = null, Action<bool>? setStartup = null,
        Action<Settings>? configureProviders = null, Action<Settings>? save = null)
    {
        _settings = settings;
        _setStartup = setStartup ?? Startup.Set;
        _save = save ?? (s => s.Save());
        _initialStartup = (readStartup ?? Startup.IsEnabled)();
        DialogLayout.Frame(this, "Set up " + Brand.Name, new Size(660, 580), new Size(600, 520));
        var shell = DialogLayout.Column(); shell.Dock = DockStyle.Fill; shell.Padding = new Padding(22);
        shell.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        shell.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        shell.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        shell.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        Controls.Add(shell);
        _progress = DialogLayout.Text("", true); _progress.Name = "Progress"; shell.Controls.Add(_progress, 0, 0);
        var body = new Panel { Dock = DockStyle.Fill, AutoScroll = true, Margin = new Padding(0, 16, 0, 8) };
        shell.Controls.Add(body, 0, 1);
        _pages = [DialogLayout.Column(), DialogLayout.Column(), DialogLayout.Column()];
        foreach (var page in _pages) { page.Dock = DockStyle.Top; body.Controls.Add(page); }
        Add(0, DialogLayout.Text("Your everyday text tools are ready", true));
        Add(0, DialogLayout.Text("Change case, clean whitespace, sort lines and use snippets on this PC. These local tools need no account, API key or server."));
        Add(0, DialogLayout.Text("Highlight text, then Ctrl + right-click or Alt + right-click to open Grimoire. You can also open the menu from its tray icon."));
        Add(0, DialogLayout.Text("Setup takes three short steps. Choose Not now at any time and keep using the tray menu; return with Set up Grimoire."));
        Add(1, DialogLayout.Text("AI is optional", true));
        Add(1, DialogLayout.Text("AI edits send the selected text or image to the provider you choose. Use an existing supported account, an API key, or your own compatible server. Accounts and usage may have provider costs."));
        Add(1, DialogLayout.Text("You can skip this step and use local tools immediately. Grimoire will not test a provider from this wizard."));
        var providers = DialogLayout.Button("Configure AI providers…", "ConfigureProviders");
        providers.Click += (_, _) => { if (configureProviders != null) configureProviders(settings); else { using var f = ProvidersForm.Create(settings); f.ShowDialog(this); } };
        Add(1, providers);
        Add(1, DialogLayout.Text("Provider settings you explicitly save, API keys you set, and vendor sign-ins remain even if you cancel setup."));
        Add(2, DialogLayout.Text("Make yourself at home", true));
        _ctrl = Choice("Ctrl + right-click opens the menu", "CtrlChord", settings.TriggerCtrl);
        _alt = Choice("Alt + right-click opens the menu", "AltChord", settings.TriggerAlt);
        _quick = Choice("Quick hotkeys (Ctrl+Alt+F for menu; U/L/T and more)", "QuickHotkeys", settings.QuickHotkeys);
        _paste = Choice("Ctrl+Shift+V pastes plain text", "PlainPaste", settings.PastePlainHotkey);
        _history = Choice("Keep the last 30 text copies in memory on this PC", "History", settings.ClipboardHistory);
        _startup = Choice("Start Grimoire with Windows", "Startup", _initialStartup);
        _updates = Choice("Automatic update checks unavailable", "UpdateCheck", settings.UpdateCheck);
        _updates.Enabled = false;
        var downloads = DialogLayout.Button("View releases…", "ViewReleases");
        downloads.Click += (_, _) => Updater.ViewReleases();
        Add(2, downloads);
        Add(2, DialogLayout.Text("Clipboard history is optional and is cleared when Grimoire exits. Preferences are applied when you finish. You can change them in Settings."));
        Add(2, DialogLayout.Text("Add starter spells later from My spells → Starter spells. Local tools are already available from the tray menu."));
        _error = DialogLayout.Text(""); _error.ForeColor = Color.Firebrick; _error.Visible = false; _error.Name = "SetupError";
        shell.Controls.Add(_error, 0, 2);
        var buttons = new FlowLayoutPanel { AutoSize = true, Dock = DockStyle.Fill, FlowDirection = FlowDirection.RightToLeft, WrapContents = true };
        _next = DialogLayout.Button("Next", "Next"); _back = DialogLayout.Button("Back", "Back");
        var cancel = DialogLayout.Button("Not now", "NotNow"); cancel.DialogResult = DialogResult.Cancel;
        buttons.Controls.AddRange([_next, _back, cancel]); shell.Controls.Add(buttons, 0, 3);
        _next.Click += (_, _) => { if (_page < 2) ShowPage(_page + 1); else if (TryFinish()) { DialogResult = DialogResult.OK; Close(); } };
        _back.Click += (_, _) => ShowPage(_page - 1);
        AcceptButton = _next; CancelButton = cancel;
        ShowPage(0);
    }

    void Add(int page, Control control) => _pages[page].Controls.Add(control);
    CheckBox Choice(string text, string name, bool value)
    {
        var c = new CheckBox { Text = text, Name = name, AccessibleName = text, Checked = value, AutoSize = true, Dock = DockStyle.Top, Margin = new Padding(3, 4, 3, 4) };
        Add(2, c); return c;
    }

    internal void ShowPage(int page)
    {
        _page = Math.Clamp(page, 0, 2);
        for (int i = 0; i < _pages.Length; i++) _pages[i].Visible = i == _page;
        _progress.Text = $"Step {_page + 1} of 3 · " + new[] { "Welcome", "Optional AI", "Preferences" }[_page];
        _back.Enabled = _page > 0;
        _next.Text = _page == 2 ? "Finish" : _page == 1 ? "Skip / Next" : "Next";
        _next.Focus();
    }

    internal bool TryFinish()
    {
        var draft = _settings.Copy();
        draft.TriggerCtrl = _ctrl.Checked; draft.TriggerAlt = _alt.Checked; draft.QuickHotkeys = _quick.Checked;
        draft.PastePlainHotkey = _paste.Checked; draft.ClipboardHistory = _history.Checked; draft.UpdateCheck = _updates.Checked;
        draft.FirstRunDone = true;
        bool startupChanged = _startup.Checked != _initialStartup;
        try
        {
            if (startupChanged) _setStartup(_startup.Checked);
            _save(draft);
            _settings.TriggerCtrl = draft.TriggerCtrl; _settings.TriggerAlt = draft.TriggerAlt; _settings.QuickHotkeys = draft.QuickHotkeys;
            _settings.PastePlainHotkey = draft.PastePlainHotkey; _settings.ClipboardHistory = draft.ClipboardHistory;
            _settings.UpdateCheck = draft.UpdateCheck; _settings.FirstRunDone = true;
            return true;
        }
        catch (Exception e)
        {
            _error.Text = "Setup could not be saved. Check access to your settings folder and try again.";
            if (startupChanged)
                try { _setStartup(_initialStartup); }
                catch { _error.Text += " The Windows startup choice could not be restored; check it in Settings."; }
            _error.Visible = true;
            Log.Error("settings", e);
            return false;
        }
    }
}

/// <summary>Flow-based native layout shared by setup and provider surfaces.</summary>
internal static class DialogLayout
{
    internal static void Frame(Form f, string title, Size size, Size minimum)
    {
        f.Text = Brand.Name + " - " + title; f.Font = new Font("Segoe UI", 10f);
        f.AutoScaleDimensions = new SizeF(96, 96); f.AutoScaleMode = AutoScaleMode.Dpi;
        f.ClientSize = size; f.MinimumSize = minimum; f.StartPosition = FormStartPosition.CenterScreen;
        f.MinimizeBox = false; f.Icon = TrayApp.AppIcon;
    }
    internal static TableLayoutPanel Column()
    {
        var panel = new TableLayoutPanel { ColumnCount = 1, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, GrowStyle = TableLayoutPanelGrowStyle.AddRows, Margin = Padding.Empty };
        panel.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        return panel;
    }
    internal static Label Text(string text, bool heading = false) => new()
    {
        Text = text, AutoSize = true, Dock = DockStyle.Top, Margin = new Padding(3, 6, 3, 10),
        Font = new Font("Segoe UI", heading ? 12f : 10f, heading ? FontStyle.Bold : FontStyle.Regular),
        UseMnemonic = false,
    };
    internal static Button Button(string text, string name) => new()
    {
        Text = text, Name = name, AccessibleName = text, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink,
        MinimumSize = new Size(94, 34), Padding = new Padding(8, 3, 8, 3), Margin = new Padding(4),
    };
}
