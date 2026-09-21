using System.IO.Pipes;
using System.Text;
using static Spellbook.Native;

namespace Spellbook;

/// <summary>Hidden window that owns the popup menus, marshals to the UI thread, and hears clipboard changes.</summary>
internal sealed class ListenerForm : Form
{
    public Action? OnClipboardUpdate;
    public ListenerForm()
    {
        ShowInTaskbar = false; FormBorderStyle = FormBorderStyle.None; Opacity = 0; Size = new Size(1, 1);
        StartPosition = FormStartPosition.Manual; Location = new Point(-32000, -32000);
    }
    // a visible (zero-opacity, off-screen, tool-window) owner can take the foreground, so popup menus get keyboard focus
    protected override CreateParams CreateParams { get { var cp = base.CreateParams; cp.ExStyle |= Native.WS_EX_TOOLWINDOW; return cp; } }
    protected override void WndProc(ref Message m)
    {
        if (m.Msg == ClipboardHistory.WM_CLIPBOARDUPDATE) { OnClipboardUpdate?.Invoke(); return; }
        base.WndProc(ref m);
    }
}

/// <summary>Tray icon, popup menus, hotkeys, and the glue between them.</summary>
internal sealed class TrayApp : ApplicationContext
{
    public static TrayApp Current { get; private set; } = null!;
    public static Icon AppIcon { get; private set; } = SystemIcons.Application;
    public static readonly string PipeName = Brand.Name + ".files";

    public Settings Settings { get; private set; }

    readonly NotifyIcon _tray;
    readonly ListenerForm _anchor;
    readonly InputHooks _hooks = new();
    readonly Actions _act;
    readonly DownloadRouter _router;
    ClipboardHistory? _clipHistory;
    ContextMenuStrip _menu = new();
    ContextMenuStrip? _fileMenu;
    bool _paused;
    bool _exiting;
    readonly QuitServer _quit;
    readonly bool _lifecycleSmoke;

    public TrayApp(List<string>? pendingFiles = null, string? showUi = null, bool lifecycleSmoke = false)
    {
        _lifecycleSmoke = lifecycleSmoke;
        Current = this;
        Paths.EnsureData();
        Settings = Settings.Load();
        AppIcon = IconFactory.Make(32);

        _anchor = new ListenerForm();
        _anchor.Show();

        _act = new Actions(this);
        _router = new DownloadRouter(Settings);

        _tray = new NotifyIcon { Icon = IconFactory.Make(16), Text = Brand.Name + " - Ctrl/Alt + right-click on highlighted text", Visible = true, ContextMenuStrip = BuildTrayMenu() };
        _tray.MouseClick += (_, e) => { if (e.Button == MouseButtons.Left) ShowMenuAt(GetForegroundWindow(), Cursor.Position); };

        RebuildMenu();
        if (!lifecycleSmoke) ApplyHooks();
        _hooks.MenuTriggered += (hwnd, pt) => _anchor.BeginInvoke(() => ShowMenuAt(hwnd, pt));
        _hooks.HotkeyTriggered += a => _anchor.BeginInvoke(() => { if (!_paused) { _act.Target = GetForegroundWindow(); Safe(a); } });
        if (!lifecycleSmoke)
        {
            _hooks.Start();
            _router.Refresh();
            ApplyClipboardHistory();
        }
        StartPipeServer();
        _quit = new QuitServer(Paths.ExePath, () => _anchor.BeginInvoke(ExitApp));

        if (lifecycleSmoke)
        {
            _tray.Visible = false;
            File.WriteAllText(Paths.File_("lifecycle-ready"), "ready");
            return;
        }
        Toast.Show(Brand.Name + " is ready - Ctrl/Alt + right-click on highlighted text", 3000);
        if (showUi != null)
            _anchor.BeginInvoke(() =>
            {
                switch (showUi)
                {
                    case "providers": OpenProviders(); break;
                    case "starters": StarterSpellsForm.Show(); RebuildMenu(); break;
                    case "spell": if (SpellEditor.Edit("")) RebuildMenu(); break;
                    case "settings": OpenSettings(); break;
                    case "dropzone": DropZoneForm.Open(this); break;
                }
            });
        else if (!Settings.FirstRunDone)
            _anchor.BeginInvoke(OpenSetup);
        else
            _anchor.BeginInvoke(() => Updater.AutoCheck(Settings));
        if (pendingFiles is { Count: > 0 })
            _anchor.BeginInvoke(() => ShowFileMenu(pendingFiles, Cursor.Position));
    }

