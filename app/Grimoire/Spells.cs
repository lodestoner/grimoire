using System.Diagnostics;
using System.Text;
using System.Text.Json;

namespace Spellbook;

/// <summary>
/// User-defined spells from spells.ini. Three kinds:
///   type=prompt   an AI instruction (mode replace/show/review, style auto/prose/code/none, role quick/precise)
///   type=command  a shell command; the text arrives on stdin and in %SPELL_TEXT%, {file} expands per selected file
///   type=http     a POST/GET to a URL with {text}, {json}, {file} placeholders in headers/body
/// files=1 marks a spell for the file menu (runs once per selected file).
/// </summary>
internal sealed record Spell(string Name, string Type, string Prompt, string Mode, string Style, string Role,
    string Command, string Url, string Method, string Headers, string Body, bool ShowResponse, bool Files,
    string CredentialReference = "", string CredentialHeader = "Authorization", string CredentialScheme = "Bearer");

internal static class Spells
{
    public static string File_ => Paths.File_("spells.ini");

    public static SpellStore Store => new(File_);
    public static List<Spell> All() => Store.All();
    public static void SavePrompt(string name, string prompt, string mode, string style, string role) =>
        Save(new Spell(name, "prompt", prompt, mode, style, role, "", "", "POST", "", "{text}", false, false));
    // A missing original means Add, never an implicit overwrite.
    public static void Save(Spell s) => Store.Save(s);
    public static bool Exists(string name) => All().Any(s => s.Name.Equals(name, StringComparison.OrdinalIgnoreCase));

    static Spell P(string name, string prompt, string mode = "review", string style = "none", string role = "quick") =>
        new(name, "prompt", prompt, mode, style, role, "", "", "POST", "", "{text}", false, false);

    /// <summary>Ready-made spells offered on first run and from Settings.</summary>
    public static readonly (Spell Spell, string About)[] Starters =
    {
        (P("Reply politely", "Draft a short, polite reply to this message. Keep it under 80 words, friendly and specific. Output only the reply.", "review", "prose"), "Turns a message into a ready-to-send answer."),
        (P("Make it a checklist", "Turn this into a Markdown checklist of concrete steps, one action per line, in the right order. Output only the checklist."), "Steps, tasks, or a plan become tick boxes."),
        (P("Explain like I'm 8", "Explain this for a curious 8-year-old in three short, friendly sentences. Output only the explanation.", "show"), "Plain-language explanation in a popup."),
        (P("TL;DR for chat", "Summarize this in two sentences a busy teammate can read in chat, then one line starting with \"Ask:\" if there is anything to decide or do. Output only that.", "show"), "Two-sentence summary plus the ask."),
        (P("Make it professional", "Rewrite this in a calm, professional tone. Keep the meaning and facts, remove slang and heat, keep it about the same length. Output only the rewritten text.", "review", "prose"), "Softens an angry draft before it goes out."),
        (P("Turn into an email", "Turn these notes into a complete, courteous email with a one-line subject on the first line (prefixed \"Subject:\"), a greeting, short paragraphs, and a sign-off placeholder. Output only the email.", "review", "prose"), "Rough notes become a sendable email."),
        (P("Extract facts", "List every name, date, number, amount, and deadline in this text as bullet points, each with the surrounding context in a few words. Output only the list.", "show"), "Pulls the hard facts out of a wall of text."),
        (P("Explain this regex / command", "Explain what this regular expression, shell command, or code snippet does, piece by piece, then give one example input and its result. Be concise.", "show", "code"), "Decode a cryptic one-liner."),
        (P("Write a commit message", "Write a git commit message for this diff or change description: a 50-character imperative subject line, a blank line, then 1-4 bullet points explaining what and why. Output only the message.", "review", "code"), "From a diff or description to a proper commit."),
        (P("Rewrite as a tweet", "Rewrite this as a single social post under 280 characters, punchy, no hashtags unless they were in the source. Output only the post.", "review", "prose"), "Long thought, short post."),
        (P("Translate to Spanish", "Translate the text to Spanish. Keep names and numbers. Output ONLY the translation.", "replace"), "In-place translation; edit the language to taste."),
        (P("Devil's advocate", "Give the three strongest objections to this argument or plan, each in one or two sentences, then one sentence on which objection matters most.", "show"), "Stress-test an idea before you commit."),
    };

