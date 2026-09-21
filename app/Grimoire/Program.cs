namespace Spellbook;

static class Program
{
    [STAThread]
    static int Main(string[] args)
    {
        // Installer extracts the NEW candidate as a helper; never execute a legacy
        // binary with an unknown option (2.1 would initialize its UI and hooks).
        if (args.Length == 2 && args[0] == "--quit-target") return QuitServer.QuitAndWait(args[1]);
        if (args.Length == 1 && args[0] == "--quit") return QuitServer.QuitAndWait(Paths.ExePath);
        // "<exe> --remind <file>" is the scheduled-reminder popup; it must not be
        // blocked by the single-instance mutex of the running tray app.
        if (args.Length >= 2 && args[0] == "--remind")
        {
            ApplicationConfiguration.Initialize();
            Reminders.ShowReminder(args[1]);
            return 0;
        }
        // diagnostics: "--test-inject" waits 2 s then sends Ctrl+C to whatever is focused (no hooks involved)
        if (args.Length >= 1 && args[0] == "--test-inject")
        {
            Thread.Sleep(2000);
            Input.CtrlC();
            Thread.Sleep(300);
            Input.TypeText("[injected]");
            return 0;
        }
        // diagnostics: "--test-chord U" waits 2 s then presses Ctrl+Alt+<key> like a real keyboard would
        if (args.Length >= 2 && args[0] == "--test-chord")
        {
            Thread.Sleep(2000);
            Input.SendChordUnstamped(Native.VK_CONTROL, Native.VK_MENU, (ushort)char.ToUpperInvariant(args[1][0]));
            return 0;
        }
        // diagnostics: "--test-keys DOWN,RIGHT,wait:500,ESC" waits 1 s then plays the keys like a real keyboard
        if (args.Length >= 2 && args[0] == "--test-keys")
        {
            Thread.Sleep(1000);
            Input.PlayKeysUnstamped(args[1]);
            return 0;
        }
        // diagnostics: "--test-click" waits 2 s then Ctrl + right-clicks at the cursor
        if (args.Length >= 1 && args[0] == "--test-click")
        {
            Thread.Sleep(2000);
            Input.SendCtrlRightClickUnstamped();
            return 0;
        }
        // headless conversions: "<exe> --convert jpg|png|md|txt|html|docx|json|csv|yaml|gif|mp3|compress|strip|small <file>..."
        if (args.Length >= 3 && args[0] == "--convert")
        {
            Paths.EnsureData();
            var op = args[1].ToLowerInvariant();
            foreach (var f in args.Skip(2))
            {
                try
                {
                    var outp = op switch
                    {
                        "jpg" => Files.ConvertImage(f, ".jpg", 90),
                        "png" => Files.ConvertImage(f, ".png"),
                        "small" => Files.CompressImage(f, 300),
                        "strip" => Files.StripMetadata(f),
                        "md" => Files.DocxToMarkdown(f),
                        "txt" => Files.Kind(f) == "pdf" ? Files.PdfToText(f) : Files.DocxToText(f),
                        "html" => Files.MarkdownToHtml(f),
                        "docx" => Files.MarkdownToDocx(f),
                        "json" => Files.Kind(f) == "yaml" ? Files.YamlToJson(f) : Files.CsvToJson(f),
                        "csv" => Files.JsonToCsv(f),
                        "yaml" => Files.JsonToYaml(f),
                        "gif" => Files.VideoToGif(f),
                        "mp3" => Files.Kind(f) == "video" ? Files.ExtractAudio(f) : Files.AudioToMp3(f),
                        "compress" => Files.CompressVideo(f),
                        _ => throw new InvalidOperationException("unknown op " + op),
                    };
                    Console.WriteLine("OK  " + outp);
                }
                catch (Exception e) { Console.WriteLine("ERR " + f + ": " + e.Message); }
            }
            return 0;
        }
        // "<exe> --files a b c" (the Explorer Send-to shortcut): hand the files to the running instance
        List<string>? pending = null;
        if (args.Length >= 1 && args[0] == "--files")
        {
            pending = args.Skip(1).Where(a => File.Exists(a) || Directory.Exists(a)).Select(Path.GetFullPath).ToList();
            if (pending.Count == 0) return 0;
            if (TrayApp.SendFilesToRunningInstance(pending)) return 0;
        }

        bool lifecycleSmoke = args.Length == 1 && args[0] == "--lifecycle-smoke";
        if (lifecycleSmoke)
        {
            var data = Environment.GetEnvironmentVariable(Brand.EnvPrefix + "HOME");
            if (Environment.GetEnvironmentVariable(Brand.EnvPrefix + "LIFECYCLE_SMOKE") != "1" ||
                string.IsNullOrEmpty(data) || !Path.GetFullPath(data).StartsWith(Path.GetFullPath(Path.GetTempPath()), StringComparison.OrdinalIgnoreCase) ||
                !File.Exists(Path.Combine(data, "grimoire-lifecycle-fixture"))) return 2;
        }
        using var mutex = new Mutex(true, Brand.Mutex, out bool created);
        if (!created)
        {
            MessageBox.Show(Brand.Name + " is already running. Look for its icon in the tray.",
                Brand.Name, MessageBoxButtons.OK, MessageBoxIcon.Information);
            return 0;
        }

        ApplicationConfiguration.Initialize();
        Application.SetUnhandledExceptionMode(UnhandledExceptionMode.CatchException);
        Application.ThreadException += (_, e) => { Log.Error("unhandled", e.Exception); Toast.Show("[x] " + Util.Trunc(e.Exception.Message, 120), 5000); };
        var ui = Array.IndexOf(args, "--ui") is var i and >= 0 && i + 1 < args.Length ? args[i + 1] : (args.Contains("--show-providers") ? "providers" : null);
        Application.Run(new TrayApp(pending, ui, lifecycleSmoke));
        return 0;
    }
}
