using System.Text;

namespace Spellbook;

/// <summary>Where the editable data lives (recipes, spells, styles, settings).</summary>
internal static class Paths
{
    static string? _data;

    public static string DataDir => _data ??= Resolve();

    static string Resolve()
    {
        var env = Environment.GetEnvironmentVariable(Brand.EnvPrefix + "HOME");
        if (!string.IsNullOrWhiteSpace(env) && Directory.Exists(env)) return env;
        // portable/repo mode: the exe sits in or under a folder that holds recipes.ini + style-prose.md
        var dir = AppContext.BaseDirectory.TrimEnd('\\', '/');
        for (int i = 0; i < 4 && !string.IsNullOrEmpty(dir); i++)
        {
            if (File.Exists(Path.Combine(dir, "recipes.ini")) && File.Exists(Path.Combine(dir, "style-prose.md"))) return dir;
            dir = Path.GetDirectoryName(dir) ?? "";
        }
        return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), Brand.DataDirName);
    }

    public static string File_(string name) => Path.Combine(DataDir, name);
    public static string Settings => File_(Brand.SettingsFile);
    public static string Recipes => File_("recipes.ini");
    public static string StyleProse => File_("style-prose.md");
    public static string StyleCode => File_("style-code.md");
    public static string ExePath => Environment.ProcessPath ?? Path.Combine(AppContext.BaseDirectory, Brand.Exe);

    /// <summary>First run: seed the data folder from the embedded defaults.</summary>
    public static void EnsureData()
    {
        Directory.CreateDirectory(DataDir);
        foreach (var n in new[] { "recipes.ini", "style-prose.md", "style-code.md", "spells.ini", "snippets.ini" }) Seed(n);
    }

    static void Seed(string name)
    {
        var path = File_(name);
        if (File.Exists(path)) return;
        using var s = typeof(Paths).Assembly.GetManifestResourceStream("Defaults." + name);
        if (s == null) { File.WriteAllText(path, ""); return; }
        using var f = File.Create(path);
        s.CopyTo(f);
    }
}

/// <summary>Tiny INI reader/writer (sections, key=value, ';' comments).</summary>
internal sealed class Ini
{
    public readonly List<string> Header = new();
    public readonly List<(string Name, Dictionary<string, string> Keys)> Sections = new();

    public static Ini Load(string path)
    {
        var ini = new Ini();
        if (!File.Exists(path)) return ini;
        Dictionary<string, string>? cur = null;
        foreach (var raw in File.ReadAllLines(path, Encoding.UTF8))
        {
            var line = raw.Trim();
            if (line.StartsWith('[') && line.EndsWith(']'))
            {
                cur = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                ini.Sections.Add((line[1..^1].Trim(), cur));
            }
            else if (cur == null) ini.Header.Add(raw);
            else if (line.Length > 0 && !line.StartsWith(';') && !line.StartsWith('#'))
            {
                int eq = line.IndexOf('=');
                if (eq > 0) cur[line[..eq].Trim()] = line[(eq + 1)..].Trim();
            }
        }
        return ini;
    }

    public string Get(string section, string key, string def = "")
    {
        foreach (var s in Sections)
            if (s.Name.Equals(section, StringComparison.OrdinalIgnoreCase) && s.Keys.TryGetValue(key, out var v)) return v;
        return def;
    }

    public void Set(string section, string key, string value)
    {
        foreach (var s in Sections)
            if (s.Name.Equals(section, StringComparison.OrdinalIgnoreCase)) { s.Keys[key] = value; return; }
        Sections.Add((section, new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { [key] = value }));
    }

    public void RemoveSection(string section) => Sections.RemoveAll(s => s.Name.Equals(section, StringComparison.OrdinalIgnoreCase));

    public void Save(string path, Action<string, string>? commit = null)
    {
        var sb = new StringBuilder();
        foreach (var h in Header) sb.AppendLine(h);
        if (Header.Count > 0 && !string.IsNullOrWhiteSpace(Header[^1])) sb.AppendLine();
        foreach (var s in Sections)
        {
            sb.Append('[').Append(s.Name).AppendLine("]");
            foreach (var kv in s.Keys) sb.Append(kv.Key).Append('=').AppendLine(kv.Value);
            sb.AppendLine();
        }
        AtomicFile.Write(path, sb.ToString().TrimEnd() + Environment.NewLine, commit);
    }
}

