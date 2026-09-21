using System.Globalization;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Spellbook;

/// <summary>Deterministic local text transforms, keyed by the same names the recipes use.</summary>
internal static class Transforms
{
    public static readonly string[] Keys =
    {
        "upper","lower","title","sentence","apheadline","camel","snake","kebab","constant","slug",
        "join","clean","splitsentences","collapseblanks","sortaz","sortza","unique","reverse","trimlines","numberlines",
        "curly","straight","emdash","ellipsis","deaccent","dblspace","codeblock",
        "b64enc","b64dec","urlenc","urldec","htmlenc","htmldec","md5","sha256","jsonpretty","jsonmin","jsonescape",
    };

    static readonly JsonSerializerOptions Pretty = new() { WriteIndented = true, Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };
    static readonly JsonSerializerOptions Compact = new() { WriteIndented = false, Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    public static string Run(string s, string key) => key switch
    {
        "upper" => s.ToUpperInvariant(),
        "lower" => s.ToLowerInvariant(),
        "title" => TitleCase(s),
        "sentence" => SentenceCase(s),
        "apheadline" => APHeadline(s),
        "camel" => Camel(s),
        "snake" => JoinWords(s, "_", false),
        "kebab" => JoinWords(s, "-", false),
        "constant" => JoinWords(s, "_", true),
        "slug" => JoinWords(Deaccent(s), "-", false),
        "join" => JoinLines(s),
        "clean" => CleanWS(s),
        "splitsentences" => SplitSentences(s),
        "collapseblanks" => CollapseBlanks(s),
        "sortaz" => string.Join("\n", Lines(s).OrderBy(x => x, StringComparer.OrdinalIgnoreCase)),
        "sortza" => string.Join("\n", Lines(s).OrderByDescending(x => x, StringComparer.OrdinalIgnoreCase)),
        "unique" => string.Join("\n", Lines(s).Distinct()),
        "reverse" => string.Join("\n", Lines(s).Reverse()),
        "trimlines" => string.Join("\n", Lines(s).Select(l => l.Trim())),
        "numberlines" => string.Join("\n", Lines(s).Select((l, i) => $"{i + 1}. {l}")),
        "curly" => Curly(s),
        "straight" => Straight(s),
        "emdash" => s.Replace("--", "—"),
        "ellipsis" => s.Replace("...", "…"),
        "deaccent" => Deaccent(s),
        "dblspace" => Regex.Replace(s, "[ \t]{2,}", " "),
        "codeblock" => "```\n" + s + "\n```",
        "b64enc" => Convert.ToBase64String(Encoding.UTF8.GetBytes(s)),
        "b64dec" => Encoding.UTF8.GetString(Convert.FromBase64String(s.Trim())),
        "urlenc" => Uri.EscapeDataString(s),
        "urldec" => Uri.UnescapeDataString(s),
        "htmlenc" => WebUtility.HtmlEncode(s),
        "htmldec" => WebUtility.HtmlDecode(s),
        "md5" => Hex(MD5.HashData(Encoding.UTF8.GetBytes(s))),
        "sha256" => Hex(SHA256.HashData(Encoding.UTF8.GetBytes(s))),
        "jsonpretty" => JsonSerializer.Serialize(JsonDocument.Parse(s, new JsonDocumentOptions { AllowTrailingCommas = true, CommentHandling = JsonCommentHandling.Skip }).RootElement, Pretty),
        "jsonmin" => JsonSerializer.Serialize(JsonDocument.Parse(s, new JsonDocumentOptions { AllowTrailingCommas = true, CommentHandling = JsonCommentHandling.Skip }).RootElement, Compact),
        "jsonescape" => JsonSerializer.Serialize(s, Compact),
        _ => throw new ArgumentException("Unknown transform: " + key),
    };

    public static string[] Lines(string s) => Regex.Replace(s, "\r\n?|\n", "\n").Split('\n');

    static string Hex(byte[] b) => Convert.ToHexString(b).ToLowerInvariant();

    static string TitleCase(string s)
    {
        return Regex.Replace(s.ToLowerInvariant(), @"\p{L}[\p{L}\p{N}']*",
            m => char.ToUpperInvariant(m.Value[0]) + m.Value[1..]);
    }

    static string SentenceCase(string s)
    {
        var sb = new StringBuilder(s.Length);
        bool capNext = true;
        foreach (var ch in s.ToLowerInvariant())
        {
            if (capNext && ch >= 'a' && ch <= 'z') { sb.Append(char.ToUpperInvariant(ch)); capNext = false; }
            else { sb.Append(ch); if (ch is '.' or '!' or '?') capNext = true; }
        }
        return sb.ToString();
    }

    static readonly HashSet<string> Small = new("a,an,and,as,at,but,by,for,if,in,nor,of,on,or,per,so,the,to,up,via,vs,with,yet,from".Split(','));

    static string APHeadline(string s)
    {
        var words = Regex.Replace(s, @"\s+", " ").Trim().Split(' ');
        var sb = new StringBuilder();
        for (int i = 0; i < words.Length; i++)
        {
            var lw = words[i].ToLowerInvariant();
            bool keep = i == 0 || i == words.Length - 1 || !Small.Contains(lw);
            if (i > 0) sb.Append(' ');
            sb.Append(keep ? TitleCase(lw) : lw);
        }
        return sb.ToString();
    }

    static string[] SplitWords(string s)
    {
        s = Regex.Replace(s, "([a-z0-9])([A-Z])", "$1 $2");
        s = Regex.Replace(s, "[^A-Za-z0-9]+", " ");
        return s.Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries);
    }