    public static void Delete(string name) => Store.Delete(name);

    static string StyleFileFor(Spell s, string text) => s.Style switch
    {
        "prose" => Paths.StyleProse,
        "code" => Paths.StyleCode,
        "auto" => TextStats.LooksLikeCode(text) ? Paths.StyleCode : Paths.StyleProse,
        _ => "",
    };

    /// <summary>
    /// Build the instruction and the separate text payload for a prompt spell. A provider appends the
    /// payload after the instruction, so a prompt that places the text itself with {text} must not also
    /// hand it over separately; {file} expands the same way for file spells.
    /// </summary>
    internal static (string Instruction, string Text) Compose(string prompt, string text, string? file = null)
    {
        var instruction = file == null ? prompt : prompt.Replace("{file}", file);
        return instruction.Contains("{text}", StringComparison.Ordinal)
            ? (instruction.Replace("{text}", text), "")
            : (instruction, text);
    }

    static string RunPrompt(Spell s, string text, string? file)
    {
        var (instruction, payload) = Compose(s.Prompt, text, file);
        return Ai.Run(s.Role == "precise" ? AiRole.Precise : AiRole.Quick, payload, instruction,
            s.Mode != "show", file == null ? StyleFileFor(s, text) : "");
    }

    /// <summary>Run a text spell; returns the result (the caller decides how to deliver it by spell.Mode).</summary>
    public static string RunText(Spell s, string text) => s.Type switch
    {
        "prompt" => RunPrompt(s, text, null),
        "command" => RunCommand(s.Command, text, null),
        "http" => RunHttp(s, text, null),
        _ => throw new InvalidOperationException("Unknown spell type: " + s.Type),
    };

    public static string RunFile(Spell s, string file) => s.Type switch
    {
        "prompt" => RunPrompt(s, Files.TextOf(file), file),
        "command" => RunCommand(s.Command, "", file),
        "http" => RunHttp(s, Files.TextOf(file), file),
        _ => throw new InvalidOperationException("Unknown spell type: " + s.Type),
    };

    internal static ProcessStartInfo CommandStartInfo(string command, string stdin, string? file)
    {
        var psi = new ProcessStartInfo(Environment.GetEnvironmentVariable("ComSpec") ?? "cmd.exe")
        {
            UseShellExecute = false, CreateNoWindow = true, RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true,
            StandardInputEncoding = new UTF8Encoding(false), StandardOutputEncoding = Encoding.UTF8, StandardErrorEncoding = Encoding.UTF8,
        };
        // This is an explicitly authored shell command; selected text stays on stdin/environment.
        command = PrepareCommand(command);
        if (file?.IndexOfAny(['"', '\r', '\n']) >= 0) throw new InvalidOperationException("Unsupported file path.");
        psi.Arguments = "/d /v:off /s /c \"" + command + "\"";
        psi.Environment["SPELL_TEXT"] = stdin;
        if (file != null) { psi.Environment["SPELL_FILE"] = file; psi.WorkingDirectory = Path.GetDirectoryName(file) ?? ""; }
        return psi;
    }

    static string RunCommand(string command, string stdin, string? file)
    {
        var (o, e, _) = Providers.RunProcess(CommandStartInfo(command, stdin, file), stdin, 120_000);
        return (o + (string.IsNullOrWhiteSpace(e) ? "" : "\n" + e)).TrimEnd();
    }

    // Never interpolate a selected path into shell source. Support the existing standalone
    // {file} convention (including its quoted spelling) through a quoted environment read.
    internal static string PrepareCommand(string command)
    {
        var pattern = @"(?<![^\s])(?:""\{file\}""|\{file\})(?![^\s])";
        var result = System.Text.RegularExpressions.Regex.Replace(command, pattern, "\"%SPELL_FILE%\"");
        if (result.Contains("{file}", StringComparison.Ordinal))
            throw new InvalidOperationException("Use {file} as a separate command argument, or read the SPELL_FILE environment variable in your script.");
        return result;
    }

    static readonly HttpClient Http = new(new HttpClientHandler { AllowAutoRedirect = false }) { Timeout = TimeSpan.FromSeconds(60) };

