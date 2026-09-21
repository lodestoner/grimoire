using System.Diagnostics;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace Spellbook;

internal enum AiRole { Quick, Precise, Vision }

internal sealed record AiRequest(string System, string Instruction, string Text, string? ImagePath, string Model);

/// <summary>One way of reaching a model: a vendor CLI you sign into, or an HTTP API with a key.</summary>
internal interface IProvider
{
    string Id { get; }
    string Label { get; }
    string Kind { get; }                 // "cli" or "api"
    bool SupportsImages { get; }
    string DefaultModel { get; }
    string[] ModelHints { get; }
    /// <summary>Short human status: "signed in", "installed, not signed in", "key set", "no key", "not installed".</summary>
    string Status();
    bool IsReady();
    string Complete(AiRequest r, int timeoutMs);
}

internal static class Providers
{
    public static readonly IProvider[] All =
    {
        new ClaudeCliProvider(), new CodexCliProvider(), new GeminiCliProvider(),
        new AnthropicApiProvider(), new OpenAiApiProvider(), new GeminiApiProvider(), new LocalOpenAiProvider(),
    };

    public static IProvider? ById(string id) => All.FirstOrDefault(p => p.Id.Equals(id, StringComparison.OrdinalIgnoreCase));

    // Keep saved provider IDs discoverable, but do not run adapters whose input boundary is unverified.
    public static string? DisabledReason(string id) =>
        id.Equals("codex-cli", StringComparison.OrdinalIgnoreCase) || id.Equals("gemini-cli", StringComparison.OrdinalIgnoreCase)
            ? "Temporarily unavailable: local file isolation is not verified. Choose another provider in AI providers."
            : null;