    // ------------------------------------------------------------ menus
    void ShowMenuAt(IntPtr target, Point pt)
    {
        if (_paused) return;
        _act.Target = target;
        if (ExplorerSelection.IsExplorer(target))
        {
            var files = ExplorerSelection.Get(target);
            if (files.Count > 0) { ShowFileMenu(files, pt); return; }
        }
        Input.ForceForeground(_anchor.Handle);   // the menu needs a foreground owner to take keys; plain SetForegroundWindow is refused when another process holds focus
        _menu.Show(pt);
    }

    public void ShowFileMenu(List<string> files, Point pt)
    {
        _fileMenu?.Dispose();
        _fileMenu = BuildFileMenu(files);
        Input.ForceForeground(_anchor.Handle);
        _fileMenu.Show(pt);
    }

    ToolStripMenuItem Item(string text, Action a, string? keys = null)
    {
        var it = new ToolStripMenuItem(text);
        if (keys != null) it.ShortcutKeyDisplayString = keys;
        it.Click += (_, _) => Safe(a);
        return it;
    }

    static void Safe(Action a)
    {
        try { a(); }
        catch (Exception e) { Log.Error("action", e); Toast.Show("[x] " + Util.Trunc(e.Message, 120), 5000); }
    }

    static ToolStripMenuItem Sub(string text, params ToolStripItem[] items)
    {
        var it = new ToolStripMenuItem(text);
        it.DropDownItems.AddRange(items);
        return it;
    }

    static ToolStripSeparator Sep() => new();
    static ContextMenuStrip NewMenu() => new() { Font = new Font("Segoe UI", 10f), ShowImageMargin = false, ShowCheckMargin = false };

    ToolStripMenuItem T(string text, string key, string? keys = null) => Item(text, () => _act.Apply(key), keys);
    ToolStripMenuItem C(string text, string instr, string mode, string? keys = null) => Item(text, () => _act.ClaudeAct(instr, mode), keys);
    ToolStripMenuItem I(string text, string kind) => Item(text, () => _act.Insert(kind));

