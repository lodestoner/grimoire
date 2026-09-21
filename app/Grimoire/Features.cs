using System.Diagnostics;
using System.Globalization;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Spellbook;

/// <summary>Capture a selection into a notes tree, optionally classified into a subfolder by AI.</summary>
internal static class Notes
{
    public static List<string> Areas(string root)
    {
        if (!Directory.Exists(root)) return new();
        return Directory.GetDirectories(root)
            .Select(Path.GetFileName)
            .Where(n => !string.IsNullOrWhiteSpace(n) && !n.StartsWith('.'))
            .Select(n => n!)
            .OrderBy(n => n, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    public static string Capture(string text, string root)
    {
        if (string.IsNullOrWhiteSpace(root) || !Directory.Exists(root))
            throw new InvalidOperationException("Set the notes folder in Settings first.");
        var areas = Areas(root);
        string area = "", slug = "capture";
        var tags = new List<string>();
        var prompt = areas.Count > 0
            ? "You are a filing assistant. Classify the following captured text into exactly ONE of these area folders:\n" + string.Join(", ", areas) + "\n\n" +
              "Respond with STRICT JSON ONLY, no preamble, no markdown fences, in this exact shape:\n" +
              "{\"area\":\"<one of the area folder names exactly>\",\"slug\":\"<short snake_case or kebab-case context, 2-5 words>\",\"tags\":[\"tag1\",\"tag2\"]}\n\nTEXT:\n"
            : "Give this captured text a short file name and tags. Respond with STRICT JSON ONLY, no preamble, no markdown fences, in this exact shape:\n" +
              "{\"slug\":\"<short snake_case or kebab-case context, 2-5 words>\",\"tags\":[\"tag1\",\"tag2\"]}\n\nTEXT:\n";
        var resp = Ai.Run(AiRole.Quick, text, prompt);
        var m = Regex.Match(resp, @"\{.*\}", RegexOptions.Singleline);
        if (m.Success)
        {
            try
            {
                using var doc = JsonDocument.Parse(m.Value);
                var r = doc.RootElement;
                if (r.TryGetProperty("area", out var a) && a.ValueKind == JsonValueKind.String)
                {
                    var hit = areas.FirstOrDefault(x => x.Equals(a.GetString(), StringComparison.OrdinalIgnoreCase));
                    if (hit != null) area = hit;
                }
                if (r.TryGetProperty("slug", out var sl) && sl.ValueKind == JsonValueKind.String)
                {
                    var s = (sl.GetString() ?? "").ToLowerInvariant().Trim();
                    s = Regex.Replace(s, "[^a-z0-9_\\-]+", "-").Trim('-', '_');
                    if (s.Length > 0) slug = s;
                }
                if (r.TryGetProperty("tags", out var t) && t.ValueKind == JsonValueKind.Array)
                    foreach (var e in t.EnumerateArray()) if (e.ValueKind == JsonValueKind.String) tags.Add(e.GetString() ?? "");
            }
            catch { }
        }
        if (area.Length == 0 && areas.Count > 0) area = areas[0];
        var date = DateTime.Now.ToString("yyyy-MM-dd");
        var dir = area.Length > 0 ? Path.Combine(root, area) : root;
        Directory.CreateDirectory(dir);
        var path = Path.Combine(dir, date + "_" + slug + ".md");
        int i = 1;
        while (File.Exists(path)) path = Path.Combine(dir, $"{date}_{slug} ({i++}).md");
        var note = $"---\ndate: {date}\ntags: [{string.Join(", ", tags)}]\nsource: {Brand.Name.ToLowerInvariant()} capture\n---\n\n{text}\n";
        File.WriteAllText(path, note, new UTF8Encoding(false));
        return path;
    }
}

/// <summary>Reminders: desktop popup (Task Scheduler), phone push (ntfy), or a todo line in the notes folder.</summary>
internal static class Reminders
{
    public const string TargetPopup = "Desktop popup", TargetNtfy = "Phone push (ntfy)", TargetTodo = "Notes todo (reminders.md)";

    public static string[] Targets(Settings s)
    {
        var list = new List<string> { TargetPopup };
        if (Ntfy.Configured(s)) list.Add(TargetNtfy);
        if (!string.IsNullOrWhiteSpace(s.NextcloudRoot)) list.Add(TargetTodo);
        return list.ToArray();
    }

    public static DateTime? ParseWhen(string when)
    {
        when = when.Trim();
        var m = Regex.Match(when, @"^\+\s*(\d+)\s*([mhdMHD])\s*$");
        if (m.Success)
        {
            int n = int.Parse(m.Groups[1].Value);
            return m.Groups[2].Value.ToLowerInvariant() switch { "m" => DateTime.Now.AddMinutes(n), "h" => DateTime.Now.AddHours(n), _ => DateTime.Now.AddDays(n) };
        }
        foreach (var fmt in new[] { "yyyy-MM-dd HH:mm", "yyyy-MM-dd H:mm", "yyyy-MM-ddTHH:mm", "HH:mm", "H:mm" })
            if (DateTime.TryParseExact(when, fmt, CultureInfo.InvariantCulture, DateTimeStyles.None, out var d))
                return fmt.StartsWith("H") && d < DateTime.Now ? d.AddDays(1) : d;
        if (DateTime.TryParse(when, CultureInfo.InvariantCulture, DateTimeStyles.None, out var d2)) return d2;
        return null;
    }

    public static string Create(string text, string when, string target, Settings s)
    {
        text = text.Trim();
        if (text.Length == 0) return "[x] No reminder text.";
        var due = ParseWhen(when);
        if (due == null) return "[x] Could not parse 'When': " + when + "\nUse +30m / +2h / +1d, HH:mm, or yyyy-MM-dd HH:mm";
        var dueDisp = due.Value.ToString("yyyy-MM-dd HH:mm");

        if (target == TargetPopup)
        {
            var id = due.Value.ToString("yyyyMMdd-HHmm") + "-" + Random.Shared.Next(0, 9999).ToString("0000");
            var dir = Path.Combine(Paths.DataDir, "reminders");
            Directory.CreateDirectory(dir);
            var file = Path.Combine(dir, id + ".txt");
            File.WriteAllText(file, text, new UTF8Encoding(false));
            var tn = Brand.ReminderTaskPrefix + id;
            var psi = new ProcessStartInfo("schtasks.exe") { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
            foreach (var a in new[] { "/create", "/tn", tn, "/sc", "once", "/st", due.Value.ToString("HH:mm"), "/sd", due.Value.ToString("MM/dd/yyyy"), "/tr", $"\"{Paths.ExePath}\" --remind \"{file}\"", "/f" })
                psi.ArgumentList.Add(a);
            using var p = Process.Start(psi)!;
            var o = p.StandardOutput.ReadToEnd() + p.StandardError.ReadToEnd();
            p.WaitForExit();
            return p.ExitCode == 0 ? $"Desktop popup scheduled for {dueDisp}.\nTask: {tn}" : "[x] schtasks failed:\n" + o.Trim();
        }
        if (target == TargetNtfy)
            return Ntfy.Push(s, text, Brand.Name + " reminder", due);
        if (target == TargetTodo)
        {
            if (string.IsNullOrWhiteSpace(s.NextcloudRoot) || !Directory.Exists(s.NextcloudRoot)) return "[x] Set the notes root in Settings first.";
            var md = Path.Combine(s.NextcloudRoot, "reminders.md");
            if (!File.Exists(md)) File.WriteAllText(md, "# Reminders\n\n", new UTF8Encoding(false));
            var one = Regex.Replace(text, @"\r?\n", " ").Trim();
            File.AppendAllText(md, $"- [ ] {one}  (due {dueDisp})\n", new UTF8Encoding(false));
            return $"Added todo (due {dueDisp}):\n{md}";
        }
        return "[x] Unknown target: " + target;
    }

    /// <summary>The popup shown by the scheduled task ("<exe> --remind file").</summary>
    public static void ShowReminder(string file)
    {
        string text;
        try { text = File.ReadAllText(file, Encoding.UTF8); } catch { text = "(reminder text missing)"; }
        using var owner = new Form { TopMost = true, ShowInTaskbar = false, StartPosition = FormStartPosition.CenterScreen, Size = new Size(1, 1), Opacity = 0 };
        owner.Show();
        MessageBox.Show(owner, text, Brand.Name + " reminder", MessageBoxButtons.OK, MessageBoxIcon.Information);
        try { File.Delete(file); } catch { }
    }
}

/// <summary>Run a command across SSH hosts, collecting per-host output. Hosts come from Settings (host:description;...).</summary>
internal static class Fleet
{
    public static List<(string Host, string Desc)> Hosts(Settings s)
    {
        var list = new List<(string, string)>();
        foreach (var part in (s.FleetHosts ?? "").Split(';', StringSplitOptions.RemoveEmptyEntries))
        {
            var idx = part.IndexOf(':');
            var host = (idx < 0 ? part : part[..idx]).Trim();
            var desc = idx < 0 ? "" : part[(idx + 1)..].Trim();
            if (host.Length > 0) list.Add((host, desc));
        }
        return list;
    }

    public static string Ssh(string host, string cmd, int timeoutMs = 60_000)
    {
        var psi = new ProcessStartInfo("ssh") { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true, StandardOutputEncoding = Encoding.UTF8, StandardErrorEncoding = Encoding.UTF8 };
        foreach (var a in new[] { "-o", "ConnectTimeout=8", "-o", "BatchMode=yes", host, cmd }) psi.ArgumentList.Add(a);
        try
        {
            using var p = Process.Start(psi)!;
            var o = p.StandardOutput.ReadToEndAsync();
            var e = p.StandardError.ReadToEndAsync();
            if (!p.WaitForExit(timeoutMs)) { try { p.Kill(true); } catch { } return "[timeout]"; }
            return (o.Result + e.Result).TrimEnd();
        }
        catch (Exception ex) { return "[error] " + ex.Message; }
    }

    public static string Run(IEnumerable<string> hosts, string cmd)
    {
        var sb = new StringBuilder();
        sb.AppendLine("$ " + cmd).AppendLine();
        foreach (var h in hosts)
        {
            sb.AppendLine("===== " + h + " =====");
            var o = Ssh(h, cmd);
            sb.AppendLine(string.IsNullOrWhiteSpace(o) ? "(no output)" : o).AppendLine();
        }
        return sb.ToString();
    }
}

internal static class PageSource
{
    static readonly HttpClient Http = new(new HttpClientHandler { AllowAutoRedirect = true }) { Timeout = TimeSpan.FromSeconds(20) };

    public static string Fetch(string url)
    {
        if (!Uri.TryCreate(url.Trim(), UriKind.Absolute, out var u) || (u.Scheme != "http" && u.Scheme != "https"))
            return "Not a valid http/https URL: " + url;
        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Get, u);
            req.Headers.UserAgent.ParseAdd("Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/124.0 Safari/537.36");
            using var resp = Http.Send(req);
            return resp.Content.ReadAsStringAsync().Result;
        }
        catch (Exception e) { return "Could not fetch page source.\r\n\r\n" + e.Message; }
    }
}

/// <summary>Vision: a Snipping Tool region on the clipboard → the vision provider reads the PNG.</summary>
internal static class Vision
{
    public static void LaunchSnip()
    {
        try { Process.Start(new ProcessStartInfo("explorer.exe", "ms-screenclip:") { UseShellExecute = true }); }
        catch { }
    }