    // ------------------------------------------------------------ shared helpers
    public static string? FindOnPath(params string[] names)
    {
        var dirs = new List<string> { Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".local", "bin") };
        dirs.AddRange((Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries));
        var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        dirs.Add(Path.Combine(appData, "npm"));
        foreach (var d in dirs)
            foreach (var n in names)
            {
                try { var p = Path.Combine(d.Trim(), n); if (File.Exists(p)) return p; } catch { }
            }
        return null;
    }

    public static ProcessStartInfo Psi(string exe)
    {
        var psi = new ProcessStartInfo
        {
            UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true,
            StandardInputEncoding = new UTF8Encoding(false), StandardOutputEncoding = Encoding.UTF8, StandardErrorEncoding = Encoding.UTF8,
        };
        if (exe.EndsWith(".cmd", StringComparison.OrdinalIgnoreCase) || exe.EndsWith(".bat", StringComparison.OrdinalIgnoreCase))
        {
            // npm shims need cmd.exe, whose quoting expands shell metacharacters. Resolve only
            // known vendor entry points, never interpolate arbitrary prompt arguments into cmd.
            var package = Path.GetFileNameWithoutExtension(exe).ToLowerInvariant() switch
            {
                "claude" => "@anthropic-ai/claude-code/cli.js",
                "codex" => "@openai/codex/bin/codex.js",
                "gemini" => "@google/gemini-cli/dist/index.js",
                _ => null,
            };
            var directory = Path.GetDirectoryName(Path.GetFullPath(exe))!;
            var entry = package == null ? null : Path.Combine(directory, "node_modules", package.Replace('/', Path.DirectorySeparatorChar));
            var node = File.Exists(Path.Combine(directory, "node.exe")) ? Path.Combine(directory, "node.exe") : FindOnPath("node.exe");
            if (entry == null || !File.Exists(entry) || node == null)
                throw new InvalidOperationException("This CLI shim cannot be launched safely. Install the vendor's native executable, or reinstall its npm package with Node.js on PATH.");
            psi.FileName = node;
            psi.ArgumentList.Add(entry);
        }
        else psi.FileName = exe;
        // never let a Claude Code session env leak into a child CLI (it would refuse to nest)
        psi.Environment.Remove("CLAUDECODE");
        psi.Environment.Remove("CLAUDE_CODE_ENTRYPOINT");
        return psi;
    }

    public static (string stdout, string stderr, int code) RunProcess(ProcessStartInfo psi, string? stdin, int timeoutMs)
        => ProcessRunner.Run(psi, stdin, timeoutMs);

    /// <summary>Open a visible console for an interactive vendor login or install.</summary>
    public static void RunInConsole(string commandLine, string title)
    {
        var wt = FindOnPath("wt.exe");
        try
        {
            if (wt != null)
                Process.Start(new ProcessStartInfo(wt, $"--title \"{title}\" cmd /k \"{commandLine}\"") { UseShellExecute = true });
            else
                Process.Start(new ProcessStartInfo("cmd.exe", $"/k title {title} && {commandLine}") { UseShellExecute = true });
        }
        catch (Exception e) { Toast.Show("[x] " + e.Message, 4000); }
    }

    static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(300) };

    public static string PostJson(string url, JsonObject body, Action<HttpRequestHeaders> headers, int timeoutMs)
    {
        using var req = new HttpRequestMessage(HttpMethod.Post, url);
        headers(req.Headers);
        req.Content = new StringContent(body.ToJsonString(), new UTF8Encoding(false), "application/json");
        using var cts = OperationContext.Deadline(timeoutMs);
        using var resp = Http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, cts.Token).ConfigureAwait(false).GetAwaiter().GetResult();
        var text = resp.Content.ReadAsStringAsync(cts.Token).ConfigureAwait(false).GetAwaiter().GetResult();
        if (!resp.IsSuccessStatusCode)
        {
            string msg = text;
            try { msg = JsonNode.Parse(text)?["error"]?["message"]?.GetValue<string>() ?? text; } catch { }
            throw new InvalidOperationException($"HTTP {(int)resp.StatusCode}: {Util.Trunc(msg, 200)}");
        }
        return text;
    }

    public static (string mime, string b64) ImageData(string path)
    {
        var ext = Path.GetExtension(path).ToLowerInvariant();
        var mime = ext switch { ".jpg" or ".jpeg" => "image/jpeg", ".gif" => "image/gif", ".webp" => "image/webp", _ => "image/png" };
        return (mime, Convert.ToBase64String(File.ReadAllBytes(path)));
    }
}

// ============================================================ vendor CLIs (browser sign-in, no key)

internal sealed class ClaudeCliProvider : IProvider
{
    public string Id => "claude-cli";
    public string Label => "Claude (Claude Code sign-in)";
    public string Kind => "cli";
    public bool SupportsImages => true;
    public string DefaultModel => "";
    public string[] ModelHints => new[] { "", "sonnet", "opus", "haiku" };
    public static string? Exe => Providers.FindOnPath("claude.exe", "claude.cmd", "claude");
    public const string InstallUrl = "https://claude.com/claude-code";

    public string Status()
    {
        if (Exe == null) return "not installed";
        try
        {
            var psi = Providers.Psi(Exe); psi.ArgumentList.Add("auth"); psi.ArgumentList.Add("status");
            var (o, e, _) = Providers.RunProcess(psi, null, 15000);
            var all = o + e;
            if (Regex.IsMatch(all, "logged\\s*in\"?\\s*[:=]\\s*true|✔|logged in|authenticated", RegexOptions.IgnoreCase) && !Regex.IsMatch(all, "not logged in|loggedIn\"?\\s*:\\s*false", RegexOptions.IgnoreCase))
                return "signed in";
            return "installed, not signed in";
        }
        catch { return "installed"; }
    }

    public bool IsReady() => Exe != null;

    public string Complete(AiRequest r, int timeoutMs)
    {
        var exe = Exe ?? throw new InvalidOperationException("Claude Code is not installed. Providers -> Claude -> Install.");
        using var operation = new ProviderDirectory();
        var psi = BuildStartInfo(exe, r, operation.Path);
        var (output, _, _) = Providers.RunProcess(psi, r.Text, timeoutMs);
        return output;
    }