    public void RebuildMenu()
    {
        var old = _menu;
        var m = NewMenu();

        var cse = Sub("Case",
            T("UPPERCASE", "upper", "Ctrl+Alt+U"), T("lowercase", "lower", "Ctrl+Alt+L"), T("Title Case", "title", "Ctrl+Alt+T"),
            T("Sentence case", "sentence", "Ctrl+Alt+S"), T("AP Headline Case", "apheadline"), Sep(),
            T("camelCase", "camel"), T("snake_case", "snake"), T("kebab-case", "kebab"), T("CONSTANT_CASE", "constant"), T("slugify (url)", "slug"));
        var ln = Sub("Lines && Paragraphs",
            T("Join lines into one paragraph", "join", "Ctrl+Alt+J"), T("Clean whitespace", "clean", "Ctrl+Alt+W"),
            T("Split sentences to lines", "splitsentences"), T("Collapse blank lines", "collapseblanks"), Sep(),
            T("Sort lines A-Z", "sortaz"), T("Sort lines Z-A", "sortza"), T("Unique lines", "unique"), T("Reverse lines", "reverse"),
            T("Trim each line", "trimlines"), T("Number lines", "numberlines"));
        var ty = Sub("Typography",
            T("Curly smart quotes", "curly"), T("Straight quotes", "straight"), T("Em dash  ( -- )", "emdash"),
            T("Ellipsis  ( ... )", "ellipsis"), T("Remove accents/diacritics", "deaccent"), T("Fix double spaces", "dblspace"));
        var enc = Sub("Encode / Decode",
            T("Base64 encode", "b64enc"), T("Base64 decode", "b64dec"), Sep(),
            T("URL encode", "urlenc"), T("URL decode", "urldec"), Sep(),
            T("HTML entities encode", "htmlenc"), T("HTML entities decode", "htmldec"));
        var dev = Sub("Dev (JSON / hash)",
            T("JSON pretty-print", "jsonpretty"), T("JSON minify", "jsonmin"), T("Escape as JSON string", "jsonescape"), Sep(),
            T("MD5 hash", "md5"), T("SHA-256 hash", "sha256"), Sep(), T("Wrap in code fence", "codeblock"));

        var langs = new[] { "Spanish", "French", "German", "Italian", "Portuguese", "Japanese", "Chinese (Simplified)" };
        var tr = Sub("Translate", langs.Select(l => (ToolStripItem)Item(l, () => _act.Translate(l))).ToArray());

        var ai = Sub("AI  (" + Ai.Describe(AiRole.Quick) + ")",
            C("Fix grammar && spelling", Actions.IN_GRAMMAR, "replace", "Ctrl+Alt+G"),
            Item("Polish (precise)", _act.PolishPrecise, "Ctrl+Alt+P"),
            C("Tighten / shorten", Actions.IN_TIGHTEN, "replace"),
            C("Rewrite for clarity", Actions.IN_CLARITY, "replace"),
            C("Rewrite as headline (AP)", Actions.IN_HEADLINE, "replace"), Sep(),
            C("Headline ideas (x5)", Actions.IN_HEADIDEAS, "show"),
            C("Summarize", Actions.IN_SUMMARY, "show"),
            C("Bullet summary", Actions.IN_BULLETS, "show"),
            C("Explain this", Actions.IN_EXPLAIN, "show"), Sep(),
            C("Code: find bugs", Actions.IN_BUGS, "show"),
            C("Code: add comments", Actions.IN_COMMENT, "review"), Sep(),
            tr,
            Item("Custom prompt...", _act.ClaudeCustom, "Ctrl+Alt+C"), Sep(),
            Item("Apply house style (auto prose/code)", _act.ApplyHouseStyle),
            Item("Rewrite to reading grade...", () => _act.MetricRewrite("grade")),
            Item("Trim to word count...", () => _act.MetricRewrite("words")));

        var recipes = new List<ToolStripItem>();
        foreach (var r in Recipes.All()) { var name = r.Name; recipes.Add(Item(name, () => _act.RunRecipe(name))); }
        if (recipes.Count > 0) recipes.Add(Sep());
        recipes.Add(Item("New recipe...", () => { RecipeEditor.Edit(""); RebuildMenu(); }));
        recipes.Add(Item("Edit recipes file", () => Util.OpenInEditor(Paths.Recipes)));
        var rec = Sub("Recipes", recipes.ToArray());

        var spells = new List<ToolStripItem>();
        foreach (var sp in Spells.All().Where(x => !x.Files)) { var s = sp; spells.Add(Item(s.Name, () => _act.RunSpell(s))); }
        if (spells.Count > 0) spells.Add(Sep());
        spells.Add(Item("Manage spells...", () => { SpellManager.Show(); RebuildMenu(); }));
        var mySpells = Sub("My spells", spells.ToArray());

        var clipItems = new List<ToolStripItem>();
        if (_clipHistory != null)
        {
            foreach (var h in _clipHistory.Items.Take(12)) { var t = h; clipItems.Add(Item(ClipboardHistory.Label(t), () => _act.PasteString(t))); }
            if (clipItems.Count > 0) clipItems.Add(Sep());
            clipItems.Add(Item("Clear history", () => { _clipHistory.Clear(); RebuildMenu(); }));
        }
        else clipItems.Add(Item("Clipboard history is off (Settings)", () => OpenSettings()));
        var clip = Sub("Clipboard history", clipItems.ToArray());

        var snipItems = new List<ToolStripItem>();
        foreach (var (name, body) in Snippets.All()) { var t = body; snipItems.Add(Item(name, () => _act.PasteString(t))); }
        if (snipItems.Count > 0) snipItems.Add(Sep());
        snipItems.Add(Item("Save selection as snippet...", _act.SaveSnippet));
        snipItems.Add(Item("Edit snippets file", () => Util.OpenInEditor(Snippets.File_)));
        var snips = Sub("Snippets", snipItems.ToArray());

        var vis = Sub("Screen snip",
            Item("Snip -> OCR (image -> text)", () => _act.VisionAction("ocr")),
            Item("Snip -> describe", () => _act.VisionAction("describe")),
            Item("Snip -> reproduce as HTML", () => _act.VisionAction("reproduce")),
            Item("Snip -> OCR -> file as note", () => _act.VisionAction("ocr-note")), Sep(),
            Item("Snip -> save PNG to " + Path.GetFileName(Settings.ScreenshotDir), () => _act.VisionAction("save")));

        var ins = Sub("Insert",
            I("Date  (" + DateTime.Now.ToString("yyyy-MM-dd") + ")", "isodate"),
            I("Date  (" + DateTime.Now.ToString("MMMM d, yyyy") + ")", "longdate"),
            I("Timestamp  (date + time)", "stamp"), I("Dateline  (CITY -- )", "dateline"), Sep(),
            I("UUID v4", "uuid"), I("Lorem ipsum", "lorem"));

        var text = Sub("Text", cse, ln, ty, Sep(), Item("Paste as plain text", _act.PastePlain, Settings.PastePlainHotkey ? "Ctrl+Shift+V" : null));
        var data = Sub("Code && data", enc, dev, Item("Audit code / string", _act.AuditCode));

        var capItems = new List<ToolStripItem>
        {
            Item("Capture to notes folder", _act.CaptureNotes),
            vis,
            Item("Copy page source (URL)", _act.PageSourceAction),
            Item("Route last download...", _router.RouteNewest),
            Item("Create reminder...", _act.Reminder),
        };
        if (Ntfy.Configured(Settings)) capItems.Add(Item("Push selection to phone (ntfy)", _act.PushNtfy));
        if (Paperless.Configured(Settings)) capItems.Add(Item("Capture to Paperless", _act.CapturePaperless));
        capItems.Add(Sep());
        capItems.Add(Item("Files... (drop zone)", () => DropZoneForm.Open(this)));
        var cap = Sub("Capture / files", capItems.ToArray());

        var analyze = Sub("Analyze",
            Item("Word && character count", _act.WordCount),
            Item("Readability (Flesch)", _act.Readability),
            Item("Search web for selection", _act.SearchWeb));

        var sysItems = new List<ToolStripItem>();
        if (Fleet.Hosts(Settings).Count > 0) sysItems.Add(Item("Fleet: run command...", _act.FleetRunAction));
        sysItems.Add(Item(Settings.RouterEnabled ? "Download router: ON  (click to turn off)" : "Download router: OFF  (click to turn on)", () =>
        {
            Settings.RouterEnabled = !Settings.RouterEnabled; Settings.Save(); _router.Refresh(); RebuildMenu();
            Toast.Show("Download router " + (Settings.RouterEnabled ? "ON" : "OFF"), 3000);
        }));
        var sys = Sub("System", sysItems.ToArray());

        m.Items.AddRange(new ToolStripItem[]
        {
            text, data, ai, mySpells, rec, Sep(), clip, snips, ins, Sep(), cap, analyze, sys, Sep(),
            Item("Set up Grimoire...", OpenSetup),
            Item("AI providers...", OpenProviders),
            Item("Settings...", OpenSettings),
            Item("Exit", ExitApp),
        });
        _menu = m;
        old.Dispose();
    }

