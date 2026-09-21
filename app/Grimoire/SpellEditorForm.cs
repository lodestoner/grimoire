namespace Spellbook;

/// <summary>The production editor; injected store/credentials/execution keep UI fixtures offline.</summary>
internal sealed class SpellEditorDialog : Form
{
    readonly SpellStore _store;
    readonly Spell? _original;
    readonly Spell _initial;
    readonly ISpellCredentials _credentials;
    readonly Func<Spell, string, string?, string> _run;
    readonly Func<bool> _aiConfigured;
    readonly bool _syntheticRun;
    readonly TextBox _name, _prompt, _command, _url, _headers, _body, _reference, _secret, _header, _scheme, _sample, _file, _output;
    readonly ComboBox _kind, _result, _style, _role, _method;
    readonly CheckBox _files, _show, _advanced, _try;
    readonly TableLayoutPanel _promptPanel, _commandPanel, _httpPanel, _advancedPanel, _testPanel;
    readonly Label _error = DialogLayout.Text("");
    readonly Button _test = DialogLayout.Button("Run test", "Test"), _cancelTest = DialogLayout.Button("Cancel test", "CancelTest"), _save = DialogLayout.Button("Save spell", "Save");
    CancellationTokenSource? _testCancellation;
    internal Task PendingWork { get; private set; } = Task.CompletedTask;