    static string JoinWords(string s, string sep, bool upper) =>
        string.Join(sep, SplitWords(s).Select(w => upper ? w.ToUpperInvariant() : w.ToLowerInvariant()));

    static string Camel(string s)
    {
        var w = SplitWords(s);
        if (w.Length == 0) return s;
        var sb = new StringBuilder(w[0].ToLowerInvariant());
        for (int i = 1; i < w.Length; i++)
        {
            var lw = w[i].ToLowerInvariant();
            sb.Append(char.ToUpperInvariant(lw[0])).Append(lw[1..]);
        }
        return sb.ToString();
    }

    static string JoinLines(string s)
    {
        s = Regex.Replace(s, @"(\r\n?|\n)+", " ");
        s = Regex.Replace(s, "[ \t]{2,}", " ");
        return s.Trim();
    }

    static string CleanWS(string s)
    {
        s = Regex.Replace(s, "\r\n?|\n", "\n");
        s = Regex.Replace(s, "[ \t]+", " ");
        s = Regex.Replace(s, " *\n *", "\n");
        s = Regex.Replace(s, "\n{3,}", "\n\n");
        return s.Trim();
    }

    static string SplitSentences(string s)
    {
        s = JoinLines(s);
        s = Regex.Replace(s, @"([.!?])\s+", "$1\n");
        return s.Trim();
    }

    static string CollapseBlanks(string s)
    {
        s = Regex.Replace(s, "\r\n?|\n", "\n");
        return Regex.Replace(s, "\n{3,}", "\n\n");
    }

    static string Curly(string s)
    {
        const string rsq = "’", lsq = "‘", ldq = "“", rdq = "”";
        s = Regex.Replace(s, @"(\w)'(\w)", "$1" + rsq + "$2");
        s = Regex.Replace(s, @"(\w)'", "$1" + rsq);
        s = Regex.Replace(s, @"'(\w)", lsq + "$1");
        s = s.Replace("'", rsq);
        s = Regex.Replace(s, "(^|[\\s\\(\\[\\{])\"", "$1" + ldq);
        s = s.Replace("\"", rdq);
        return s;
    }

    static string Straight(string s) => s
        .Replace("“", "\"").Replace("”", "\"")
        .Replace("‘", "'").Replace("’", "'")
        .Replace("—", "--").Replace("–", "-")
        .Replace("…", "...");

    public static string Deaccent(string s)
    {
        var sb = new StringBuilder(s.Length);
        foreach (var ch in s.Normalize(NormalizationForm.FormD))
        {
            var cat = CharUnicodeInfo.GetUnicodeCategory(ch);
            if (cat == UnicodeCategory.NonSpacingMark) continue;
            sb.Append(ch switch
            {
                'æ' => "ae", 'Æ' => "AE", 'ß' => "ss", 'ø' => "o", 'Ø' => "O", 'œ' => "oe", 'Œ' => "OE",
                'đ' => "d", 'Đ' => "D", 'ł' => "l", 'Ł' => "L", 'þ' => "th", 'Þ' => "Th",
                _ => ch.ToString(),
            });
        }
        return sb.ToString().Normalize(NormalizationForm.FormC);
    }
}