    ContextMenuStrip BuildFileMenu(List<string> files)
    {
        var m = NewMenu();
        var kinds = files.Select(Files.Kind).ToHashSet();
        bool any(params string[] k) => kinds.Overlaps(k);
        var only = files.Where(f => !Directory.Exists(f)).ToList();
        List<string> Of(params string[] k) => only.Where(f => k.Contains(Files.Kind(f))).ToList();

        var head = new ToolStripMenuItem(files.Count == 1 ? Path.GetFileName(files[0]) : files.Count + " items selected") { Enabled = false, Font = new Font("Segoe UI", 9.5f, FontStyle.Bold) };
        m.Items.Add(head);
        m.Items.Add(Sep());

        if (any("image"))
        {
            var imgs = Of("image");
            bool heic = imgs.Any(f => Path.GetExtension(f).ToLowerInvariant() is ".heic" or ".heif");
            m.Items.Add(Sub("Image" + (heic ? "  (HEIC ok)" : ""),
                Item("Convert to JPG", () => _act.FileBatch(imgs, "To JPG", f => Files.ConvertImage(f, ".jpg", 90))),
                Item("Convert to PNG", () => _act.FileBatch(imgs, "To PNG", f => Files.ConvertImage(f, ".png"))),
                Item("Convert to BMP", () => _act.FileBatch(imgs, "To BMP", f => Files.ConvertImage(f, ".bmp"))),
                Item("Convert to TIFF", () => _act.FileBatch(imgs, "To TIFF", f => Files.ConvertImage(f, ".tif"))),
                Item("Convert to GIF", () => _act.FileBatch(imgs, "To GIF", f => Files.ConvertImage(f, ".gif"))), Sep(),
                Item("Resize to 1920 px (JPG)", () => _act.FileBatch(imgs, "Resize 1920", f => Files.ConvertImage(f, ".jpg", 90, 1920))),
                Item("Resize to 1280 px (JPG)", () => _act.FileBatch(imgs, "Resize 1280", f => Files.ConvertImage(f, ".jpg", 90, 1280))),
                Item("Resize to 800 px (JPG)", () => _act.FileBatch(imgs, "Resize 800", f => Files.ConvertImage(f, ".jpg", 88, 800))),
                Item("Resize to custom size...", () =>
                {
                    var s = InputDialog.Show("Resize", "Longest side in pixels:", "1600");
                    if (int.TryParse(s, out var px) && px > 16) _act.FileBatch(imgs, "Resize " + px, f => Files.ConvertImage(f, ".jpg", 90, px));
                }), Sep(),
                Item("Compress to ~500 KB (JPG)", () => _act.FileBatch(imgs, "Compress", f => Files.CompressImage(f, 500))),
                Item("Compress to ~200 KB (JPG)", () => _act.FileBatch(imgs, "Compress", f => Files.CompressImage(f, 200))),
                Item("Strip metadata (EXIF/GPS)", () => _act.FileBatch(imgs, "Strip metadata", Files.StripMetadata)), Sep(),
                Item("Describe image (AI vision)", () => _act.FileSummarizeImage(imgs, "describe")),
                Item("OCR image (AI vision)", () => _act.FileSummarizeImage(imgs, "ocr"))));
        }
        if (any("docx", "pdf", "markdown", "csv", "json", "yaml", "text"))
        {
            var items = new List<ToolStripItem>();
            if (any("docx")) { var d = Of("docx"); items.Add(Item("DOCX -> Markdown", () => _act.FileBatch(d, "DOCX to MD", Files.DocxToMarkdown))); items.Add(Item("DOCX -> Text", () => _act.FileBatch(d, "DOCX to TXT", Files.DocxToText))); }
            if (any("pdf")) { var d = Of("pdf"); items.Add(Item("PDF -> Text", () => _act.FileBatch(d, "PDF to TXT", Files.PdfToText))); }
            if (any("markdown")) { var d = Of("markdown"); items.Add(Item("Markdown -> HTML", () => _act.FileBatch(d, "MD to HTML", Files.MarkdownToHtml))); items.Add(Item("Markdown -> DOCX", () => _act.FileBatch(d, "MD to DOCX", Files.MarkdownToDocx))); }
            if (any("csv")) { var d = Of("csv"); items.Add(Item("CSV -> JSON", () => _act.FileBatch(d, "CSV to JSON", Files.CsvToJson))); }
            if (any("json")) { var d = Of("json"); items.Add(Item("JSON -> CSV", () => _act.FileBatch(d, "JSON to CSV", Files.JsonToCsv))); items.Add(Item("JSON -> YAML", () => _act.FileBatch(d, "JSON to YAML", Files.JsonToYaml))); }
            if (any("yaml")) { var d = Of("yaml"); items.Add(Item("YAML -> JSON", () => _act.FileBatch(d, "YAML to JSON", Files.YamlToJson))); }
            var tx = Of("text", "markdown", "csv", "json", "yaml");
            if (tx.Count > 0) { items.Add(Sep()); items.Add(Item("Line endings -> LF (Linux)", () => _act.FileBatch(tx, "LF", f => Files.NormalizeText(f, false)))); items.Add(Item("Line endings -> CRLF (Windows)", () => _act.FileBatch(tx, "CRLF", f => Files.NormalizeText(f, true)))); }
            m.Items.Add(Sub("Document", items.ToArray()));
        }
        if (any("video", "audio"))
        {
            var items = new List<ToolStripItem>();
            if (Files.FfmpegPath == null) items.Add(Item("Install ffmpeg (winget)...", Files.InstallFfmpeg));
            else
            {
                if (any("video"))
                {
                    var v = Of("video");
                    items.Add(Item("Video -> GIF (640 px, 12 fps)", () => _act.FileBatch(v, "To GIF", f => Files.VideoToGif(f))));
                    items.Add(Item("Extract audio -> MP3", () => _act.FileBatch(v, "Extract audio", Files.ExtractAudio)));
                    items.Add(Item("Compress video (H.264, smaller)", () => _act.FileBatch(v, "Compress", Files.CompressVideo)));
                    items.Add(Item("Convert to MP4", () => _act.FileBatch(v, "To MP4", Files.ToMp4)));
                    items.Add(Item("First frame -> PNG", () => _act.FileBatch(v, "Frame", Files.VideoFrame)));
                    items.Add(Item("Trim...", () =>
                    {
                        var st = InputDialog.Show("Trim", "Start (hh:mm:ss or seconds):", "00:00:00"); if (st == null) return;
                        var du = InputDialog.Show("Trim", "Duration (hh:mm:ss or seconds):", "00:00:10"); if (du == null) return;
                        _act.FileBatch(v, "Trim", f => Files.Trim(f, st.Trim(), du.Trim()));
                    }));
                }
                if (any("audio"))
                {
                    var a = Of("audio");
                    if (items.Count > 0) items.Add(Sep());
                    items.Add(Item("Audio -> MP3", () => _act.FileBatch(a, "To MP3", Files.AudioToMp3)));
                    items.Add(Item("Audio -> WAV", () => _act.FileBatch(a, "To WAV", Files.AudioToWav)));
                }
            }
            m.Items.Add(Sub("Media", items.ToArray()));
        }
        var readable = Of("docx", "pdf", "markdown", "csv", "json", "yaml", "text");
        if (readable.Count > 0)
        {
            m.Items.Add(Sub("AI",
                Item("Summarize", () => _act.FileSummarize(readable, "Summarize this document in a short paragraph followed by key bullet points.", "Summary")),
                Item("Explain", () => _act.FileSummarize(readable, Actions.IN_EXPLAIN, "Explain")),
                Item("Extract action items", () => _act.FileSummarize(readable, "List every action item, decision, deadline, and open question in this text as bullets.", "Action items")),
                Item("Ask a question...", () =>
                {
                    var q = InputDialog.Show("Ask about file(s)", "Question or instruction:");
                    if (!string.IsNullOrWhiteSpace(q)) _act.FileSummarize(readable, q.Trim(), "Answer");
                })));
        }
        var fileSpells = Spells.All().Where(s => s.Files).ToList();
        if (fileSpells.Count > 0)
            m.Items.Add(Sub("My spells", fileSpells.Select(s => (ToolStripItem)Item(s.Name, () => _act.FileSpell(only, s))).ToArray()));
        if (Paperless.Configured(Settings) && only.Count > 0)
            m.Items.Add(Item("Send to Paperless", () => _act.FilePaperless(only)));

        m.Items.Add(Sep());
        m.Items.Add(Item("Copy path" + (files.Count > 1 ? "s" : ""), () => { Clip.SetText(string.Join(Environment.NewLine, files)); Toast.Show("Path copied", 2000); }));
        if (readable.Count > 0) m.Items.Add(Item("Copy contents as text", () => _act.Busy("Reading...", () => string.Join("\n\n", readable.Select(Files.TextOf))).ContinueWith(t => { if (t.Result != null) _anchor.BeginInvoke(() => { Clip.SetText(t.Result); Toast.Show("Contents copied", 2000); }); })));
        if (only.Count > 0) m.Items.Add(Item("SHA-256", () => _act.Busy("Hashing...", () => string.Join("\n", only.Select(f => Files.Sha256(f) + "  " + Path.GetFileName(f)))).ContinueWith(t => { if (t.Result != null) _anchor.BeginInvoke(() => ResultForm.Show("SHA-256", t.Result)); })));
        m.Items.Add(Item("Zip", () => _act.FileBatch(new List<string> { files[0] }, "Zip", _ => Files.ZipFiles(files))));
        m.Items.Add(Sep());
        m.Items.Add(Item("Text menu instead", () => { SetForegroundWindow(_anchor.Handle); _menu.Show(Cursor.Position); }));
        return m;
    }