    static string RunHttp(Spell s, string text, string? file)
    {
        string Fill(string t) => t.Replace("{text}", text).Replace("{json}", JsonSerializer.Serialize(text)).Replace("{file}", file ?? "").Replace("{filename}", file == null ? "" : Path.GetFileName(file));
        var url = Fill(s.Url);
        if (string.IsNullOrWhiteSpace(url)) throw new InvalidOperationException("Spell has no url=");
        using var req = new HttpRequestMessage(new HttpMethod(s.Method), url);
        string? contentType = null;
        foreach (var line in s.Headers.Split('\n', '|'))
        {
            var idx = line.IndexOf(':');
            if (idx <= 0) continue;
            var k = line[..idx].Trim(); var v = Fill(line[(idx + 1)..].Trim());
            if (k.Equals("Content-Type", StringComparison.OrdinalIgnoreCase)) contentType = v;
            else req.Headers.TryAddWithoutValidation(k, v);
        }
        if (s.CredentialReference.Length > 0)
        {
            if (req.RequestUri?.Scheme != "https") throw new InvalidOperationException("Credential-backed spells require HTTPS.");
            var secret = new SpellCredentials().Get(s.CredentialReference)
                ?? throw new InvalidOperationException("The named spell credential is missing. Edit this spell to choose a credential.");
            req.Headers.Remove(s.CredentialHeader);
            req.Headers.Add(s.CredentialHeader, s.CredentialScheme.Length == 0 ? secret : s.CredentialScheme + " " + secret);
        }
        if (s.Method != "GET")
        {
            if (file != null && s.Body.Trim() == "{filedata}")
                req.Content = new ByteArrayContent(File.ReadAllBytes(file));
            else
                req.Content = new StringContent(Fill(s.Body), new UTF8Encoding(false), contentType ?? (s.Body.TrimStart().StartsWith('{') ? "application/json" : "text/plain"));
            if (contentType != null && req.Content.Headers.ContentType != null) req.Content.Headers.ContentType = System.Net.Http.Headers.MediaTypeHeaderValue.Parse(contentType);
        }
        using var deadline = OperationContext.Deadline(60_000);
        using var resp = Http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, deadline.Token).ConfigureAwait(false).GetAwaiter().GetResult();
        var body = resp.Content.ReadAsStringAsync(deadline.Token).ConfigureAwait(false).GetAwaiter().GetResult();
        if (!resp.IsSuccessStatusCode) throw new InvalidOperationException($"HTTP {(int)resp.StatusCode}: {Util.Trunc(body, 200)}");
        return body;
    }
}