    internal static ProcessStartInfo BuildStartInfo(string exe, AiRequest request, string directory)
    {
        var psi = Providers.Psi(exe);
        psi.WorkingDirectory = directory;
        var instruction = request.Instruction;
        foreach (var arg in new[] { "--safe-mode", "--no-session-persistence", "--strict-mcp-config", "--tools", request.ImagePath == null ? "" : "Read" }) psi.ArgumentList.Add(arg);
        if (request.ImagePath != null)
        {
            // Only a copy of the explicitly selected image is placed in the isolated working directory.
            var image = Path.Combine(directory, "selected-image" + Path.GetExtension(request.ImagePath));
            File.Copy(request.ImagePath, image);
            instruction += "\n\nRead the selected image: " + image;
            psi.ArgumentList.Add("--allowedTools"); psi.ArgumentList.Add("Read(" + image.Replace('\\', '/') + ")");
        }
        psi.ArgumentList.Add("-p"); psi.ArgumentList.Add(instruction);
        psi.ArgumentList.Add("--output-format"); psi.ArgumentList.Add("text");
        if (!string.IsNullOrWhiteSpace(request.Model)) { psi.ArgumentList.Add("--model"); psi.ArgumentList.Add(request.Model.Trim()); }
        if (request.System.Length > 0) { psi.ArgumentList.Add("--append-system-prompt"); psi.ArgumentList.Add(request.System); }
        return psi;
    }
}

internal sealed class CodexCliProvider : IProvider
{
    public string Id => "codex-cli";
    public string Label => "ChatGPT (Codex CLI sign-in)";
    public string Kind => "cli";
    public bool SupportsImages => true;
    public string DefaultModel => "";
    public string[] ModelHints => new[] { "", "gpt-5", "gpt-5-codex", "o4-mini" };
    public static string? Exe => Providers.FindOnPath("codex.exe", "codex.cmd", "codex");
    public const string InstallUrl = "https://github.com/openai/codex";

    public string Status() => Providers.DisabledReason(Id)!;

    public bool IsReady() => false;

    public string Complete(AiRequest r, int timeoutMs) =>
        throw new InvalidOperationException(Id + ": " + Providers.DisabledReason(Id));

    internal static ProcessStartInfo BuildStartInfo(string exe, AiRequest request, string directory, string outputFile)
    {
        var psi = Providers.Psi(exe);
        psi.WorkingDirectory = directory;
        foreach (var arg in new[] { "exec", "-", "--sandbox", "read-only", "--ephemeral", "--ignore-user-config", "--skip-git-repo-check", "-C", directory, "--output-last-message", outputFile }) psi.ArgumentList.Add(arg);
        if (!string.IsNullOrWhiteSpace(request.Model)) { psi.ArgumentList.Add("-m"); psi.ArgumentList.Add(request.Model.Trim()); }
        if (request.ImagePath != null) { psi.ArgumentList.Add("-i"); psi.ArgumentList.Add(request.ImagePath); }
        return psi;
    }
}

internal sealed class GeminiCliProvider : IProvider
{
    public string Id => "gemini-cli";
    public string Label => "Gemini (Gemini CLI, Google sign-in)";
    public string Kind => "cli";
    public bool SupportsImages => false;
    public string DefaultModel => "";
    public string[] ModelHints => new[] { "", "gemini-2.5-pro", "gemini-2.5-flash" };
    public static string? Exe => Providers.FindOnPath("gemini.exe", "gemini.cmd", "gemini");
    public const string InstallUrl = "https://github.com/google-gemini/gemini-cli";

    public string Status() => Providers.DisabledReason(Id)!;

    public bool IsReady() => false;

    public string Complete(AiRequest r, int timeoutMs) =>
        throw new InvalidOperationException(Id + ": " + Providers.DisabledReason(Id));
}

// ============================================================ HTTP APIs (bring your own key)