    ContextMenuStrip BuildTrayMenu()
    {
        var m = new ContextMenuStrip { Font = new Font("Segoe UI", 9.75f) };
        var pause = new ToolStripMenuItem("Pause hotkeys");
        pause.Click += (_, _) => { _paused = !_paused; _hooks.Enabled = !_paused; pause.Text = _paused ? "Resume hotkeys" : "Pause hotkeys"; _tray.Icon = _paused ? SystemIcons.Shield : IconFactory.Make(16); Toast.Show(_paused ? Brand.Name + " paused" : Brand.Name + " resumed", 2500); };
        var sendTo = new ToolStripMenuItem(ExplorerSelection.SendToInstalled() ? "'Send to' shortcut installed" : "Install 'Send to' shortcut");
        sendTo.Click += (_, _) => Safe(() => { ExplorerSelection.InstallSendTo(); sendTo.Text = "'Send to' shortcut installed"; Toast.Show("Right-click any file -> Send to -> " + Brand.Name, 4000); });
        m.Items.AddRange(new ToolStripItem[]
        {
            Item("Open " + Brand.Name + " menu", () => ShowMenuAt(GetForegroundWindow(), Cursor.Position)),
            Item("Files... (drop zone)", () => DropZoneForm.Open(this)),
            pause, Sep(),
            Item("Set up Grimoire...", OpenSetup),
            Item("AI providers...", OpenProviders),
            Item("Settings...", OpenSettings),
            Item("Open data folder", () => Util.OpenUrl(Paths.DataDir)),
            sendTo, Sep(),
            Item("View releases...", Updater.ViewReleases),
            Item("About " + Brand.Name, () => MessageBox.Show(
                Brand.Name + " " + Brand.Version + " - " + Brand.Tagline + ".\n\n" +
                "Highlight text, then Ctrl + right-click (or Alt + right-click, or Ctrl+Alt+F) to open the spellbook.\n" +
                "Select files in Explorer and do the same for conversions.\n" +
                "AI: " + Ai.Describe(AiRole.Quick) + " (quick), " + Ai.Describe(AiRole.Precise) + " (precise), " + Ai.Describe(AiRole.Vision) + " (vision).\n\n" +
                "Data folder: " + Paths.DataDir + "\n" + Brand.Homepage + (Updater.IsInstalled ? "\n(installed)" : "\n(portable)"), Brand.Name, MessageBoxButtons.OK, MessageBoxIcon.Information)),
            Item("Exit", ExitApp),
        });
        return m;
    }