    public static string SavePng(Image img, string dir, string prefix)
    {
        Directory.CreateDirectory(dir);
        var png = Path.Combine(dir, prefix + DateTime.Now.ToString("yyyy-MM-dd_HHmmss") + ".png");
        int i = 1;
        while (File.Exists(png)) png = Path.Combine(dir, prefix + DateTime.Now.ToString("yyyy-MM-dd_HHmmss") + $"_{i++}.png");
        img.Save(png, System.Drawing.Imaging.ImageFormat.Png);
        return png;
    }

    /// <summary>mode: ocr | reproduce | describe</summary>
    public static string Run(Image img, string mode)
    {
        var dir = Path.Combine(Path.GetTempPath(), Brand.Name);
        Directory.CreateDirectory(dir);
        var png = Path.Combine(dir, "vision_" + Guid.NewGuid().ToString("N") + ".png");
        img.Save(png, System.Drawing.Imaging.ImageFormat.Png);
        try
        {
            var instr = mode switch
            {
                "ocr" => "Transcribe ALL text in this image exactly, preserving line breaks. Output only the text:",
                "reproduce" => "Reproduce this UI as clean minimal HTML+CSS. Output only the code:",
                _ => "Describe this image concisely: what it shows, any text, and anything notable.",
            };
            return Ai.Run(AiRole.Vision, "", instr, imagePath: png);
        }
        finally { try { File.Delete(png); } catch { } }
    }
}
