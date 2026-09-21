namespace Spellbook;

internal static class SpellEditor
{
    public static bool Edit(string name)
    {
        var store = Spells.Store;
        var original = name.Length == 0 ? null : store.All().FirstOrDefault(s => s.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
        using var form = Create(store, original);
        return form.ShowDialog() == DialogResult.OK;
    }
    internal static SpellEditorDialog Create(SpellStore store, Spell? original = null, Spell? initial = null,
        ISpellCredentials? credentials = null, Func<Spell, string, string?, string>? run = null, Func<bool>? aiConfigured = null) =>
        new(store, original, initial, credentials, run, aiConfigured);
}

internal static class SpellManager
{
    public static void Show() { using var form = Create(Spells.Store); form.ShowDialog(); }
    internal static SpellManagerDialog Create(SpellStore store, Func<string, bool>? confirm = null) => new(store, confirm);
    internal static string Kind(Spell spell) => spell.Type switch { "command" => "Shell command", "http" => "HTTP request", "prompt" => "AI prompt", _ => spell.Type };
    internal static string Result(Spell spell) => spell.Type == "http" && !spell.ShowResponse ? "No popup" : spell.Mode switch { "replace" => "Replace selection", "show" => "Show reply", _ => "Review before replacing" };
}

internal sealed class SpellManagerDialog : Form
{
    readonly SpellStore _store;
    readonly Func<string, bool> _confirm;
    readonly ListView _list = new() { Name = "Spells", AccessibleName = "Spells", Dock = DockStyle.Fill, View = View.Details, FullRowSelect = true, MultiSelect = false, HideSelection = false };
    readonly TextBox _search = new() { Name = "Search", AccessibleName = "Search spells", PlaceholderText = "Search by name or kind", Dock = DockStyle.Fill };
    readonly Label _status = DialogLayout.Text("");
    readonly Button _edit = DialogLayout.Button("Edit", "Edit"), _duplicate = DialogLayout.Button("Duplicate", "Duplicate"), _remove = DialogLayout.Button("Remove…", "Remove"), _undo = DialogLayout.Button("Undo removal", "Undo");
    readonly Stack<SpellStore.UndoState> _undos = new();
    bool _sizingColumns;
    internal SpellManagerDialog(SpellStore store, Func<string, bool>? confirm)
    {
        _store = store;
        _confirm = confirm ?? (name => MessageBox.Show(this, $"Remove the spell “{name}”?\n\nYou can undo this removal here until another edit changes the file. Stored credentials are kept.", "Remove spell", MessageBoxButtons.OKCancel, MessageBoxIcon.Warning, MessageBoxDefaultButton.Button2) == DialogResult.OK);
        DialogLayout.Frame(this, "My spells", new(850, 540), new(640, 430));
        var shell = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 5, Padding = new Padding(16) };
        shell.ColumnStyles.Add(new(SizeType.Percent, 100));
        shell.RowStyles.Add(new(SizeType.AutoSize)); shell.RowStyles.Add(new(SizeType.AutoSize)); shell.RowStyles.Add(new(SizeType.Percent, 100)); shell.RowStyles.Add(new(SizeType.AutoSize)); shell.RowStyles.Add(new(SizeType.AutoSize));
        Controls.Add(shell);
        shell.Controls.Add(DialogLayout.Text("My spells", true), 0, 0); shell.Controls.Add(_search, 0, 1);
        _list.Columns.Add("Name", 300); _list.Columns.Add("Kind", 150); _list.Columns.Add("Input", 80); _list.Columns.Add("Result", 220);
        shell.Controls.Add(_list, 0, 2); _status.Name = "Status"; shell.Controls.Add(_status, 0, 3);
        var buttons = new FlowLayoutPanel { AutoSize = true, Dock = DockStyle.Fill, WrapContents = true };
        var add = DialogLayout.Button("New spell", "New"); var starters = DialogLayout.Button("Starter spells", "Starters"); var close = DialogLayout.Button("Close", "Close"); close.DialogResult = DialogResult.Cancel;
        buttons.Controls.AddRange([add, _edit, _duplicate, _remove, _undo, starters, close]); shell.Controls.Add(buttons, 0, 4); CancelButton = close;
        _search.TextChanged += (_, _) => RefreshSpells(); _list.SelectedIndexChanged += (_, _) => UpdateButtons();
        _list.ClientSizeChanged += (_, _) => SizeColumns(); _list.FontChanged += (_, _) => SizeColumns();
        Shown += (_, _) => SizeColumns();
        _list.DoubleClick += (_, _) => EditSelected(false);
        _list.KeyDown += (_, e) => { if (e.KeyCode == Keys.Delete && Selected != null) { RemoveSelected(); e.Handled = true; } };
        add.Click += (_, _) => OpenEditor(null, null); _edit.Click += (_, _) => EditSelected(false); _duplicate.Click += (_, _) => EditSelected(true);
        _remove.Click += (_, _) => RemoveSelected(); _undo.Click += (_, _) => UndoRemoval();
        starters.Click += (_, _) => { using var gallery = StarterSpellsForm.Create(_store); gallery.ShowDialog(this); RefreshSpells(); };
        RefreshSpells();
    }
    internal Spell? Selected => _list.SelectedItems.Count == 1 ? _list.SelectedItems[0].Tag as Spell : null;
    internal void RefreshSpells()
    {
        var selected = Selected?.Name; _list.Items.Clear();
        try
        {
            var spells = _store.All();
            foreach (var s in spells.Where(s => (s.Name + " " + SpellManager.Kind(s) + (s.Files ? " file" : " text")).Contains(_search.Text, StringComparison.OrdinalIgnoreCase)))
            {
                var row = new ListViewItem([s.Name, SpellManager.Kind(s), s.Files ? "File" : "Text", SpellManager.Result(s)]) { Tag = s };
                _list.Items.Add(row); if (s.Name == selected) row.Selected = true;
            }
            _status.Text = spells.Count == 0 ? "No spells yet. Create one or choose a starter spell." : _list.Items.Count == 0 ? "No matches. Try another search." : $"{_list.Items.Count} spell(s) · Changes save immediately. Close keeps saved changes.";
        }
        catch { _status.Text = "Could not read spells.ini. Check the file and try reopening My spells."; }
        SizeColumns(); UpdateButtons();
    }
    void SizeColumns()
    {
        if (_sizingColumns || _list.ClientSize.Width <= 0) return;
        _sizingColumns = true;
        try
        {
            int padding = Math.Max(16, _list.Font.Height);
            int Measure(string text) => TextRenderer.MeasureText(text, _list.Font, Size.Empty,
                TextFormatFlags.SingleLine | TextFormatFlags.NoPrefix).Width + padding;
            var widths = _list.Columns.Cast<ColumnHeader>().Select(column => Measure(column.Text)).ToArray();
            foreach (ListViewItem item in _list.Items)
                for (int i = 0; i < widths.Length; i++) widths[i] = Math.Max(widths[i], Measure(item.SubItems[i].Text));
            // Use spare space for names, retaining measured minima for every column.
            // At narrow sizes the native horizontal scrollbar keeps all content accessible.
            widths[0] += Math.Max(0, _list.ClientSize.Width - 4 - widths.Sum());
            for (int i = 0; i < widths.Length; i++) _list.Columns[i].Width = widths[i];
        }
        finally { _sizingColumns = false; }
    }
    void UpdateButtons() { _edit.Enabled = _duplicate.Enabled = _remove.Enabled = Selected != null; _undo.Enabled = _undos.Count > 0; }
    void OpenEditor(Spell? original, Spell? initial)
    {
        using var form = SpellEditor.Create(_store, original, initial);
        if (form.ShowDialog(this) == DialogResult.OK) _undos.Clear();
        RefreshSpells();
    }
    void EditSelected(bool duplicate)
    {
        if (Selected is not { } selected) return;
        if (!duplicate) { OpenEditor(selected, null); return; }
        string stem = selected.Name[..Math.Min(selected.Name.Length, 100)]; string name = stem + " copy"; int number = 2;
        var names = _store.All().Select(s => s.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
        while (names.Contains(name)) name = stem + " copy " + number++;
        OpenEditor(null, selected with { Name = name });
    }
    internal bool RemoveSelected()
    {
        if (Selected is not { } selected || !_confirm(selected.Name)) return false;
        try { _undos.Push(_store.Delete(selected.Name, selected)); RefreshSpells(); _status.Text = $"Removed “{selected.Name}”. Undo removal restores it."; return true; }
        catch { _status.Text = "The spell could not be removed. Refresh the list and check file access."; return false; }
    }
    internal bool UndoRemoval()
    {
        if (!_undos.TryPeek(out var undo)) return false;
        try { _store.Undo(undo); _undos.Pop(); RefreshSpells(); return true; }
        catch { _status.Text = "Undo could not be saved, or the file changed since removal. It has been left unchanged."; return false; }
    }
}

internal static class StarterSpellsForm
{
    public static int Show()
    {
        using var form = Create(Spells.Store); form.ShowDialog(); return form.Added;
    }
    internal static StarterSpellDialog Create(SpellStore store) => new(store);
}
internal sealed class StarterSpellDialog : Form
{
    internal int Added { get; private set; }
    internal StarterSpellDialog(SpellStore store)
    {
        DialogLayout.Frame(this, "Starter spells", new(690, 570), new(540, 400));
        var shell = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 4, Padding = new Padding(16) };
        shell.ColumnStyles.Add(new(SizeType.Percent, 100)); shell.RowStyles.Add(new(SizeType.AutoSize)); shell.RowStyles.Add(new(SizeType.Percent, 100)); shell.RowStyles.Add(new(SizeType.AutoSize)); shell.RowStyles.Add(new(SizeType.AutoSize)); Controls.Add(shell);
        shell.Controls.Add(DialogLayout.Text("Choose AI prompts to make your own. Additions save immediately; a configured AI provider is needed to run them."), 0, 0);
        var scroll = new Panel { Dock = DockStyle.Fill, AutoScroll = true }; var column = DialogLayout.Column(); column.Dock = DockStyle.Top; scroll.Controls.Add(column); shell.Controls.Add(scroll, 0, 1);
        var names = store.All().Select(s => s.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var boxes = new List<(CheckBox Box, Spell Spell)>();
        foreach (var (spell, about) in Spells.Starters)
        {
            bool exists = names.Contains(spell.Name);
            var box = new CheckBox { Text = spell.Name + (exists ? " (already added)" : ""), AutoSize = true, Dock = DockStyle.Top, Enabled = !exists, UseMnemonic = false, Margin = new Padding(3, 10, 3, 0) };
            column.Controls.Add(box); column.Controls.Add(DialogLayout.Text(about)); boxes.Add((box, spell));
        }
        var status = DialogLayout.Text(""); shell.Controls.Add(status, 0, 2);
        var buttons = new FlowLayoutPanel { Dock = DockStyle.Fill, AutoSize = true };
        var add = DialogLayout.Button("Add selected", "Add"); var close = DialogLayout.Button("Close", "Close"); close.DialogResult = DialogResult.Cancel;
        buttons.Controls.AddRange([add, close]); shell.Controls.Add(buttons, 0, 3); CancelButton = close;
        add.Click += (_, _) =>
        {
            foreach (var (box, spell) in boxes.Where(b => b.Box.Enabled && b.Box.Checked))
                try { store.Save(spell); Added++; box.Checked = false; box.Enabled = false; box.Text += " (already added)"; }
                catch { status.Text = "Some spells could not be added. Existing spells were kept; close and reopen to refresh."; return; }
            status.Text = $"Added {Added} spell(s). Edit them in My spells.";
        };
    }
}