    // ------------------------------------------------------------ hotkeys / services
    void ApplyHooks()
    {
        _hooks.UseCtrl = Settings.TriggerCtrl;
        _hooks.UseAlt = Settings.TriggerAlt;
        var list = new List<(uint, Keys, Action)>();
        if (Settings.QuickHotkeys)
        {
            const uint CA = MOD_CONTROL | MOD_ALT;
            list.Add((CA, Keys.F, () => ShowMenuAt(_act.Target, Cursor.Position)));
            list.Add((CA, Keys.U, () => _act.Apply("upper")));
            list.Add((CA, Keys.L, () => _act.Apply("lower")));
            list.Add((CA, Keys.T, () => _act.Apply("title")));
            list.Add((CA, Keys.S, () => _act.Apply("sentence")));
            list.Add((CA, Keys.J, () => _act.Apply("join")));
            list.Add((CA, Keys.W, () => _act.Apply("clean")));
            list.Add((CA, Keys.G, () => _act.ClaudeAct(Actions.IN_GRAMMAR, "replace")));
            list.Add((CA, Keys.C, _act.ClaudeCustom));
            list.Add((CA, Keys.P, _act.PolishPrecise));
        }
        if (Settings.PastePlainHotkey) list.Add((MOD_CONTROL | MOD_SHIFT, Keys.V, _act.PastePlain));
        _hooks.SetHotkeys(list);
        Log.Write($"hotkeys set: {list.Count}");
    }