    internal SpellEditorDialog(SpellStore store, Spell? original, Spell? initial, ISpellCredentials? credentials,
        Func<Spell, string, string?, string>? run, Func<bool>? aiConfigured)
    {
        _store = store; _original = original; _credentials = credentials ?? new SpellCredentials(); _syntheticRun = run != null;
        _run = run ?? ((s, text, file) => file == null ? Spells.RunText(s, text) : Spells.RunFile(s, file));
        var value = initial ?? original ?? new Spell("", "prompt", "", "review", "none", "quick", "", "https://", "POST", "Content-Type: application/json", "{\"text\": {json}}", false, false);
        _initial = value;
        DialogLayout.Frame(this, original == null ? "New spell" : "Edit spell", new(780, 760), new(580, 470));
        var shell = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 3, Padding = new Padding(16) };
        shell.ColumnStyles.Add(new(SizeType.Percent, 100)); shell.RowStyles.Add(new(SizeType.Percent, 100)); shell.RowStyles.Add(new(SizeType.AutoSize)); shell.RowStyles.Add(new(SizeType.AutoSize)); Controls.Add(shell);
        var scroll = new Panel { Dock = DockStyle.Fill, AutoScroll = true }; var column = DialogLayout.Column(); column.Dock = DockStyle.Top; scroll.Controls.Add(column); shell.Controls.Add(scroll, 0, 0);
        Add(column, DialogLayout.Text(original == null ? "Create a spell" : "Edit your spell", true));
        _name = TextField(column, "Name in My spells", "SpellName", value.Name);
        _kind = Choice(column, "Type", "Kind", ["AI prompt", "Shell command", "HTTP request"], value.Type == "command" ? 1 : value.Type == "http" ? 2 : 0);
        _files = Check(column, "Run once per selected file", "Files", value.Files);
        _promptPanel = Section(column);
        _prompt = TextField(_promptPanel, "Instruction · Selected text is appended, or use {text} in your prompt.", "Prompt", value.Prompt, 140);
        _commandPanel = Section(column);
        Add(_commandPanel, DialogLayout.Text("Runs through cmd.exe under your Windows account. Text arrives on stdin and in SPELL_TEXT. Commands can change files and use the network."));
        _command = TextField(_commandPanel, "Shell command", "Command", value.Command, 105);
        Add(_commandPanel, DialogLayout.Text("For files, use {file} as a separate argument (quotes are added safely), or read the SPELL_FILE environment variable in your script. Avoid embedding paths in shell source."));
        _httpPanel = Section(column);
        _url = TextField(_httpPanel, "Request URL · Do not put credentials in the URL or body.", "Url", value.Url);
        _method = Choice(_httpPanel, "Method", "Method", ["POST", "PUT", "PATCH", "GET"], Array.IndexOf(new[] { "POST", "PUT", "PATCH", "GET" }, value.Method));
        _headers = TextField(_httpPanel, "Non-secret headers · One Key: value per line", "Headers", value.Headers.Replace("|", Environment.NewLine), 65);
        _body = TextField(_httpPanel, "Body · {text}, {json}, {file}, {filename}, or {filedata} for file bytes", "Body", value.Body, 85);
        _show = Check(_httpPanel, "Show the server reply", "ShowResponse", value.ShowResponse);
        _result = Choice(column, "Result", "Result", ["Review before replacing", "Replace selection", "Show reply"], value.Mode == "replace" ? 1 : value.Mode == "show" ? 2 : 0);
        _advanced = Check(column, "Advanced options", "Advanced", false);
        _advancedPanel = Section(column);
        _style = Choice(_advancedPanel, "Writing style (AI only)", "Style", ["None", "Automatic", "Prose", "Code"], Array.IndexOf(new[] { "none", "auto", "prose", "code" }, value.Style));
        _role = Choice(_advancedPanel, "AI provider role", "Role", ["Quick", "Precise"], value.Role == "precise" ? 1 : 0);
        _aiConfigured = aiConfigured ?? (() => { try { Ai.Resolve(_role.SelectedIndex == 1 ? AiRole.Precise : AiRole.Quick); return true; } catch { return false; } });
        var credentialPanel = Section(_advancedPanel); credentialPanel.Name = "CredentialPanel";
        Add(credentialPanel, DialogLayout.Text("Optional HTTP credential · Only its name is saved in spells.ini. A blank value reuses the named credential. A new value needs a new name. Values are never loaded into this editor."));
        _reference = TextField(credentialPanel, "Credential name", "CredentialReference", value.CredentialReference);
        _secret = TextField(credentialPanel, "New credential value (saved to Windows Credential Manager with Save spell)", "CredentialValue", ""); _secret.UseSystemPasswordChar = true;
        _header = TextField(credentialPanel, "Credential header", "CredentialHeader", value.CredentialHeader);
        _scheme = TextField(credentialPanel, "Prefix · Bearer, or blank for a raw key", "CredentialScheme", value.CredentialScheme);
        var removeReference = DialogLayout.Button("Remove reference", "RemoveCredential"); Add(credentialPanel, removeReference);
        Add(credentialPanel, DialogLayout.Text("Remove reference detaches this spell. Shared stored credentials remain in Windows Credential Manager (Grimoire/spells/name), where you can delete them explicitly."));
        removeReference.Click += (_, _) => { _reference.Clear(); _secret.Clear(); };
        _try = Check(column, "Try this spell", "Try", false); _testPanel = Section(column);
        Add(_testPanel, DialogLayout.Text("Run test is an explicit action. AI sends the sample to your configured provider; HTTP sends it to the URL above; shell commands run locally with your account's permissions. Tests may have effects that Cancel cannot undo."));
        _sample = TextField(_testPanel, "Sample text", "Sample", "The quick brown fox jumps over the lazy dog.", 55);
        _file = TextField(_testPanel, "File test · Choose a disposable fixture you are comfortable sending or changing.", "TestFile", "");
        var browse = DialogLayout.Button("Choose test file…", "ChooseFile"); Add(_testPanel, browse);
        browse.Click += (_, _) => { using var dialog = new OpenFileDialog { Title = "Choose a disposable test file", CheckFileExists = true }; if (dialog.ShowDialog(this) == DialogResult.OK) _file.Text = dialog.FileName; };
        var testButtons = new FlowLayoutPanel { AutoSize = true, Dock = DockStyle.Top }; testButtons.Controls.AddRange([_test, _cancelTest]); Add(_testPanel, testButtons); _cancelTest.Enabled = false;
        _output = TextField(_testPanel, "Test result", "TestOutput", "", 100); _output.ReadOnly = true;
        _test.Click += (_, _) => { PendingWork = RunTestAsync(); }; _cancelTest.Click += (_, _) => _testCancellation?.Cancel();
        _error.Name = "Error"; _error.ForeColor = Color.Firebrick; _error.Visible = false; shell.Controls.Add(_error, 0, 1);
        var buttons = new FlowLayoutPanel { Dock = DockStyle.Fill, AutoSize = true, FlowDirection = FlowDirection.RightToLeft };
        var cancel = DialogLayout.Button("Cancel", "Cancel"); cancel.DialogResult = DialogResult.Cancel;
        buttons.Controls.AddRange([_save, cancel]); shell.Controls.Add(buttons, 0, 2); CancelButton = cancel;
        _save.Click += (_, _) => { if (TrySave()) { DialogResult = DialogResult.OK; Close(); } };
        void ShowFields()
        {
            bool prompt = _kind.SelectedIndex == 0, http = _kind.SelectedIndex == 2;
            _promptPanel.Visible = prompt; _commandPanel.Visible = _kind.SelectedIndex == 1; _httpPanel.Visible = http;
            _advancedPanel.Visible = _advanced.Checked; credentialPanel.Visible = http;
            _style.Visible = _role.Visible = prompt;
            foreach (var label in _advancedPanel.Controls.OfType<Label>()) label.Visible = prompt;
            _advanced.Visible = prompt || http;
            if (!prompt && !http) _advancedPanel.Visible = false;
            _testPanel.Visible = _try.Checked; _sample.Visible = !_files.Checked; _file.Visible = browse.Visible = _files.Checked;
            foreach (var label in _testPanel.Controls.OfType<Label>())
                if (label.Tag is string field) label.Visible = field == "Sample" ? !_files.Checked : field != "TestFile" || _files.Checked;
            _body.Enabled = _method.SelectedItem?.ToString() != "GET";
            _result.Enabled = !http || _show.Checked;
        }
        _kind.SelectedIndexChanged += (_, _) => ShowFields(); _advanced.CheckedChanged += (_, _) => ShowFields(); _try.CheckedChanged += (_, _) => ShowFields(); _files.CheckedChanged += (_, _) => ShowFields(); _show.CheckedChanged += (_, _) => ShowFields(); _method.SelectedIndexChanged += (_, _) => ShowFields();
        ShowFields(); Shown += (_, _) => _name.Focus();
    }
    static void Add(TableLayoutPanel panel, Control control) => panel.Controls.Add(control);
    static TableLayoutPanel Section(TableLayoutPanel parent) { var section = DialogLayout.Column(); section.Dock = DockStyle.Top; Add(parent, section); return section; }
    static TextBox TextField(TableLayoutPanel parent, string caption, string name, string value, int height = 0)
    {
        var label = DialogLayout.Text(caption); label.Tag = name; Add(parent, label);
        var text = new TextBox { Name = name, AccessibleName = caption, Text = value, Dock = DockStyle.Top, Multiline = height > 0, AcceptsReturn = height > 0, ScrollBars = height > 0 ? ScrollBars.Vertical : ScrollBars.None, Margin = new Padding(3, 0, 3, 6) };
        if (height > 0) text.Height = height; Add(parent, text); return text;
    }
    static ComboBox Choice(TableLayoutPanel parent, string caption, string name, string[] values, int index)
    {
        Add(parent, DialogLayout.Text(caption)); var box = new ComboBox { Name = name, AccessibleName = caption, Dock = DockStyle.Top, DropDownStyle = ComboBoxStyle.DropDownList, Margin = new Padding(3, 0, 3, 6) };
        box.Items.AddRange(values); box.SelectedIndex = Math.Max(0, index); Add(parent, box); return box;
    }
    static CheckBox Check(TableLayoutPanel parent, string caption, string name, bool value)
    {
        var box = new CheckBox { Name = name, AccessibleName = caption, Text = caption, Checked = value, AutoSize = true, Dock = DockStyle.Top, Margin = new Padding(3, 10, 3, 10) }; Add(parent, box); return box;
    }
    // WinForms may normalize line endings. An untouched control preserves the original bytes/value.
    static string Field(TextBox box, string original, string? display = null) =>
        box.Text.Replace("\r\n", "\n") == (display ?? original).Replace("\r\n", "\n") ? original : box.Text;
    internal Spell Build() => new(_name.Text, new[] { "prompt", "command", "http" }[_kind.SelectedIndex], Field(_prompt, _initial.Prompt),
        new[] { "review", "replace", "show" }[_result.SelectedIndex], new[] { "none", "auto", "prose", "code" }[_style.SelectedIndex], _role.SelectedIndex == 1 ? "precise" : "quick",
        Field(_command, _initial.Command), Field(_url, _initial.Url), _method.SelectedItem!.ToString()!, Field(_headers, _initial.Headers, _initial.Headers.Replace("|", Environment.NewLine)), Field(_body, _initial.Body), _show.Checked, _files.Checked,
        _kind.SelectedIndex == 2 ? _reference.Text.Trim() : "", _header.Text.Trim(), _scheme.Text.Trim());
    bool Error(string text) { _error.Text = text; _error.Visible = true; return false; }
    internal bool TrySave()
    {
        var spell = Build(); var error = SpellStore.Validate(spell);
        if (error != null) return Error(error);
        bool createdCredential = false;
        try
        {
            if (spell.Type == "http")
            {
                if (_secret.Text.Length > 0)
                {
                    if (spell.CredentialReference.Length == 0) return Error("Give the new credential a name, or clear the credential value.");
                    if (_credentials.Get(spell.CredentialReference) != null) return Error("That credential name already exists. Reuse it with a blank value, or choose a new name.");
                    _credentials.Set(spell.CredentialReference, _secret.Text); createdCredential = true;
                }
                else if (spell.CredentialReference.Length > 0 && _credentials.Get(spell.CredentialReference) == null)
                    return Error("That credential was not found. Enter a new value or choose an existing credential name.");
            }
            _store.Save(spell, _original); _secret.Clear(); return true;
        }
        catch (InvalidOperationException e)
        {
            if (createdCredential) RollbackCredential(spell.CredentialReference);
            // Only store validation uses safe user-facing messages; native/credential errors stay generic.
            return Error(e.Message.StartsWith("A spell with", StringComparison.Ordinal) || e.Message.StartsWith("This spell changed", StringComparison.Ordinal)
                ? e.Message : "Could not save the spell. Check file access and Credential Manager, then try again.");
        }
        catch
        {
            if (createdCredential) RollbackCredential(spell.CredentialReference);
            return Error("Could not save the spell. Your previous spells were kept. Check access to the data folder.");
        }
    }
    void RollbackCredential(string name) { try { _credentials.Delete(name); } catch { /* A failed cleanup leaves an unused named credential, never an ini secret. */ } }
    async Task RunTestAsync()
    {
        if (_testCancellation != null) return;
        // Capture controls on the owner thread before scheduling any work.
        var spell = Build(); string sample = _sample.Text; string? file = spell.Files ? _file.Text : null;
        var error = SpellStore.Validate(spell); if (error != null) { Error(error); return; }
        if (spell.Type == "prompt" && !_aiConfigured()) { Error("No AI provider is configured. Open Providers from the menu, choose a provider role, then try again."); return; }
        if (spell.Files && (string.IsNullOrWhiteSpace(file) || !File.Exists(file))) { Error("Choose an existing disposable test file first. The test runs once on that file."); return; }
        if (_secret.Text.Length > 0) { Error("Save the new credential before testing, or clear its value to use an existing reference."); return; }
        if (!_syntheticRun && spell.Type != "prompt" && MessageBox.Show(this,
            spell.Type == "command" ? "Run the displayed command now? It runs with your Windows account's permissions and may change files or use the network." : "Send the displayed HTTP request now? It may change server data. Check the URL, method, headers and body above.",
            "Run spell test", MessageBoxButtons.OKCancel, MessageBoxIcon.Warning, MessageBoxDefaultButton.Button2) != DialogResult.OK) return;
        var cts = new CancellationTokenSource(); _testCancellation = cts;
        _test.Enabled = _save.Enabled = false; _cancelTest.Enabled = true; _error.Visible = false; _output.Text = "Running…";
        try
        {
            var result = await Task.Run(() => { using (OperationContext.Push(cts.Token)) return _run(spell, sample, file); });
            if (!IsDisposed && !Disposing) _output.Text = cts.IsCancellationRequested ? "Test cancelled." : result;
        }
        catch (OperationCanceledException) { if (!IsDisposed && !Disposing) _output.Text = "Test cancelled."; }
        catch { if (!IsDisposed && !Disposing) _output.Text = "Test failed. Check the spell, provider configuration and destination. Diagnostic details are not shown here because they may contain private data."; }
        finally
        {
            _testCancellation = null; cts.Dispose();
            if (!IsDisposed && !Disposing) { _test.Enabled = _save.Enabled = true; _cancelTest.Enabled = false; }
        }
    }
    protected override void OnFormClosing(FormClosingEventArgs e) { _testCancellation?.Cancel(); base.OnFormClosing(e); }
    protected override void Dispose(bool disposing) { if (disposing) _testCancellation?.Cancel(); base.Dispose(disposing); }
}
