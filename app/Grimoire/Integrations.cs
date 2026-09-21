using System.Net.Http.Headers;
using System.Text;

namespace Spellbook;

/// <summary>ntfy push notifications (self-hosted or ntfy.sh). Scheduled delivery is done by the ntfy server itself.</summary>
internal static class Ntfy
{
    static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(20) };

    public static bool Configured(Settings s) => !string.IsNullOrWhiteSpace(s.NtfyUrl) && !string.IsNullOrWhiteSpace(s.NtfyTopic);

    public static string Push(Settings s, string message, string title, DateTime? at = null, string? priority = null)
    {
        if (!Configured(s)) throw new InvalidOperationException("ntfy is not configured (Settings -> Integrations).");
        var url = s.NtfyUrl.TrimEnd('/') + "/" + s.NtfyTopic.Trim();
        using var req = new HttpRequestMessage(HttpMethod.Post, url);
        req.Headers.TryAddWithoutValidation("Title", title);
        if (at != null && at.Value > DateTime.Now.AddSeconds(15)) req.Headers.TryAddWithoutValidation("Delay", new DateTimeOffset(at.Value).ToUnixTimeSeconds().ToString());
        if (!string.IsNullOrEmpty(priority)) req.Headers.TryAddWithoutValidation("Priority", priority);
        var token = Credentials.Get("ntfy");
        if (!string.IsNullOrEmpty(token)) req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        req.Content = new StringContent(message, new UTF8Encoding(false), "text/plain");
        using var resp = Http.Send(req);
        var body = resp.Content.ReadAsStringAsync().Result;
        if (!resp.IsSuccessStatusCode) throw new InvalidOperationException($"ntfy HTTP {(int)resp.StatusCode}: {Util.Trunc(body, 200)}");
        return at != null ? $"Phone push scheduled via ntfy for {at:yyyy-MM-dd HH:mm}." : "Pushed to ntfy.";
    }
}

/// <summary>Paperless-ngx document upload.</summary>
internal static class Paperless
{
    static readonly HttpClient Http = new() { Timeout = TimeSpan.FromMinutes(5) };

    public static bool Configured(Settings s) => !string.IsNullOrWhiteSpace(s.PaperlessUrl) && Credentials.Has("paperless");

    public static string Upload(Settings s, string path, string? title = null)
    {
        if (!Configured(s)) throw new InvalidOperationException("Paperless is not configured (Settings -> Integrations).");
        var url = s.PaperlessUrl.TrimEnd('/') + "/api/documents/post_document/";
        using var form = new MultipartFormDataContent();
        var bytes = File.ReadAllBytes(path);
        var fc = new ByteArrayContent(bytes);
        fc.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
        form.Add(fc, "document", Path.GetFileName(path));
        form.Add(new StringContent(title ?? Path.GetFileNameWithoutExtension(path)), "title");
        using var req = new HttpRequestMessage(HttpMethod.Post, url) { Content = form };
        req.Headers.Authorization = new AuthenticationHeaderValue("Token", Credentials.Get("paperless"));
        using var resp = Http.Send(req);
        var body = resp.Content.ReadAsStringAsync().Result;
        if (!resp.IsSuccessStatusCode) throw new InvalidOperationException($"Paperless HTTP {(int)resp.StatusCode}: {Util.Trunc(body, 200)}");
        return "Uploaded to Paperless: " + Path.GetFileName(path) + " (task " + body.Trim('"') + ")";
    }

    public static string UploadText(Settings s, string text, string title)
    {
        var dir = Path.Combine(Path.GetTempPath(), Brand.Name); Directory.CreateDirectory(dir);
        var safe = System.Text.RegularExpressions.Regex.Replace(title, "[^A-Za-z0-9 _-]+", "").Trim();
        if (safe.Length == 0) safe = "capture";
        var p = Path.Combine(dir, safe + ".txt");
        File.WriteAllText(p, text, new UTF8Encoding(false));
        try { return Upload(s, p, title); } finally { try { File.Delete(p); } catch { } }
    }
}