/// <summary>Write and flush beside the target, then atomically replace it. Failed writes keep the old file.</summary>
internal static class AtomicFile
{
    internal static void Write(string path, string content, Action<string, string>? commit = null, Func<bool>? unchanged = null)
    {
        var folder = Path.GetDirectoryName(Path.GetFullPath(path))!; Directory.CreateDirectory(folder);
        var temp = Path.Combine(folder, ".grimoire-" + Guid.NewGuid().ToString("N") + ".tmp");
        try
        {
            using (var stream = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                var bytes = new UTF8Encoding(false).GetBytes(content); stream.Write(bytes); stream.Flush(true);
            }
            if (unchanged != null && !unchanged()) throw new InvalidOperationException("The file changed. Refresh before trying again.");
            if (commit != null) commit(temp, path);
            else if (File.Exists(path)) File.Replace(temp, path, null);
            else File.Move(temp, path);
        }
        finally { if (File.Exists(temp)) File.Delete(temp); }
    }
}

internal sealed class Settings
{
    // AI roles: "providerId:model"
    public string RoleQuick = "";
    public string RolePrecise = "";
    public string RoleVision = "";
    public string EndpointOpenAI = "";
    public string EndpointLocal = "http://localhost:11434/v1";

    public string NextcloudRoot = "";
    public string DownloadsDir = "";
    public bool RouterEnabled;

    public bool TriggerCtrl = true;
    public bool TriggerAlt = true;
    public bool QuickHotkeys = true;
    public bool PastePlainHotkey;
    public bool ClipboardHistory;

    public string NtfyUrl = "";
    public string NtfyTopic = "";
    public string PaperlessUrl = "";
    public string FleetHosts = "";
    public string ScreenshotDir = "";

    public bool FirstRunDone;
    public bool UpdateCheck = true;
    public string LastUpdateCheck = "";