/// <summary>Word counts, readability, and code-vs-prose detection.</summary>
internal static class TextStats
{
    public static int Words(string s)
    {
        var clean = Regex.Replace(s, @"\s+", " ").Trim();
        return clean.Length == 0 ? 0 : clean.Split(' ').Length;
    }

    public static int Sentences(string s) => Math.Max(Regex.Matches(s, "[.!?]").Count, 1);

    public static int Syllables(string word)
    {
        var w = Regex.Replace(word.ToLowerInvariant(), "[^a-z]", "");
        if (w.Length == 0) return 0;
        int n = 0; bool prevV = false;
        foreach (var ch in w)
        {
            bool isV = "aeiouy".Contains(ch);
            if (isV && !prevV) n++;
            prevV = isV;
        }
        if (w.EndsWith('e') && n > 1) n--;
        return Math.Max(n, 1);
    }

    public static (double ease, double grade, int words, int sentences, int syllables) Flesch(string s)
    {
        var clean = Regex.Replace(s, @"\s+", " ").Trim();
        if (clean.Length == 0) return (0, 0, 0, 0, 0);
        var words = clean.Split(' ');
        int wc = words.Length, sc = Sentences(s);
        int syl = words.Sum(Syllables);
        double asl = (double)wc / sc, asw = (double)syl / wc;
        return (206.835 - 1.015 * asl - 84.6 * asw, 0.39 * asl + 11.8 * asw - 15.59, wc, sc, syl);
    }

    public static double Grade(string s) => Flesch(s).grade;

    public static string EaseLabel(double fre) => fre >= 80 ? "very easy" : fre >= 60 ? "plain English" : fre >= 50 ? "fairly hard" : fre >= 30 ? "difficult" : "very difficult";

    public static bool LooksLikeCode(string s)
    {
        int score = 0;
        if (Regex.IsMatch(s, "[;{}]")) score++;
        if (Regex.IsMatch(s, @"\b(function|def|class|return|var|let|const|import|public|private|void|elseif|foreach)\b")) score++;
        if (Regex.IsMatch(s, @"=>|::|->|\$\w+|#include|</?\w+>|\bself\b")) score++;
        if (Regex.IsMatch(s, @"^\s{2,}\S", RegexOptions.Multiline)) score++;
        if (Regex.IsMatch(s, @"[\w\)]\.\w+\(")) score++;
        return score >= 2;
    }

    public static string BalanceCheck(string s)
    {
        var sb = new StringBuilder();
        foreach (var (o, c) in new[] { ('(', ')'), ('[', ']'), ('{', '}') })
        {
            int no = s.Count(ch => ch == o), nc = s.Count(ch => ch == c);
            sb.Append($"{o}{c} {no}/{nc} {(no == nc ? "ok" : "MISMATCH")}    ");
        }
        int dq = s.Count(ch => ch == '"'), sq = s.Count(ch => ch == '\''), bt = s.Count(ch => ch == '`');
        sb.Append($"\ndouble-quote {dq}{(dq % 2 == 1 ? " ODD" : " even")}    single-quote {sq}{(sq % 2 == 1 ? " ODD" : " even")}    backtick {bt}{(bt % 2 == 1 ? " ODD" : " even")}");
        return sb.ToString();
    }
}

internal static class Util
{
    public static string Trunc(string s, int n) => s.Length <= n ? s : s[..n] + "...";

    /// <summary>Match the line-ending convention of the original selection so pastes look native.</summary>
    public static string MatchNewlines(string output, string original)
    {
        if (!original.Contains("\r\n")) return output;
        return Regex.Replace(output, "\r\n?|\n", "\r\n");
    }

    public static void OpenInEditor(string path)
    {
        try
        {
            if (!File.Exists(path)) File.WriteAllText(path, "");
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("notepad.exe", $"\"{path}\"") { UseShellExecute = true });
        }
        catch (Exception e) { Toast.Show("[x] " + e.Message, 4000); }
    }

    public static void OpenUrl(string url)
    {
        try { System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(url) { UseShellExecute = true }); }
        catch (Exception e) { Toast.Show("[x] " + e.Message, 4000); }
    }
}