    void ApplyClipboardHistory()
    {
        if (Settings.ClipboardHistory && _clipHistory == null)
        {
            _clipHistory = new ClipboardHistory(_anchor.Handle);
            _anchor.OnClipboardUpdate = () => _clipHistory?.OnUpdate();
        }
        else if (!Settings.ClipboardHistory && _clipHistory != null)
        {
            _clipHistory.Dispose(); _clipHistory = null; _anchor.OnClipboardUpdate = null;
        }
    }

    /// <summary>"&lt;exe&gt; --files a b c" from a second instance (Send-to shortcut) lands here.</summary>
    void StartPipeServer()
    {
        var t = new Thread(() =>
        {
            while (true)
            {
                try
                {
                    using var server = new NamedPipeServerStream(PipeName, PipeDirection.In, 1, PipeTransmissionMode.Byte, PipeOptions.CurrentUserOnly);
                    server.WaitForConnection();
                    using var reader = new StreamReader(server, Encoding.UTF8);
                    var files = new List<string>();
                    string? line;
                    while ((line = reader.ReadLine()) != null) if (File.Exists(line) || Directory.Exists(line)) files.Add(line);
                    if (files.Count > 0) _anchor.BeginInvoke(() => ShowFileMenu(files, Cursor.Position));
                }
                catch (Exception e) { Log.Error("pipe", e); Thread.Sleep(1000); }
            }
        }) { IsBackground = true, Name = Brand.Name + "Pipe" };
        t.Start();
    }