    public static Settings Load()
    {
        var ini = Ini.Load(Paths.Settings);
        var s = new Settings
        {
            RoleQuick = ini.Get("ai", "quick"),
            RolePrecise = ini.Get("ai", "precise"),
            RoleVision = ini.Get("ai", "vision"),
            EndpointOpenAI = ini.Get("ai", "openai_endpoint"),
            EndpointLocal = ini.Get("ai", "local_endpoint", "http://localhost:11434/v1"),
            RouterEnabled = ini.Get("router", "enabled", "0") == "1",
            NextcloudRoot = ini.Get("paths", "nextcloud"),
            DownloadsDir = ini.Get("paths", "downloads"),
            ScreenshotDir = ini.Get("paths", "screenshots"),
            TriggerCtrl = ini.Get("hotkeys", "ctrl_rightclick", "1") == "1",
            TriggerAlt = ini.Get("hotkeys", "alt_rightclick", "1") == "1",
            QuickHotkeys = ini.Get("hotkeys", "quick", "1") == "1",
            PastePlainHotkey = ini.Get("hotkeys", "paste_plain", "0") == "1",
            ClipboardHistory = ini.Get("clipboard", "history",
                ini.Get("general", "first_run_done", "0") == "1" || ini.Sections.Any(x => x.Name.Equals("claude", StringComparison.OrdinalIgnoreCase)) ? "1" : "0") == "1",
            NtfyUrl = ini.Get("ntfy", "url"),
            NtfyTopic = ini.Get("ntfy", "topic"),
            PaperlessUrl = ini.Get("paperless", "url"),
            FleetHosts = ini.Get("fleet", "hosts"),
            FirstRunDone = ini.Get("general", "first_run_done", "0") == "1",
            UpdateCheck = ini.Get("general", "update_check", "1") == "1",
            LastUpdateCheck = ini.Get("general", "last_update_check"),
        };
        // migrate the 2.0 layout ([claude] model/precise_model) onto roles
        if (string.IsNullOrWhiteSpace(s.RoleQuick) && ini.Sections.Any(x => x.Name.Equals("claude", StringComparison.OrdinalIgnoreCase)))
        {
            s.RoleQuick = "claude-cli:" + ini.Get("claude", "model");
            s.RolePrecise = "claude-cli:" + ini.Get("claude", "precise_model", "opus");
            s.RoleVision = "claude-cli:";
            s.FirstRunDone = true;
        }
        if (string.IsNullOrWhiteSpace(s.DownloadsDir))
            s.DownloadsDir = Native.KnownFolder(Native.FOLDERID_Downloads) ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads");
        if (string.IsNullOrWhiteSpace(s.ScreenshotDir))
            s.ScreenshotDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyPictures), "Screenshots");
        return s;
    }

    internal Settings Copy() => (Settings)MemberwiseClone();

    public void Save()
    {
        var ini = Ini.Load(Paths.Settings);
        ini.RemoveSection("claude");
        ini.Set("general", "first_run_done", FirstRunDone ? "1" : "0");
        ini.Set("general", "update_check", UpdateCheck ? "1" : "0");
        ini.Set("general", "last_update_check", LastUpdateCheck);
        ini.Set("ai", "quick", RoleQuick.Trim());
        ini.Set("ai", "precise", RolePrecise.Trim());
        ini.Set("ai", "vision", RoleVision.Trim());
        ini.Set("ai", "openai_endpoint", EndpointOpenAI.Trim());
        ini.Set("ai", "local_endpoint", EndpointLocal.Trim());
        ini.Set("router", "enabled", RouterEnabled ? "1" : "0");
        ini.Set("paths", "nextcloud", NextcloudRoot.Trim());
        ini.Set("paths", "downloads", DownloadsDir.Trim());
        ini.Set("paths", "screenshots", ScreenshotDir.Trim());
        ini.Set("hotkeys", "ctrl_rightclick", TriggerCtrl ? "1" : "0");
        ini.Set("hotkeys", "alt_rightclick", TriggerAlt ? "1" : "0");
        ini.Set("hotkeys", "quick", QuickHotkeys ? "1" : "0");
        ini.Set("hotkeys", "paste_plain", PastePlainHotkey ? "1" : "0");
        ini.Set("clipboard", "history", ClipboardHistory ? "1" : "0");
        ini.Set("ntfy", "url", NtfyUrl.Trim());
        ini.Set("ntfy", "topic", NtfyTopic.Trim());
        ini.Set("paperless", "url", PaperlessUrl.Trim());
        ini.Set("fleet", "hosts", FleetHosts.Trim());
        ini.Save(Paths.Settings);
    }
}

internal static class Recipes
{
    public static List<(string Name, string Steps)> All()
    {
        var ini = Ini.Load(Paths.Recipes);
        return ini.Sections.Select(s => (s.Name, s.Keys.TryGetValue("steps", out var v) ? v : "")).ToList();
    }

    public static string Steps(string name) => Ini.Load(Paths.Recipes).Get(name, "steps");

    public static void Save(string oldName, string newName, string steps)
    {
        var ini = Ini.Load(Paths.Recipes);
        if (!string.IsNullOrEmpty(oldName) && !oldName.Equals(newName, StringComparison.OrdinalIgnoreCase)) ini.RemoveSection(oldName);
        ini.Set(newName, "steps", steps);
        ini.Save(Paths.Recipes);
    }

    public static void Delete(string name)
    {
        var ini = Ini.Load(Paths.Recipes);
        ini.RemoveSection(name);
        ini.Save(Paths.Recipes);
    }
}

/// <summary>Start-with-Windows via the per-user Run key (no shortcut files, no scheduler).</summary>
internal static class Startup
{
    const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    const string Name = Brand.RunKeyName;

    public static bool IsEnabled()
    {
        using var k = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(RunKey);
        return k?.GetValue(Name) is string;
    }

    public static void Set(bool on)
    {
        using var k = Microsoft.Win32.Registry.CurrentUser.CreateSubKey(RunKey);
        if (on) k.SetValue(Name, "\"" + Paths.ExePath + "\"");
        else k.DeleteValue(Name, false);
    }
}