/// <summary>Edits one raw INI section, preserving every other section and its order.</summary>
internal sealed class SpellStore(string path, Action<string, string>? commit = null)
{
    static readonly object Gate = new();
    static readonly System.Text.RegularExpressions.Regex Sections = new(@"(?m)^[ \t]*\[([^\]\r\n]+)\][ \t]*\r?$", System.Text.RegularExpressions.RegexOptions.Compiled);
    internal sealed record UndoState(string Before, string After);
    sealed record Section(string Name, int Start, int Length, Dictionary<string, string> Keys);
    string Read() => File.Exists(path) ? File.ReadAllText(path, Encoding.UTF8) : "";
    static List<Section> Parse(string text)
    {
        var matches = Sections.Matches(text);
        var result = new List<Section>();
        for (int i = 0; i < matches.Count; i++)
        {
            var match = matches[i];
            int end = i + 1 == matches.Count ? text.Length : matches[i + 1].Index;
            var keys = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var raw in text[(match.Index + match.Length)..end].Split('\n'))
            {
                var line = raw.Trim(); int eq = line.IndexOf('=');
                if (eq > 0 && !line.StartsWith(';') && !line.StartsWith('#')) keys[line[..eq].Trim()] = line[(eq + 1)..].Trim();
            }
            result.Add(new(match.Groups[1].Value.Trim(), match.Index, end - match.Index, keys));
        }
        return result;
    }
    static Spell Decode(Section section)
    {
        string G(string k, string d = "") => section.Keys.GetValueOrDefault(k, d);
        string V(string k, string d = "")
        {
            var value = G(k, d);
            if (G("exact_format") == "json-v1" && section.Keys.TryGetValue(k + "_json", out var json))
            {
                var exact = JsonSerializer.Deserialize<string>(json) ?? "";
                // An older app or manual edit may update the legacy mirror and leave this
                // supplemental key behind. Such an edit takes precedence over stale exact text.
                if (value == LegacyValue(k, exact).Trim()) return exact;
            }
            return LegacyRead(k, value);
        }
        return new(section.Name, G("type", "prompt").ToLowerInvariant(), V("prompt"), G("mode", "review").ToLowerInvariant(),
            G("style", "none").ToLowerInvariant(), G("role", "quick").ToLowerInvariant(), V("command"),
            V("url"), G("method", "POST").ToUpperInvariant(), V("headers"), V("body", "{text}"),
            G("show", "0") == "1", G("files", "0") == "1", G("credential"), G("credential_header", "Authorization"), G("credential_scheme", "Bearer"));
    }
    public List<Spell> All() { lock (Gate) return Parse(Read()).Select(Decode).ToList(); }
    public static string? Validate(Spell s)
    {
        if (string.IsNullOrWhiteSpace(s.Name) || s.Name != s.Name.Trim() || s.Name.Length > 120 || s.Name.Any(c => char.IsControl(c) || "[]=;#".Contains(c)))
            return "Use a name of 1–120 characters without leading/trailing spaces, line breaks, or [ ] = ; #.";
        if (s.Type is not ("prompt" or "command" or "http")) return "Choose a supported spell type.";
        if (s.Mode is not ("review" or "replace" or "show")) return "Choose how to show the result.";
        if (s.Type == "prompt" && string.IsNullOrWhiteSpace(s.Prompt)) return "Write an AI instruction.";
        if (s.Type == "command")
        {
            if (string.IsNullOrWhiteSpace(s.Command)) return "Enter a shell command.";
            try { Spells.PrepareCommand(s.Command); } catch (InvalidOperationException e) { return e.Message; }
        }
        if (s.Type == "http")
        {
            if (!Uri.TryCreate(s.Url, UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https") || uri.UserInfo.Length > 0)
                return "Enter an HTTP or HTTPS URL without a username or password.";
            if (s.Method is not ("GET" or "POST" or "PUT" or "PATCH")) return "Choose an HTTP method.";
        }
        if (s.Url.Any(char.IsControl)) return "The URL cannot contain line breaks or control characters.";
        if (new[] { s.Mode, s.Style, s.Role, s.Method }.Any(v => v.Any(char.IsControl))) return "Spell option values cannot contain line breaks or control characters.";
        if (s.Type == "http" && s.Headers.Split('\n', '|').Any(line =>
        {
            int colon = line.IndexOf(':');
            return colon > 0 && new[] { "authorization", "proxy-authorization", "cookie", "x-api-key", "api-key", "x-auth-token" }.Contains(line[..colon].Trim().ToLowerInvariant());
        })) return "Move the secret header into Advanced options using a named credential; headers in spells.ini are plain text.";
        if (s.CredentialReference.Length > 0)
        {
            if (!System.Text.RegularExpressions.Regex.IsMatch(s.CredentialReference, @"\A[a-zA-Z0-9_.-]{1,80}\z")) return "Credential names use 1–80 letters, numbers, dots, dashes or underscores.";
            if (!System.Text.RegularExpressions.Regex.IsMatch(s.CredentialHeader, @"\A[A-Za-z0-9-]+\z") || s.CredentialHeader.Equals("Content-Type", StringComparison.OrdinalIgnoreCase)) return "Enter a request header name for the credential, such as Authorization or X-Api-Key.";
            if (!System.Text.RegularExpressions.Regex.IsMatch(s.CredentialScheme, @"\A[A-Za-z0-9-]*\z")) return "The credential prefix must be a single word (for example Bearer), or blank.";
            if (s.Type == "http" && !s.Url.StartsWith("https://", StringComparison.OrdinalIgnoreCase)) return "Credential-backed HTTP spells require HTTPS.";
        }
        return null;
    }
    static string LegacyValue(string key, string value) => key == "url" ? value : value.Replace("\\", "\\\\").Replace("\r\n", "\n").Replace("\r", "\n").Replace("\n", "\\n");
    static string LegacyRead(string key, string value) => key == "url" ? value :
        System.Text.RegularExpressions.Regex.Replace(value, @"\\(n|\\)", m => m.Groups[1].Value == "n" ? "\n" : "\\");
    static string Encode(Spell s)
    {
        var b = new StringBuilder().Append('[').Append(s.Name).AppendLine("]");
        bool supplemental = false;
        void K(string k, string v) => b.Append(k).Append('=').AppendLine(v);
        void V(string k, string v)
        {
            string mirror = LegacyValue(k, v); K(k, mirror);
            if (LegacyRead(k, mirror.Trim()) != v) { K(k + "_json", JsonSerializer.Serialize(v)); supplemental = true; }
        }
        K("type", s.Type); K("mode", s.Mode); K("style", s.Style); K("role", s.Role);
        V("prompt", s.Prompt); V("command", s.Command); V("url", s.Url); K("method", s.Method);
        V("headers", s.Headers); V("body", s.Body); K("show", s.ShowResponse ? "1" : "0"); K("files", s.Files ? "1" : "0");
        if (supplemental) K("exact_format", "json-v1");
        if (s.CredentialReference.Length > 0) { K("credential", s.CredentialReference); K("credential_header", s.CredentialHeader); K("credential_scheme", s.CredentialScheme); }
        return b.AppendLine().ToString();
    }
    public void Save(Spell spell, Spell? original = null)
    {
        var error = Validate(spell); if (error != null) throw new InvalidOperationException(error);
        lock (Gate)
        {
            var before = Read(); var sections = Parse(before);
            var matches = original == null ? [] : sections.Where(s => s.Name.Equals(original.Name, StringComparison.OrdinalIgnoreCase)).ToList();
            if (original != null && (matches.Count != 1 || Decode(matches[0]) != original))
                throw new InvalidOperationException("This spell changed or was removed. Close the editor and reopen the latest version.");
            var target = matches.SingleOrDefault();
            if (sections.Any(s => s != target && s.Name.Equals(spell.Name, StringComparison.OrdinalIgnoreCase)))
                throw new InvalidOperationException("A spell with this name already exists. Choose a different name.");
            if (spell == original) return;
            string after = target == null ? before + (before.Length > 0 && !before.EndsWith('\n') ? Environment.NewLine : "") + Encode(spell)
                : before[..target.Start] + Encode(spell) + before[(target.Start + target.Length)..];
            Write(before, after);
        }
    }
    public UndoState Delete(string name, Spell? expected = null)
    {
        lock (Gate)
        {
            var before = Read(); var matches = Parse(before).Where(s => s.Name.Equals(name, StringComparison.OrdinalIgnoreCase)).ToList();
            if (matches.Count != 1) throw new InvalidOperationException("The spell list changed or has duplicate names. Refresh it before removing a spell.");
            var s = matches[0];
            if (expected != null && Decode(s) != expected) throw new InvalidOperationException("This spell changed. Refresh before removing it.");
            var after = before.Remove(s.Start, s.Length);
            Write(before, after); return new(before, after);
        }
    }
    public void Undo(UndoState state) { lock (Gate) Write(state.After, state.Before); }
    void Write(string before, string after)
    {
        if (Read() != before) throw new InvalidOperationException("The spells file changed. Refresh before trying again.");
        AtomicFile.Write(path, after, commit, () => Read() == before);
    }
}

internal interface ISpellCredentials
{
    string? Get(string name);
    void Set(string name, string value);
    void Delete(string name);
}
internal sealed class SpellCredentials : ISpellCredentials
{
    public string? Get(string name) => Credentials.Get("spells/" + name);
    public void Set(string name, string value) => Credentials.Set("spells/" + name, value);
    public void Delete(string name) => Credentials.Delete("spells/" + name);
}

/// <summary>Reusable text blocks from snippets.ini, pasted on demand.</summary>
internal static class Snippets
{
    public static string File_ => Paths.File_("snippets.ini");

    public static List<(string Name, string Text)> All() =>
        Ini.Load(File_).Sections.Select(s => (s.Name, s.Keys.TryGetValue("text", out var t) ? t.Replace("\\n", "\n") : "")).ToList();

    public static void Save(string name, string text)
    {
        var ini = Ini.Load(File_);
        ini.Set(name, "text", text.Replace("\r\n", "\n").Replace("\n", "\\n"));
        ini.Save(File_);
    }

    public static void Delete(string name) { var ini = Ini.Load(File_); ini.RemoveSection(name); ini.Save(File_); }
}