internal sealed class AnthropicApiProvider : IProvider
{
    public string Id => "anthropic";
    public string Label => "Anthropic API (key)";
    public string Kind => "api";
    public bool SupportsImages => true;
    public string DefaultModel => "claude-sonnet-5";
    public string[] ModelHints => new[] { "claude-sonnet-5", "claude-opus-5", "claude-haiku-4-5-20251001" };
    public const string KeyUrl = "https://console.anthropic.com/settings/keys";
    public string Status() => Credentials.Has(Id) ? "key set" : "no key";
    public bool IsReady() => Credentials.Has(Id);

    public string Complete(AiRequest r, int timeoutMs)
    {
        var key = Credentials.Get(Id) ?? throw new InvalidOperationException("No Anthropic API key. Providers -> Anthropic API -> API key.");
        var content = new JsonArray();
        if (r.ImagePath != null)
        {
            var (mime, b64) = Providers.ImageData(r.ImagePath);
            content.Add(new JsonObject { ["type"] = "image", ["source"] = new JsonObject { ["type"] = "base64", ["media_type"] = mime, ["data"] = b64 } });
        }
        content.Add(new JsonObject { ["type"] = "text", ["text"] = r.Instruction + (r.Text.Length > 0 ? "\n\n" + r.Text : "") });
        var body = new JsonObject
        {
            ["model"] = string.IsNullOrWhiteSpace(r.Model) ? DefaultModel : r.Model.Trim(),
            ["max_tokens"] = 8192,
            ["messages"] = new JsonArray(new JsonObject { ["role"] = "user", ["content"] = content }),
        };
        if (r.System.Length > 0) body["system"] = r.System;
        var resp = Providers.PostJson("https://api.anthropic.com/v1/messages", body, h => { h.Add("x-api-key", key); h.Add("anthropic-version", "2023-06-01"); }, timeoutMs);
        var node = JsonNode.Parse(resp);
        var sb = new StringBuilder();
        foreach (var part in node?["content"]?.AsArray() ?? new JsonArray())
            if (part?["type"]?.GetValue<string>() == "text") sb.Append(part["text"]?.GetValue<string>());
        return sb.ToString();
    }
}

internal class OpenAiApiProvider : IProvider
{
    public virtual string Id => "openai";
    public virtual string Label => "OpenAI API (key)";
    public string Kind => "api";
    public bool SupportsImages => true;
    public virtual string DefaultModel => "gpt-4.1-mini";
    public virtual string[] ModelHints => new[] { "gpt-4.1-mini", "gpt-4.1", "gpt-5", "gpt-5-mini", "o4-mini" };
    public const string KeyUrl = "https://platform.openai.com/api-keys";
    public virtual string Endpoint => string.IsNullOrWhiteSpace(TrayApp.Current.Settings.EndpointOpenAI) ? "https://api.openai.com/v1" : TrayApp.Current.Settings.EndpointOpenAI.TrimEnd('/');
    public virtual string Status() => Credentials.Has(Id) ? "key set" : "no key";
    public virtual bool IsReady() => Credentials.Has(Id);

    public string Complete(AiRequest r, int timeoutMs)
    {
        var key = Credentials.Get(Id);
        if (string.IsNullOrEmpty(key) && Id == "openai") throw new InvalidOperationException("No OpenAI API key. Providers -> OpenAI API -> API key.");
        var userContent = new JsonArray();
        if (r.ImagePath != null)
        {
            var (mime, b64) = Providers.ImageData(r.ImagePath);
            userContent.Add(new JsonObject { ["type"] = "image_url", ["image_url"] = new JsonObject { ["url"] = $"data:{mime};base64,{b64}" } });
        }
        userContent.Add(new JsonObject { ["type"] = "text", ["text"] = r.Instruction + (r.Text.Length > 0 ? "\n\n" + r.Text : "") });
        var messages = new JsonArray();
        if (r.System.Length > 0) messages.Add(new JsonObject { ["role"] = "system", ["content"] = r.System });
        messages.Add(new JsonObject { ["role"] = "user", ["content"] = userContent });
        var body = new JsonObject { ["model"] = string.IsNullOrWhiteSpace(r.Model) ? DefaultModel : r.Model.Trim(), ["messages"] = messages };
        var resp = Providers.PostJson(Endpoint + "/chat/completions", body, h => { if (!string.IsNullOrEmpty(key)) h.Authorization = new AuthenticationHeaderValue("Bearer", key); }, timeoutMs);
        var node = JsonNode.Parse(resp);
        return node?["choices"]?[0]?["message"]?["content"]?.GetValue<string>() ?? "";
    }
}

