using Spellbook;

namespace Spellbook.Tests;

internal static class Program
{
    static readonly List<string> Results =
    [
        "Grimoire Windows regression runner",
        "Evidence scope: controlled synthetic fixtures only; no desktop interactivity, public network requests, credentials, or user data.",
    ];

    static int _failures;

    [STAThread]
    public static int Main(string[] args)
    {
        if (args.Length >= 2 && args[0] == "--fixture") return RuntimeTests.Fixture(args);
        System.Windows.Forms.Application.SetHighDpiMode(System.Windows.Forms.HighDpiMode.PerMonitorV2);
        System.Windows.Forms.Application.EnableVisualStyles();
        System.Windows.Forms.Application.SetCompatibleTextRenderingDefault(false);
        bool uiOnly = args.Length > 0 && args[0] == "--ui-smoke";
        if (uiOnly) args = args[1..];
        foreach (var line in Results) Console.WriteLine(line);
        var reportPath = ParseReportPath(args);
        if (_failures > 0)
        {
            WriteReport(reportPath);
            return 2;
        }

        var dataRoot = Path.Combine(Path.GetTempPath(), "Grimoire.Tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dataRoot);
        Environment.SetEnvironmentVariable(Brand.EnvPrefix + "HOME", dataRoot);

        try
        {
            Paths.EnsureData();
            if (!uiOnly)
            {
                Run("own-path quit IPC is acknowledged and wrong paths time out", DeliveryTests.QuitProtocol);
                Run("source candidate update preference never triggers automatic checks", DeliveryTests.ManualUpdates);
                Run("embedded defaults seed an isolated data root", TestEmbeddedDefaults);
                Run("every transform produces its representative output", TestTransforms);
                Run("settings round-trip all persisted values", TestSettingsRoundTrip);
                Run("INI entries preserve values and support removal", TestIniEntries);
                Run("recipes persist, rename, and delete", TestRecipes);
                Run("snippets preserve newlines and delete", TestSnippets);
                Run("spells persist supported kinds and reject an unsupported kind", TestSpells);
                Run("operation overlap, cancellation, failed setup, owner thread and quit lifecycle", RuntimeTests.OperationLifecycleCases);
                Run("process stdin, output limits, cancellation, exit failures and UI context", RuntimeTests.ProcessCases);
                Run("job owns descendants after live and already-exited parents", RuntimeTests.ProcessTrees);
                Run("CLI argument restrictions, executable launch and npm shim resolution", RuntimeTests.CliSetup);
                Run("temporary directories and diagnostic privacy", RuntimeTests.TempAndDiagnostics);
                Run("loopback API and HTTP spell cancellation", RuntimeTests.HttpCancellation);
                Run("command spells run and reject nonzero exits with stdout", RuntimeTests.CommandSpell);
                Run("prompt composition sends the selected text exactly once", AiTests.Composition);
                Run("images reach only the provider chosen for the role", AiTests.ImageRouting);
                Run("unverified CLI adapters refuse completion and preserve saved roles", AiTests.DisabledCliProviders);
                Run("clipboard snapshots retain monitor exclusion", () => ClipboardTests.SnapshotPreservesPrivacy(ClipboardTests.Exclude, new byte[] { 0x58 }));
                Run("clipboard snapshots retain history opt-out", () => ClipboardTests.SnapshotPreservesPrivacy(ClipboardTests.History, new byte[4]));
                Run("clipboard snapshots retain cloud opt-out", () => ClipboardTests.SnapshotPreservesPrivacy(ClipboardTests.Cloud, new byte[4]));
                Run("clipboard snapshots retain affirmative privacy preferences", () => ClipboardTests.SnapshotPreservesPrivacy(ClipboardTests.History, new byte[] { 1, 0, 0, 0 }));
                Run("temporary paste text is excluded from Windows clipboard monitoring", ClipboardTests.TemporaryTextIsPrivate);
                Run("local history skips monitor-excluded text", () => ClipboardTests.HistorySkipsProtectedText(ClipboardTests.Exclude));
                Run("local history skips history-excluded text", () => ClipboardTests.HistorySkipsProtectedText(ClipboardTests.History));
                Run("local history skips cloud-excluded text", () => ClipboardTests.HistorySkipsProtectedText(ClipboardTests.Cloud));
                Run("local history skips malformed privacy preferences", ClipboardTests.HistorySkipsMalformedMarker);
                Run("local history honors affirmative privacy preferences", ClipboardTests.HistoryCapturesAllowedText);
                Run("local history captures ordinary text", ClipboardTests.HistoryCapturesOrdinaryText);
                Run("clipboard snapshots bound oversized marker stream reads", ClipboardTests.SnapshotBoundsMarkerStream);
                Run("local history bounds oversized marker stream reads", ClipboardTests.HistoryBoundsMarkerStream);
                Run("clipboard marker read failures preserve exclusion", ClipboardTests.MarkerReadFailuresFailClosed);
            }
            Run("spell store atomic mutations, collision, undo and content preservation", SpellTests.Store);
            Run("actual spell editor save, cancel and named credential lifecycle", SpellTests.Editor);
            Run("actual spell manager search, confirmation and undo", SpellTests.Manager);
            Run("spell editor captures inputs and cancels on close", SpellTests.Cancellation);
            Run("setup finish, cancel, save failure and isolated startup preferences", SetupTests.State);
            Run("provider roles preserve None and unavailable values", SetupTests.Roles);
            Run("closing provider dialog cancels status and test workers", SetupTests.Cancellation);
            string uiDirectory = Path.Combine(reportPath == null ? "test-results" : Path.GetDirectoryName(Path.GetFullPath(reportPath))!, "ui");
            Run("actual setup and provider forms render at scaled sizes", () => SetupTests.Render(uiDirectory));
            Run("actual spell manager, editor and gallery render at scaled sizes", () => SpellTests.Render(uiDirectory));
        }
        finally
        {
            try { Directory.Delete(dataRoot, true); }
            catch (Exception e) { Fail("isolated fixture cleanup", e.Message); }
        }

        WriteReport(reportPath);
        return _failures == 0 ? 0 : 1;
    }

    static string? ParseReportPath(string[] args)
    {
        if (args.Length == 0) return null;
        if (args.Length == 2 && args[0] == "--report" && !string.IsNullOrWhiteSpace(args[1])) return args[1];
        Fail("command line", "usage: Grimoire.Tests [--ui-smoke] [--report <path>]");
        return null;
    }

    static void Run(string name, Action test)
    {
        try
        {
            test();
            Record("PASS", name);
        }
        catch (Exception e)
        {
            Fail(name, $"{e.GetType().Name}: {e.Message}");
        }
    }

    static void TestEmbeddedDefaults()
    {
        foreach (var name in new[] { "recipes.ini", "style-prose.md", "style-code.md", "spells.ini", "snippets.ini" })
            Assert(File.Exists(Paths.File_(name)), $"missing seeded {name}");

        AssertEqual("clean,grammar,curly,emdash", Recipes.Steps("Polish prose"), "default recipe");
        AssertEqual("Thanks,\n", Snippets.All().Single(x => x.Name == "Email sign-off").Text, "default snippet");
        Assert(Spells.Exists("Reply politely"), "default spell was not seeded");
    }

    static void TestTransforms()
    {
        var cases = new Dictionary<string, (string Input, string Expected)>(StringComparer.Ordinal)
        {
            ["upper"] = ("MiXeD", "MIXED"),
            ["lower"] = ("MiXeD", "mixed"),
            ["title"] = ("hELLO wORLD", "Hello World"),
            ["sentence"] = ("hELLO. wORLD!", "Hello. World!"),
            ["apheadline"] = ("the rise of the machines", "The Rise of the Machines"),
            ["camel"] = ("hello-world value", "helloWorldValue"),
            ["snake"] = ("HelloWorld value", "hello_world_value"),
            ["kebab"] = ("HelloWorld value", "hello-world-value"),
            ["constant"] = ("HelloWorld value", "HELLO_WORLD_VALUE"),
            ["slug"] = ("Crème brûlée", "creme-brulee"),
            ["join"] = (" alpha\r\n beta\n", "alpha beta"),
            ["clean"] = ("  alpha \t beta \r\n \r\n\r\n gamma  ", "alpha beta\n\ngamma"),
            ["splitsentences"] = ("One. Two? Three!", "One.\nTwo?\nThree!"),
            ["collapseblanks"] = ("a\r\n\r\n\r\nb", "a\n\nb"),
            ["sortaz"] = ("b\nA\nc", "A\nb\nc"),
            ["sortza"] = ("b\nA\nc", "c\nb\nA"),
            ["unique"] = ("a\nb\na", "a\nb"),
            ["reverse"] = ("a\nb\nc", "c\nb\na"),
            ["trimlines"] = (" a \n\tb\t", "a\nb"),
            ["numberlines"] = ("alpha\nbeta", "1. alpha\n2. beta"),
            ["curly"] = ("\"Hello\" 'world' can't", "“Hello” ‘world’ can’t"),
            ["straight"] = ("“Hello”—‘world’…", "\"Hello\"--'world'..."),
            ["emdash"] = ("left--right", "left—right"),
            ["ellipsis"] = ("wait...", "wait…"),
            ["deaccent"] = ("Crème Æsir Straße", "Creme AEsir Strasse"),
            ["dblspace"] = ("a   b\t\tc", "a b c"),
            ["codeblock"] = ("x = 1;", "```\nx = 1;\n```"),
            ["b64enc"] = ("hello", "aGVsbG8="),
            ["b64dec"] = ("aGVsbG8=", "hello"),
            ["urlenc"] = ("a b+c", "a%20b%2Bc"),
            ["urldec"] = ("a%20b%2Bc", "a b+c"),
            ["htmlenc"] = ("<tag>&", "&lt;tag&gt;&amp;"),
            ["htmldec"] = ("&lt;tag&gt;&amp;", "<tag>&"),
            ["md5"] = ("abc", "900150983cd24fb0d6963f7d28e17f72"),
            ["sha256"] = ("abc", "ba7816bf8f01cfea414140de5dae2223b00361a396177a9cb410ff61f20015ad"),
            ["jsonpretty"] = ("{\"a\":1,}", "{" + Environment.NewLine + "  \"a\": 1" + Environment.NewLine + "}"),
            ["jsonmin"] = ("{ \"a\": 1 }", "{\"a\":1}"),
            ["jsonescape"] = ("line\n\"quoted\"", "\"line\\n\\\"quoted\\\"\""),
        };

        AssertSequenceEqual(Transforms.Keys, cases.Keys, "transform fixture keys");
        foreach (var key in Transforms.Keys)
        {
            var fixture = cases[key];
            AssertEqual(fixture.Expected, Transforms.Run(fixture.Input, key), key);
        }
        AssertThrows<ArgumentException>(() => Transforms.Run("fixture", "unsupported"), "unknown transform");
    }

    static void TestSettingsRoundTrip()
    {
        var expected = new Settings
        {
            RoleQuick = "synthetic-quick:model",
            RolePrecise = "synthetic-precise:model",
            RoleVision = "synthetic-vision:model",
            EndpointOpenAI = "https://example.invalid/v1",
            EndpointLocal = "http://127.0.0.1:11434/v1",
            NextcloudRoot = "X:\\Synthetic\\Notes",
            DownloadsDir = "X:\\Synthetic\\Downloads",
            RouterEnabled = true,
            TriggerCtrl = false,
            TriggerAlt = true,
            QuickHotkeys = false,
            PastePlainHotkey = true,
            ClipboardHistory = false,
            NtfyUrl = "https://example.invalid/ntfy",
            NtfyTopic = "synthetic-topic",
            PaperlessUrl = "https://example.invalid/paperless",
            FleetHosts = "synthetic-a,synthetic-b",
            ScreenshotDir = "X:\\Synthetic\\Screenshots",
            FirstRunDone = true,
            UpdateCheck = false,
            LastUpdateCheck = "2000-01-02T03:04:05Z",
        };

        expected.Save();
        var actual = Settings.Load();
        AssertEqual(expected.RoleQuick, actual.RoleQuick, "quick role");
        AssertEqual(expected.RolePrecise, actual.RolePrecise, "precise role");
        AssertEqual(expected.RoleVision, actual.RoleVision, "vision role");
        AssertEqual(expected.EndpointOpenAI, actual.EndpointOpenAI, "OpenAI endpoint");
        AssertEqual(expected.EndpointLocal, actual.EndpointLocal, "local endpoint");
        AssertEqual(expected.NextcloudRoot, actual.NextcloudRoot, "notes root");
        AssertEqual(expected.DownloadsDir, actual.DownloadsDir, "downloads directory");
        AssertEqual(expected.RouterEnabled, actual.RouterEnabled, "router enabled");
        AssertEqual(expected.TriggerCtrl, actual.TriggerCtrl, "Ctrl trigger");
        AssertEqual(expected.TriggerAlt, actual.TriggerAlt, "Alt trigger");
        AssertEqual(expected.QuickHotkeys, actual.QuickHotkeys, "quick hotkeys");
        AssertEqual(expected.PastePlainHotkey, actual.PastePlainHotkey, "plain-paste hotkey");
        AssertEqual(expected.ClipboardHistory, actual.ClipboardHistory, "clipboard history");
        AssertEqual(expected.NtfyUrl, actual.NtfyUrl, "ntfy URL");
        AssertEqual(expected.NtfyTopic, actual.NtfyTopic, "ntfy topic");
        AssertEqual(expected.PaperlessUrl, actual.PaperlessUrl, "paperless URL");
        AssertEqual(expected.FleetHosts, actual.FleetHosts, "fleet hosts");
        AssertEqual(expected.ScreenshotDir, actual.ScreenshotDir, "screenshot directory");
        AssertEqual(expected.FirstRunDone, actual.FirstRunDone, "first-run state");
        AssertEqual(expected.UpdateCheck, actual.UpdateCheck, "update check");
        AssertEqual(expected.LastUpdateCheck, actual.LastUpdateCheck, "last update check");
    }

    static void TestIniEntries()
    {
        var path = Paths.File_("synthetic.ini");
        File.WriteAllText(path, "; synthetic fixture\n[First]\nKey = one\n# ignored\n[Second]\nValue=two\n");
        var ini = Ini.Load(path);
        AssertEqual("one", ini.Get("first", "key"), "case-insensitive INI read");
        AssertEqual("fallback", ini.Get("missing", "key", "fallback"), "INI default");
        ini.Set("First", "Added", "three");
        ini.RemoveSection("SECOND");
        ini.Save(path);

        var saved = Ini.Load(path);
        AssertEqual("three", saved.Get("first", "added"), "saved INI entry");
        Assert(!saved.Sections.Any(x => x.Name.Equals("Second", StringComparison.OrdinalIgnoreCase)), "removed INI section remained");
    }

    static void TestRecipes()
    {
        File.WriteAllText(Paths.Recipes, "; synthetic recipes\n");
        Recipes.Save("", "First recipe", "clean,curly");
        AssertEqual("clean,curly", Recipes.Steps("first recipe"), "saved recipe");
        Recipes.Save("First recipe", "Renamed recipe", "trimlines,unique");
        AssertEqual("", Recipes.Steps("First recipe"), "old recipe name");
        AssertEqual("trimlines,unique", Recipes.All().Single().Steps, "renamed recipe");
        Recipes.Delete("RENAMED RECIPE");
        AssertEqual(0, Recipes.All().Count, "deleted recipe count");
    }

    static void TestSnippets()
    {
        File.WriteAllText(Snippets.File_, "; synthetic snippets\n");
        Snippets.Save("Fixture", "first\r\nsecond\\tail");
        AssertEqual("first\nsecond\\tail", Snippets.All().Single().Text, "snippet round-trip");
        Snippets.Delete("fixture");
        AssertEqual(0, Snippets.All().Count, "deleted snippet count");
    }

    static void TestSpells()
    {
        File.WriteAllText(Spells.File_, "; synthetic spells\n");
        var prompt = new Spell("Prompt fixture", "prompt", "Line one\nLine two \\ tail", "review", "prose", "precise", "", "", "POST", "", "{text}", false, false);
        var command = new Spell("Command fixture", "command", "", "show", "none", "quick", "synthetic-command\nsecond", "", "POST", "", "{text}", false, false);
        var http = new Spell("HTTP fixture", "http", "", "replace", "none", "quick", "", "https://example.invalid/{filename}", "POST", "X-Fixture: yes\nContent-Type: application/json", "{\"text\": {json}}", true, true);
        foreach (var spell in new[] { prompt, command, http }) Spells.Save(spell);

        var actual = Spells.All();
        AssertEqual(3, actual.Count, "spell count");
        AssertEqual(prompt, actual.Single(x => x.Name == prompt.Name), "prompt spell");
        AssertEqual(command, actual.Single(x => x.Name == command.Name), "command spell");
        AssertEqual(http, actual.Single(x => x.Name == http.Name), "HTTP spell");
        Assert(Spells.Exists("prompt FIXTURE"), "spell lookup was not case-insensitive");
        var unsupported = new Spell("Unsupported", "unsupported", "", "show", "none", "quick", "", "", "POST", "", "{text}", false, false);
        AssertThrows<InvalidOperationException>(() => Spells.RunText(unsupported, "synthetic text"), "unsupported spell kind");
        Spells.Delete("PROMPT FIXTURE");
        Assert(!Spells.Exists("Prompt fixture"), "deleted spell remained");
    }

    static void Assert(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }

    static void AssertEqual<T>(T expected, T actual, string subject)
    {
        if (!EqualityComparer<T>.Default.Equals(expected, actual))
            throw new InvalidOperationException($"{subject}: expected <{expected}>, got <{actual}>");
    }

    static void AssertSequenceEqual(IEnumerable<string> expected, IEnumerable<string> actual, string subject)
    {
        var expectedArray = expected.ToArray();
        var actualArray = actual.ToArray();
        if (!expectedArray.SequenceEqual(actualArray, StringComparer.Ordinal))
            throw new InvalidOperationException($"{subject}: expected <{string.Join(",", expectedArray)}>, got <{string.Join(",", actualArray)}>");
    }

    static void AssertThrows<T>(Action action, string subject) where T : Exception
    {
        try { action(); }
        catch (T) { return; }
        catch (Exception e) { throw new InvalidOperationException($"{subject}: expected {typeof(T).Name}, got {e.GetType().Name}"); }
        throw new InvalidOperationException($"{subject}: expected {typeof(T).Name}, but no exception was thrown");
    }

    static void Fail(string name, string detail)
    {
        _failures++;
        Record("FAIL", $"{name}: {detail}");
    }

    static void Record(string status, string message)
    {
        var line = $"{status}: {message}";
        Results.Add(line);
        Console.WriteLine(line);
    }

    static void WriteReport(string? path)
    {
        Results.Add($"Result: {_failures} failure(s)");
        var text = string.Join(Environment.NewLine, Results) + Environment.NewLine;
        if (path != null)
        {
            var fullPath = Path.GetFullPath(path);
            Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);
            File.WriteAllText(fullPath, text);
        }
        Console.WriteLine($"Result: {_failures} failure(s)");
    }
}