    public static bool SendFilesToRunningInstance(IEnumerable<string> files)
    {
        try
        {
            using var client = new NamedPipeClientStream(".", PipeName, PipeDirection.Out);
            client.Connect(800);
            using var w = new StreamWriter(client, new UTF8Encoding(false));
            foreach (var f in files) w.WriteLine(Path.GetFullPath(f));
            w.Flush();
            return true;
        }
        catch { return false; }
    }

    SetupWizard? _setup;
    void OpenSetup()
    {
        if (_setup is { IsDisposed: false }) { _setup.Activate(); return; }
        _setup = new SetupWizard(Settings);
        _setup.FormClosed += (_, _) =>
        {
            ApplyHooks(); ApplyClipboardHistory(); RebuildMenu();
            _setup?.Dispose(); _setup = null;
        };
        _setup.Show();
    }

    void OpenProviders()
    {
        ProvidersForm.Show(Settings);
        RebuildMenu();
    }

    void OpenSettings()
    {
        if (!SettingsForm.Show(Settings)) { RebuildMenu(); return; }
        ApplyHooks();
        ApplyClipboardHistory();
        _router.Refresh();
        RebuildMenu();
        Toast.Show("Settings saved", 2500);
    }

    async void ExitApp()
    {
        if (_exiting) return;
        _exiting = true;
        await _act.CancelAndWaitAsync();
        _quit.Dispose();
        _tray.Visible = false;
        _hooks.Dispose();
        _router.Dispose();
        _clipHistory?.Dispose();
        Toast.Dismiss();
        if (_lifecycleSmoke) File.WriteAllText(Paths.File_("lifecycle-closed"), "closed");
        ExitThread();
    }
}