/// <summary>Any OpenAI-compatible server: Ollama, LM Studio, llama.cpp, OpenRouter, vLLM.</summary>
internal sealed class LocalOpenAiProvider : OpenAiApiProvider
{
    public override string Id => "local";
    public override string Label => "Local / OpenAI-compatible server";
    public override string DefaultModel => "";
    public override string[] ModelHints => new[] { "llama3.2", "qwen2.5", "mistral", "gemma3" };
    public override string Endpoint => string.IsNullOrWhiteSpace(TrayApp.Current.Settings.EndpointLocal) ? "http://localhost:11434/v1" : TrayApp.Current.Settings.EndpointLocal.TrimEnd('/');
    public override string Status()
    {
        try
        {
            using var c = new HttpClient { Timeout = TimeSpan.FromSeconds(2) };
            using var resp = c.GetAsync(Endpoint + "/models").Result;
            return resp.IsSuccessStatusCode ? "reachable" : "server answered " + (int)resp.StatusCode;
        }
        catch { return "not reachable"; }
    }
    public override bool IsReady() => true;
}

internal sealed class GeminiApiProvider : IProvider
{
    public string Id => "gemini";
    public string Label => "Gemini API (key)";
    public string Kind => "api";
    public bool SupportsImages => true;
    public string DefaultModel => "gemini-2.5-flash";
    public string[] ModelHints => new[] { "gemini-2.5-flash", "gemini-2.5-pro" };
    public const string KeyUrl = "https://aistudio.google.com/apikey";
    public string Status() => Credentials.Has(Id) ? "key set" : "no key";
    public bool IsReady() => Credentials.Has(Id);

    public string Complete(AiRequest r, int timeoutMs)
    {
        var key = Credentials.Get(Id) ?? throw new InvalidOperationException("No Gemini API key. Providers -> Gemini API -> API key.");
        var parts = new JsonArray();
        if (r.ImagePath != null)
        {
            var (mime, b64) = Providers.ImageData(r.ImagePath);
            parts.Add(new JsonObject { ["inline_data"] = new JsonObject { ["mime_type"] = mime, ["data"] = b64 } });
        }
        parts.Add(new JsonObject { ["text"] = r.Instruction + (r.Text.Length > 0 ? "\n\n" + r.Text : "") });
        var body = new JsonObject { ["contents"] = new JsonArray(new JsonObject { ["role"] = "user", ["parts"] = parts }) };
        if (r.System.Length > 0) body["system_instruction"] = new JsonObject { ["parts"] = new JsonArray(new JsonObject { ["text"] = r.System }) };
        var model = string.IsNullOrWhiteSpace(r.Model) ? DefaultModel : r.Model.Trim();
        var resp = Providers.PostJson($"https://generativelanguage.googleapis.com/v1beta/models/{model}:generateContent", body, h => h.Add("x-goog-api-key", key), timeoutMs);
        var node = JsonNode.Parse(resp);
        var sb = new StringBuilder();
        foreach (var p in node?["candidates"]?[0]?["content"]?["parts"]?.AsArray() ?? new JsonArray())
            sb.Append(p?["text"]?.GetValue<string>());
        return sb.ToString();
    }
}

// ============================================================ the facade the rest of the app calls

internal static class Ai
{
    public const string RawSystem =
        "You are a text-transformation tool. Do any reasoning silently, then output ONLY the finished result wrapped between the markers [[[R]]] and [[[/R]]]. " +
        "Put nothing except the result inside the markers (no preamble, labels, quotes, or code fences) and nothing after [[[/R]]].";

    /// <summary>Role setting is "providerId:model" (model may be empty).</summary>
    public static (IProvider provider, string model) Resolve(AiRole role) => Resolve(role, TrayApp.Current.Settings);

    /// <summary>
    /// Resolve the provider a role is configured to use. An empty Precise or Vision role means no
    /// role-specific override and reuses the configured Quick provider; it never searches for another one.
    /// </summary>
    internal static (IProvider provider, string model) Resolve(AiRole role, Settings s)
    {
        var spec = role switch { AiRole.Precise => s.RolePrecise, AiRole.Vision => s.RoleVision, _ => s.RoleQuick };
        if (string.IsNullOrWhiteSpace(spec)) spec = s.RoleQuick;
        if (string.IsNullOrWhiteSpace(spec)) throw new InvalidOperationException("No AI provider configured yet. Open Providers... from the menu.");
        var idx = spec.IndexOf(':');
        var id = idx < 0 ? spec : spec[..idx];
        var model = idx < 0 ? "" : spec[(idx + 1)..];
        var p = Providers.ById(id.Trim()) ?? throw new InvalidOperationException("Unknown provider '" + id + "'. Open Providers...");
        return (p, model.Trim());
    }

    public static string Describe(AiRole role)
    {
        try { var (p, m) = Resolve(role); return p.Label.Split(' ')[0] + (m.Length > 0 ? " " + m : ""); }
        catch { return "not configured"; }
    }

    /// <summary>
    /// The provider for this role, refusing an image the configured provider cannot read. Never look for
    /// another backend: an image must only reach the provider the user chose for this role.
    /// </summary>
    internal static (IProvider provider, string model) Plan(AiRole role, Settings settings, bool hasImage)
    {
        var (provider, model) = Resolve(role, settings);
        if (hasImage && !provider.SupportsImages)
            throw new InvalidOperationException(provider.Label + " cannot read images. Open Providers..., pick an image-capable provider for Vision, and save.");
        return (provider, model);
    }

    public static string Run(AiRole role, string text, string instruction, bool raw = false, string? styleFile = null, string? imagePath = null, int timeoutMs = 300_000)
    {
        OperationContext.Token.ThrowIfCancellationRequested();
        var (provider, model) = Plan(role, TrayApp.Current.Settings, imagePath != null);
        var sys = raw ? RawSystem : "";
        if (!string.IsNullOrEmpty(styleFile) && File.Exists(styleFile))
            sys = (sys + "\n\nApply this house style:\n" + File.ReadAllText(styleFile, Encoding.UTF8)).Trim();
        var resp = provider.Complete(new AiRequest(sys, instruction, text, imagePath, model), timeoutMs);
        OperationContext.Token.ThrowIfCancellationRequested();
        resp = (resp ?? "").Replace("\r\n", "\n");
        if (raw)
        {
            var m = Regex.Match(resp, @"\[\[\[R\]\]\](.*?)\[\[\[/R\]\]\]", RegexOptions.Singleline);
            resp = m.Success ? m.Groups[1].Value.Trim() : Regex.Replace(resp, @"\[\[\[/?R\]\]\]", "").Trim();
            resp = Regex.Replace(resp, "^```[a-zA-Z0-9]*\n(.*)\n```$", "$1", RegexOptions.Singleline);
        }
        else resp = resp.TrimEnd();
        return resp;
    }

    /// <summary>Tiny round-trip used by the Providers dialog.</summary>
    public static string Test(IProvider p, string model)
    {
        var r = p.Complete(new AiRequest("", "Reply with exactly the two letters OK and nothing else.", "", null, model), 90_000);
        return (r ?? "").Trim();
    }
}
