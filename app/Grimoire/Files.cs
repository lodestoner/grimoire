using System.Diagnostics;
using System.IO.Compression;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Markdig;
using DocumentFormat.OpenXml;
using DocumentFormat.OpenXml.Packaging;
using W = DocumentFormat.OpenXml.Wordprocessing;

namespace Spellbook;

/// <summary>What is selected in the foreground Explorer window (or desktop), via the shell automation objects.</summary>
internal static class ExplorerSelection
{
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern int GetClassName(IntPtr hWnd, StringBuilder buf, int max);

    public static string ClassOf(IntPtr hwnd) { var sb = new StringBuilder(64); GetClassName(hwnd, sb, 64); return sb.ToString(); }

    public static bool IsExplorer(IntPtr hwnd) => ClassOf(hwnd) is "CabinetWClass" or "ExploreWClass" or "Progman" or "WorkerW";

    public static List<string> Get(IntPtr foreground)
    {
        var result = new List<string>();
        try
        {
            var t = Type.GetTypeFromProgID("Shell.Application");
            if (t == null) return result;
            dynamic shell = Activator.CreateInstance(t)!;
            dynamic windows = shell.Windows();
            int count = windows.Count;
            bool desktop = ClassOf(foreground) is "Progman" or "WorkerW";
            for (int i = 0; i < count; i++)
            {
                dynamic? w = null;
                try { w = windows.Item(i); } catch { }
                if (w == null) continue;
                try
                {
                    long hwnd = (long)w.HWND;
                    string name = ((string)(w.Name ?? "")).ToLowerInvariant();
                    bool match = hwnd == (long)foreground || (desktop && (name.Contains("desktop") || name.Contains("explorer")) && hwnd != (long)foreground && Path.GetFileName(((string)w.FullName ?? "")).Equals("explorer.exe", StringComparison.OrdinalIgnoreCase) && false);
                    if (!match) continue;
                    dynamic items = w.Document.SelectedItems();
                    int n = items.Count;
                    for (int j = 0; j < n; j++) { string p = items.Item(j).Path; if (!string.IsNullOrEmpty(p) && (File.Exists(p) || Directory.Exists(p))) result.Add(p); }
                    break;
                }
                catch { }
            }
            if (desktop && result.Count == 0)
            {
                // the desktop is a ShellFolderView reachable through the "desktop" shell window
                try
                {
                    dynamic dv = shell.Windows().FindWindowSW(Type.Missing, Type.Missing, 8 /*SWC_DESKTOP*/, out int _, 1 /*SWFO_NEEDDISPATCH*/);
                    dynamic items = dv.Document.SelectedItems();
                    int n = items.Count;
                    for (int j = 0; j < n; j++) { string p = items.Item(j).Path; if (!string.IsNullOrEmpty(p) && (File.Exists(p) || Directory.Exists(p))) result.Add(p); }
                }
                catch { }
            }
        }
        catch (Exception e) { Log.Error("ExplorerSelection", e); }
        return result;
    }

    /// <summary>Install a "Send to" shortcut so any Explorer selection can be handed to the app.</summary>
    public static void InstallSendTo()
    {
        var dir = Environment.GetFolderPath(Environment.SpecialFolder.SendTo);
        var lnk = Path.Combine(dir, Brand.Name + ".lnk");
        var t = Type.GetTypeFromProgID("WScript.Shell") ?? throw new InvalidOperationException("WScript.Shell unavailable");
        dynamic ws = Activator.CreateInstance(t)!;
        dynamic sc = ws.CreateShortcut(lnk);
        sc.TargetPath = Paths.ExePath;
        sc.Arguments = "--files";
        sc.WorkingDirectory = Path.GetDirectoryName(Paths.ExePath);
        sc.IconLocation = Paths.ExePath + ",0";
        sc.Description = "Open the " + Brand.Name + " file menu";
        sc.Save();
    }

    public static bool SendToInstalled() => File.Exists(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.SendTo), Brand.Name + ".lnk"));
}

/// <summary>File conversions. Every method takes a source path and returns the output path.</summary>
internal static class Files
{
    public static readonly HashSet<string> ImageExt = new(StringComparer.OrdinalIgnoreCase) { ".jpg", ".jpeg", ".png", ".bmp", ".gif", ".tif", ".tiff", ".heic", ".heif", ".webp", ".avif", ".jxr", ".ico", ".dng", ".cr2", ".nef", ".arw" };
    public static readonly HashSet<string> VideoExt = new(StringComparer.OrdinalIgnoreCase) { ".mp4", ".mov", ".mkv", ".avi", ".webm", ".m4v", ".wmv", ".ts", ".flv" };
    public static readonly HashSet<string> AudioExt = new(StringComparer.OrdinalIgnoreCase) { ".mp3", ".wav", ".m4a", ".aac", ".flac", ".ogg", ".opus", ".wma" };
    public static readonly HashSet<string> TextExt = new(StringComparer.OrdinalIgnoreCase) { ".txt", ".md", ".markdown", ".csv", ".json", ".yaml", ".yml", ".xml", ".html", ".htm", ".log", ".ini", ".cfg", ".toml", ".ps1", ".py", ".js", ".ts", ".cs", ".sh", ".bat", ".cmd", ".sql" };

    public static string Kind(string path)
    {
        if (Directory.Exists(path)) return "folder";
        var e = Path.GetExtension(path);
        if (ImageExt.Contains(e)) return "image";
        if (VideoExt.Contains(e)) return "video";
        if (AudioExt.Contains(e)) return "audio";
        if (e.Equals(".docx", StringComparison.OrdinalIgnoreCase)) return "docx";
        if (e.Equals(".pdf", StringComparison.OrdinalIgnoreCase)) return "pdf";
        if (e.Equals(".md", StringComparison.OrdinalIgnoreCase) || e.Equals(".markdown", StringComparison.OrdinalIgnoreCase)) return "markdown";
        if (e.Equals(".csv", StringComparison.OrdinalIgnoreCase)) return "csv";
        if (e.Equals(".json", StringComparison.OrdinalIgnoreCase)) return "json";
        if (e.Equals(".yaml", StringComparison.OrdinalIgnoreCase) || e.Equals(".yml", StringComparison.OrdinalIgnoreCase)) return "yaml";
        if (TextExt.Contains(e)) return "text";
        return "other";
    }

    /// <summary>Next free path beside the source with a new extension and optional suffix.</summary>
    public static string OutPath(string src, string newExt, string suffix = "")
    {
        var dir = Path.GetDirectoryName(src) ?? "";
        var name = Path.GetFileNameWithoutExtension(src) + suffix;
        var p = Path.Combine(dir, name + newExt);
        int i = 1;
        while (File.Exists(p) || p.Equals(src, StringComparison.OrdinalIgnoreCase)) p = Path.Combine(dir, $"{name} ({i++}){newExt}");
        return p;
    }

    // ------------------------------------------------------------ images (Windows Imaging Component via WPF)
    static T OnSta<T>(Func<T> f)
    {
        if (Thread.CurrentThread.GetApartmentState() == ApartmentState.STA) return f();
        T result = default!; Exception? err = null;
        var t = new Thread(() => { try { result = f(); } catch (Exception e) { err = e; } });
        t.SetApartmentState(ApartmentState.STA); t.Start(); t.Join();
        if (err != null) throw err;
        return result;
    }

    static System.Windows.Media.Imaging.BitmapSource Load(string path)
    {
        using var fs = File.OpenRead(path);
        var dec = System.Windows.Media.Imaging.BitmapDecoder.Create(fs, System.Windows.Media.Imaging.BitmapCreateOptions.IgnoreColorProfile, System.Windows.Media.Imaging.BitmapCacheOption.OnLoad);
        System.Windows.Media.Imaging.BitmapSource frame = dec.Frames[0];
        // honour EXIF orientation (phone photos)
        try
        {
            if (frame.Metadata is System.Windows.Media.Imaging.BitmapMetadata md && md.ContainsQuery("System.Photo.Orientation"))
            {
                var o = Convert.ToInt32(md.GetQuery("System.Photo.Orientation"));
                double angle = o switch { 3 => 180, 6 => 90, 8 => 270, _ => 0 };
                if (angle != 0)
                {
                    var tb = new System.Windows.Media.Imaging.TransformedBitmap(frame, new System.Windows.Media.RotateTransform(angle));
                    tb.Freeze(); frame = tb;
                }
            }
        }
        catch { }
        return frame;
    }

    static System.Windows.Media.Imaging.BitmapSource Fit(System.Windows.Media.Imaging.BitmapSource src, int maxDim)
    {
        if (maxDim <= 0 || (src.PixelWidth <= maxDim && src.PixelHeight <= maxDim)) return src;
        double s = (double)maxDim / Math.Max(src.PixelWidth, src.PixelHeight);
        var tb = new System.Windows.Media.Imaging.TransformedBitmap(src, new System.Windows.Media.ScaleTransform(s, s));
        tb.Freeze();
        return tb;
    }

    static byte[] Encode(System.Windows.Media.Imaging.BitmapSource src, string ext, int quality)
    {
        System.Windows.Media.Imaging.BitmapEncoder enc = ext switch
        {
            ".jpg" or ".jpeg" => new System.Windows.Media.Imaging.JpegBitmapEncoder { QualityLevel = Math.Clamp(quality, 1, 100) },
            ".png" => new System.Windows.Media.Imaging.PngBitmapEncoder(),
            ".bmp" => new System.Windows.Media.Imaging.BmpBitmapEncoder(),
            ".gif" => new System.Windows.Media.Imaging.GifBitmapEncoder(),
            ".tif" or ".tiff" => new System.Windows.Media.Imaging.TiffBitmapEncoder(),
            _ => throw new InvalidOperationException("Cannot encode " + ext),
        };
        if (ext is ".jpg" or ".jpeg" or ".bmp")
        {
            // flatten transparency onto white
            var conv = new System.Windows.Media.Imaging.FormatConvertedBitmap(src, System.Windows.Media.PixelFormats.Bgr24, null, 0);
            conv.Freeze(); src = conv;
        }
        enc.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(src));
        using var ms = new MemoryStream();
        enc.Save(ms);
        return ms.ToArray();
    }

    public static string ConvertImage(string src, string ext, int quality = 90, int maxDim = 0) => OnSta(() =>
    {
        var img = Fit(Load(src), maxDim);
        var bytes = Encode(img, ext, quality);
        var outp = OutPath(src, ext, maxDim > 0 ? $"-{maxDim}" : "");
        File.WriteAllBytes(outp, bytes);
        return outp;
    });

    /// <summary>Re-encode as JPEG, lowering quality (then size) until under the target.</summary>
    public static string CompressImage(string src, int targetKb) => OnSta(() =>
    {
        var img = Load(src);
        long target = targetKb * 1024L;
        byte[] best = Encode(img, ".jpg", 92);
        foreach (var q in new[] { 85, 75, 65, 55, 45, 35 })
        {
            if (best.Length <= target) break;
            best = Encode(img, ".jpg", q);
        }
        int dim = Math.Max(img.PixelWidth, img.PixelHeight);
        while (best.Length > target && dim > 400)
        {
            dim = (int)(dim * 0.8);
            best = Encode(Fit(img, dim), ".jpg", 70);
        }
        var outp = OutPath(src, ".jpg", "-small");
        File.WriteAllBytes(outp, best);
        return outp;
    });

    /// <summary>Any decodable image (HEIC included) as a temporary PNG for the vision providers.</summary>
    public static string ToTempPng(string src) => OnSta(() =>
    {
        var dir = Path.Combine(Path.GetTempPath(), Brand.Name); Directory.CreateDirectory(dir);
        var png = Path.Combine(dir, "img_" + Guid.NewGuid().ToString("N") + ".png");
        File.WriteAllBytes(png, Encode(Fit(Load(src), 2000), ".png", 100));
        return png;
    });

    public static string StripMetadata(string src) => OnSta(() =>
    {
        var ext = Path.GetExtension(src).ToLowerInvariant();
        if (ext is not (".jpg" or ".jpeg" or ".png" or ".tif" or ".tiff" or ".bmp")) ext = ".jpg";
        var bytes = Encode(Load(src), ext, 92);
        var outp = OutPath(src, ext, "-clean");
        File.WriteAllBytes(outp, bytes);
        return outp;
    });

    // ------------------------------------------------------------ documents
    public static string DocxToMarkdown(string src)
    {
        var outp = OutPath(src, ".md");
        File.WriteAllText(outp, DocxText(src, true), new UTF8Encoding(false));
        return outp;
    }

    public static string DocxToText(string src)
    {
        var outp = OutPath(src, ".txt");
        File.WriteAllText(outp, DocxText(src, false), new UTF8Encoding(false));
        return outp;
    }

    static string DocxText(string path, bool markdown)
    {
        using var doc = WordprocessingDocument.Open(path, false);
        var body = doc.MainDocumentPart?.Document?.Body;
        if (body == null) return "";
        var sb = new StringBuilder();
        foreach (var el in body.ChildElements)
        {
            if (el is W.Paragraph p) sb.Append(ParagraphText(p, markdown)).Append('\n').Append(markdown ? "\n" : "");
            else if (el is W.Table t)
            {
                bool first = true;
                foreach (var row in t.Elements<W.TableRow>())
                {
                    var cells = row.Elements<W.TableCell>().Select(c => string.Join(" ", c.Elements<W.Paragraph>().Select(pp => ParagraphText(pp, false))).Replace("|", "\\|").Trim()).ToList();
                    if (markdown)
                    {
                        sb.Append("| ").Append(string.Join(" | ", cells)).Append(" |\n");
                        if (first) sb.Append("|").Append(string.Join("|", cells.Select(_ => " --- "))).Append("|\n");
                    }
                    else sb.Append(string.Join("\t", cells)).Append('\n');
                    first = false;
                }
                sb.Append('\n');
            }
        }
        return Regex.Replace(sb.ToString(), "\n{3,}", "\n\n").Trim() + "\n";
    }

    static string ParagraphText(W.Paragraph p, bool markdown)
    {
        var sb = new StringBuilder();
        foreach (var child in p.ChildElements)
        {
            if (child is W.Run r) sb.Append(RunText(r, markdown));
            else if (child is W.Hyperlink h)
            {
                var inner = string.Concat(h.Elements<W.Run>().Select(rr => RunText(rr, false)));
                sb.Append(inner);
            }
        }
        var text = sb.ToString();
        if (!markdown) return text;
        var style = p.ParagraphProperties?.ParagraphStyleId?.Val?.Value ?? "";
        var m = Regex.Match(style, @"^Heading(\d)$", RegexOptions.IgnoreCase);
        if (m.Success) return new string('#', int.Parse(m.Groups[1].Value)) + " " + text.Trim();
        if (style.Equals("Title", StringComparison.OrdinalIgnoreCase)) return "# " + text.Trim();
        if (p.ParagraphProperties?.NumberingProperties != null || style.StartsWith("List", StringComparison.OrdinalIgnoreCase)) return "- " + text.Trim();
        if (style.Contains("Quote", StringComparison.OrdinalIgnoreCase)) return "> " + text.Trim();
        return text;
    }

    static string RunText(W.Run r, bool markdown)
    {
        var sb = new StringBuilder();
        foreach (var c in r.ChildElements)
        {
            if (c is W.Text t) sb.Append(t.Text);
            else if (c is W.TabChar) sb.Append('\t');
            else if (c is W.Break) sb.Append('\n');
        }
        var s = sb.ToString();
        if (!markdown || s.Trim().Length == 0) return s;
        var rp = r.RunProperties;
        bool bold = rp?.Bold != null && (rp.Bold.Val == null || rp.Bold.Val.Value);
        bool italic = rp?.Italic != null && (rp.Italic.Val == null || rp.Italic.Val.Value);
        bool code = rp?.RunFonts?.Ascii?.Value?.Contains("Courier", StringComparison.OrdinalIgnoreCase) == true || rp?.RunFonts?.Ascii?.Value?.Contains("Consolas", StringComparison.OrdinalIgnoreCase) == true;
        var lead = s.Length - s.TrimStart().Length; var trail = s.Length - s.TrimEnd().Length;
        var core = s.Trim();
        if (code) core = "`" + core + "`";
        if (bold && italic) core = "***" + core + "***"; else if (bold) core = "**" + core + "**"; else if (italic) core = "*" + core + "*";
        return s[..lead] + core + s[(s.Length - trail)..];
    }

    public static string MarkdownToHtml(string src)
    {
        var md = File.ReadAllText(src, Encoding.UTF8);
        var pipeline = new Markdig.MarkdownPipelineBuilder().UseAdvancedExtensions().Build();
        var body = Markdig.Markdown.ToHtml(md, pipeline);
        var html = "<!doctype html>\n<html><head><meta charset=\"utf-8\"><title>" + System.Net.WebUtility.HtmlEncode(Path.GetFileNameWithoutExtension(src)) + "</title>\n" +
            "<style>body{font-family:Segoe UI,system-ui,sans-serif;max-width:52rem;margin:2rem auto;padding:0 1rem;line-height:1.55}pre{background:#f4f4f4;padding:.8rem;overflow:auto}code{font-family:Consolas,monospace}table{border-collapse:collapse}td,th{border:1px solid #ccc;padding:.3rem .6rem}img{max-width:100%}</style></head>\n<body>\n" + body + "</body></html>\n";
        var outp = OutPath(src, ".html");
        File.WriteAllText(outp, html, new UTF8Encoding(false));
        return outp;
    }

    /// <summary>Markdown to DOCX: headings, paragraphs, bold/italic/code, bullet and numbered lists, code blocks, quotes.</summary>
    public static string MarkdownToDocx(string src)
    {
        var md = File.ReadAllText(src, Encoding.UTF8);
        var outp = OutPath(src, ".docx");
        using (var doc = WordprocessingDocument.Create(outp, WordprocessingDocumentType.Document))
        {
            var main = doc.AddMainDocumentPart();
            main.Document = new W.Document(new W.Body());
            var body = main.Document.Body!;
            AddStyles(main);
            var pipeline = new Markdig.MarkdownPipelineBuilder().UseAdvancedExtensions().Build();
            var ast = Markdig.Markdown.Parse(md, pipeline);
            foreach (var block in ast) AppendBlock(body, block, 0, false);
            body.Append(new W.SectionProperties());
            main.Document.Save();
        }
        return outp;
    }

    static void AddStyles(MainDocumentPart main)
    {
        var sp = main.AddNewPart<StyleDefinitionsPart>();
        var styles = new W.Styles();
        W.Style S(string id, string name, int size, bool bold, string? color = null)
        {
            var st = new W.Style { Type = W.StyleValues.Paragraph, StyleId = id };
            st.Append(new W.StyleName { Val = name });
            st.Append(new W.BasedOn { Val = "Normal" });
            var rp = new W.StyleRunProperties();
            if (bold) rp.Append(new W.Bold());
            rp.Append(new W.FontSize { Val = (size * 2).ToString() });
            if (color != null) rp.Append(new W.Color { Val = color });
            st.Append(rp);
            return st;
        }
        var normal = new W.Style { Type = W.StyleValues.Paragraph, StyleId = "Normal", Default = true };
        normal.Append(new W.StyleName { Val = "Normal" });
        normal.Append(new W.StyleRunProperties(new W.RunFonts { Ascii = "Calibri", HighAnsi = "Calibri" }, new W.FontSize { Val = "22" }));
        styles.Append(normal);
        styles.Append(S("Heading1", "heading 1", 18, true, "1F3864"));
        styles.Append(S("Heading2", "heading 2", 15, true, "2E74B5"));
        styles.Append(S("Heading3", "heading 3", 13, true, "2E74B5"));
        styles.Append(S("Heading4", "heading 4", 12, true));
        styles.Append(S("Heading5", "heading 5", 11, true));
        styles.Append(S("Heading6", "heading 6", 11, true));
        var quote = S("Quote", "Quote", 11, false, "404040"); quote.Append(new W.StyleParagraphProperties(new W.Indentation { Left = "720" })); styles.Append(quote);
        var code = new W.Style { Type = W.StyleValues.Paragraph, StyleId = "Code" };
        code.Append(new W.StyleName { Val = "Code" });
        code.Append(new W.StyleRunProperties(new W.RunFonts { Ascii = "Consolas", HighAnsi = "Consolas" }, new W.FontSize { Val = "19" }));
        code.Append(new W.StyleParagraphProperties(new W.Shading { Val = W.ShadingPatternValues.Clear, Fill = "F2F2F2" }, new W.SpacingBetweenLines { After = "0" }));
        styles.Append(code);
        sp.Styles = styles;
    }

    static void AppendBlock(W.Body body, Markdig.Syntax.Block block, int level, bool ordered)
    {
        switch (block)
        {
            case Markdig.Syntax.HeadingBlock h:
                body.Append(Para("Heading" + Math.Clamp(h.Level, 1, 6), Inlines(h.Inline)));
                break;
            case Markdig.Syntax.ParagraphBlock p:
                body.Append(Para(null, Inlines(p.Inline), level, ordered));
                break;
            case Markdig.Syntax.ListBlock list:
                foreach (var item in list) if (item is Markdig.Syntax.ListItemBlock li) foreach (var b in li) AppendBlock(body, b, level + 1, list.IsOrdered);
                break;
            case Markdig.Syntax.QuoteBlock q:
                foreach (var b in q) { if (b is Markdig.Syntax.ParagraphBlock qp) body.Append(Para("Quote", Inlines(qp.Inline))); else AppendBlock(body, b, level, ordered); }
                break;
            case Markdig.Syntax.FencedCodeBlock fc:
                foreach (var line in fc.Lines.Lines.Take(fc.Lines.Count)) body.Append(Para("Code", new[] { new W.Run(new W.Text(line.ToString()) { Space = SpaceProcessingModeValues.Preserve }) }));
                break;
            case Markdig.Syntax.CodeBlock cb:
                foreach (var line in cb.Lines.Lines.Take(cb.Lines.Count)) body.Append(Para("Code", new[] { new W.Run(new W.Text(line.ToString()) { Space = SpaceProcessingModeValues.Preserve }) }));
                break;
            case Markdig.Syntax.ThematicBreakBlock:
                body.Append(Para(null, new[] { new W.Run(new W.Text("―――――――――――――――――――")) }));
                break;
            case Markdig.Extensions.Tables.Table table:
                var t = new W.Table(new W.TableProperties(new W.TableBorders(
                    new W.TopBorder { Val = W.BorderValues.Single, Size = 4 }, new W.BottomBorder { Val = W.BorderValues.Single, Size = 4 },
                    new W.LeftBorder { Val = W.BorderValues.Single, Size = 4 }, new W.RightBorder { Val = W.BorderValues.Single, Size = 4 },
                    new W.InsideHorizontalBorder { Val = W.BorderValues.Single, Size = 4 }, new W.InsideVerticalBorder { Val = W.BorderValues.Single, Size = 4 })));
                foreach (var row in table.OfType<Markdig.Extensions.Tables.TableRow>())
                {
                    var tr = new W.TableRow();
                    foreach (var cell in row.OfType<Markdig.Extensions.Tables.TableCell>())
                    {
                        var runs = cell.OfType<Markdig.Syntax.ParagraphBlock>().SelectMany(pp => Inlines(pp.Inline)).ToList();
                        if (row.IsHeader) foreach (var r in runs) (r.RunProperties ??= new W.RunProperties()).Append(new W.Bold());
                        tr.Append(new W.TableCell(Para(null, runs)));
                    }
                    t.Append(tr);
                }
                body.Append(t);
                body.Append(Para(null, Array.Empty<W.Run>()));
                break;
            default:
                if (block is Markdig.Syntax.LeafBlock leaf && leaf.Inline != null) body.Append(Para(null, Inlines(leaf.Inline), level, ordered));
                else if (block is Markdig.Syntax.ContainerBlock cont) foreach (var b in cont) AppendBlock(body, b, level, ordered);
                break;
        }
    }

    static W.Paragraph Para(string? style, IEnumerable<W.Run> runs, int listLevel = 0, bool ordered = false)
    {
        var p = new W.Paragraph();
        var pp = new W.ParagraphProperties();
        if (style != null) pp.Append(new W.ParagraphStyleId { Val = style });
        if (listLevel > 0)
        {
            pp.Append(new W.Indentation { Left = (360 * listLevel + 360).ToString(), Hanging = "360" });
            p.Append(pp);
            p.Append(new W.Run(new W.Text(ordered ? "• " : "• ") { Space = SpaceProcessingModeValues.Preserve }));
        }
        else p.Append(pp);
        foreach (var r in runs) p.Append(r);
        return p;
    }

    static List<W.Run> Inlines(Markdig.Syntax.Inlines.ContainerInline? container, bool bold = false, bool italic = false, bool code = false)
    {
        var list = new List<W.Run>();
        if (container == null) return list;
        foreach (var inl in container)
        {
            switch (inl)
            {
                case Markdig.Syntax.Inlines.LiteralInline lit: list.Add(MakeRun(lit.Content.ToString(), bold, italic, code)); break;
                case Markdig.Syntax.Inlines.EmphasisInline em:
                    bool b = bold || em.DelimiterCount >= 2, i = italic || em.DelimiterCount == 1;
                    list.AddRange(Inlines(em, b, i, code)); break;
                case Markdig.Syntax.Inlines.CodeInline ci: list.Add(MakeRun(ci.Content, bold, italic, true)); break;
                case Markdig.Syntax.Inlines.LineBreakInline lb: list.Add(new W.Run(lb.IsHard ? new W.Break() : new W.Text(" ") { Space = SpaceProcessingModeValues.Preserve })); break;
                case Markdig.Syntax.Inlines.LinkInline link:
                    var inner = Inlines(link, bold, italic, code);
                    foreach (var r in inner) { (r.RunProperties ??= new W.RunProperties()).Append(new W.Underline { Val = W.UnderlineValues.Single }); r.RunProperties.Append(new W.Color { Val = "0563C1" }); }
                    list.AddRange(inner);
                    if (!string.IsNullOrEmpty(link.Url) && !link.IsImage) list.Add(MakeRun(" (" + link.Url + ")", false, false, false));
                    break;
                case Markdig.Syntax.Inlines.ContainerInline c: list.AddRange(Inlines(c, bold, italic, code)); break;
                case Markdig.Syntax.Inlines.HtmlInline html: list.Add(MakeRun(html.Tag, bold, italic, true)); break;
            }
        }
        return list;
    }

    static W.Run MakeRun(string text, bool bold, bool italic, bool code)
    {
        var r = new W.Run();
        var rp = new W.RunProperties();
        if (bold) rp.Append(new W.Bold());
        if (italic) rp.Append(new W.Italic());
        if (code) { rp.Append(new W.RunFonts { Ascii = "Consolas", HighAnsi = "Consolas" }); rp.Append(new W.Shading { Val = W.ShadingPatternValues.Clear, Fill = "F2F2F2" }); }
        if (rp.HasChildren) r.Append(rp);
        r.Append(new W.Text(text) { Space = SpaceProcessingModeValues.Preserve });
        return r;
    }

    public static string PdfToText(string src)
    {
        var sb = new StringBuilder();
        using (var pdf = UglyToad.PdfPig.PdfDocument.Open(src))
            foreach (var page in pdf.GetPages())
            {
                sb.Append(UglyToad.PdfPig.DocumentLayoutAnalysis.TextExtractor.ContentOrderTextExtractor.GetText(page)).Append("\n\n");
            }
        var outp = OutPath(src, ".txt");
        File.WriteAllText(outp, sb.ToString().Trim() + "\n", new UTF8Encoding(false));
        return outp;
    }

    // ------------------------------------------------------------ data files
    public static List<string[]> ParseCsv(string text)
    {
        var rows = new List<string[]>();
        var row = new List<string>(); var cell = new StringBuilder(); bool q = false;
        for (int i = 0; i < text.Length; i++)
        {
            var c = text[i];
            if (q)
            {
                if (c == '"') { if (i + 1 < text.Length && text[i + 1] == '"') { cell.Append('"'); i++; } else q = false; }
                else cell.Append(c);
            }
            else if (c == '"') q = true;
            else if (c == ',') { row.Add(cell.ToString()); cell.Clear(); }
            else if (c == '\r') { }
            else if (c == '\n') { row.Add(cell.ToString()); cell.Clear(); rows.Add(row.ToArray()); row.Clear(); }
            else cell.Append(c);
        }
        if (cell.Length > 0 || row.Count > 0) { row.Add(cell.ToString()); rows.Add(row.ToArray()); }
        return rows;
    }

    static string CsvCell(string s) => s.IndexOfAny(new[] { ',', '"', '\n', '\r' }) >= 0 ? "\"" + s.Replace("\"", "\"\"") + "\"" : s;

    public static string CsvToJson(string src)
    {
        var rows = ParseCsv(File.ReadAllText(src, Encoding.UTF8));
        var arr = new JsonArray();
        if (rows.Count > 0)
        {
            var head = rows[0];
            foreach (var r in rows.Skip(1))
            {
                if (r.Length == 1 && r[0].Length == 0) continue;
                var o = new JsonObject();
                for (int i = 0; i < head.Length; i++) o[head[i]] = i < r.Length ? Auto(r[i]) : null;
                arr.Add(o);
            }
        }
        var outp = OutPath(src, ".json");
        File.WriteAllText(outp, arr.ToJsonString(new JsonSerializerOptions { WriteIndented = true, Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping }), new UTF8Encoding(false));
        return outp;
    }

    static JsonNode? Auto(string s)
    {
        if (long.TryParse(s, out var l)) return l;
        if (double.TryParse(s, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var d) && s.Contains('.')) return d;
        if (s.Equals("true", StringComparison.OrdinalIgnoreCase)) return true;
        if (s.Equals("false", StringComparison.OrdinalIgnoreCase)) return false;
        return s;
    }

    public static string JsonToCsv(string src)
    {
        var node = JsonNode.Parse(File.ReadAllText(src, Encoding.UTF8));
        var arr = node as JsonArray ?? throw new InvalidOperationException("JSON root must be an array of objects");
        var cols = new List<string>();
        foreach (var o in arr.OfType<JsonObject>()) foreach (var kv in o) if (!cols.Contains(kv.Key)) cols.Add(kv.Key);
        var sb = new StringBuilder();
        sb.AppendLine(string.Join(",", cols.Select(CsvCell)));
        foreach (var o in arr.OfType<JsonObject>())
            sb.AppendLine(string.Join(",", cols.Select(c => CsvCell(o[c] is JsonValue v ? v.ToString() : o[c]?.ToJsonString() ?? ""))));
        var outp = OutPath(src, ".csv");
        File.WriteAllText(outp, sb.ToString(), new UTF8Encoding(false));
        return outp;
    }

    public static string JsonToYaml(string src)
    {
        var obj = new YamlDotNet.Serialization.DeserializerBuilder().Build().Deserialize(new StringReader(File.ReadAllText(src, Encoding.UTF8)));
        var yaml = new YamlDotNet.Serialization.SerializerBuilder().Build().Serialize(obj);
        var outp = OutPath(src, ".yaml");
        File.WriteAllText(outp, yaml, new UTF8Encoding(false));
        return outp;
    }

    public static string YamlToJson(string src)
    {
        var obj = new YamlDotNet.Serialization.DeserializerBuilder().Build().Deserialize(new StringReader(File.ReadAllText(src, Encoding.UTF8)));
        var json = new YamlDotNet.Serialization.SerializerBuilder().JsonCompatible().Build().Serialize(obj);
        var pretty = JsonSerializer.Serialize(JsonDocument.Parse(json).RootElement, new JsonSerializerOptions { WriteIndented = true, Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping });
        var outp = OutPath(src, ".json");
        File.WriteAllText(outp, pretty, new UTF8Encoding(false));
        return outp;
    }

    public static string NormalizeText(string src, bool crlf)
    {
        var text = File.ReadAllText(src);
        text = Regex.Replace(text, "\r\n?|\n", crlf ? "\r\n" : "\n");
        var outp = OutPath(src, Path.GetExtension(src), crlf ? "-crlf" : "-lf");
        File.WriteAllText(outp, text, new UTF8Encoding(false));
        return outp;
    }

    // ------------------------------------------------------------ media (ffmpeg)
    public static string? FfmpegPath => Providers.FindOnPath("ffmpeg.exe") ?? (File.Exists(Path.Combine(Paths.DataDir, "tools", "ffmpeg.exe")) ? Path.Combine(Paths.DataDir, "tools", "ffmpeg.exe") : null);

    public static void InstallFfmpeg() => Providers.RunInConsole("winget install --id Gyan.FFmpeg -e --accept-package-agreements --accept-source-agreements", "Install ffmpeg");

    static string Ff(string src, string outp, params string[] args)
    {
        var exe = FfmpegPath ?? throw new InvalidOperationException("ffmpeg is not installed (Files menu -> Install ffmpeg).");
        var psi = new ProcessStartInfo(exe) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardError = true, RedirectStandardOutput = true };
        psi.ArgumentList.Add("-y"); psi.ArgumentList.Add("-hide_banner"); psi.ArgumentList.Add("-loglevel"); psi.ArgumentList.Add("error");
        psi.ArgumentList.Add("-i"); psi.ArgumentList.Add(src);
        foreach (var a in args) psi.ArgumentList.Add(a);
        psi.ArgumentList.Add(outp);
        using var p = Process.Start(psi)!;
        var err = p.StandardError.ReadToEndAsync(); p.StandardOutput.ReadToEndAsync();
        if (!p.WaitForExit(1_800_000)) { try { p.Kill(true); } catch { } throw new TimeoutException("ffmpeg timed out"); }
        if (p.ExitCode != 0) throw new InvalidOperationException("ffmpeg: " + Util.Trunc(err.Result.Trim(), 300));
        return outp;
    }

    public static string VideoToGif(string src, int width = 640, int fps = 12) =>
        Ff(src, OutPath(src, ".gif"), "-vf", $"fps={fps},scale={width}:-1:flags=lanczos,split[s0][s1];[s0]palettegen[p];[s1][p]paletteuse", "-loop", "0");
    public static string ExtractAudio(string src) => Ff(src, OutPath(src, ".mp3"), "-vn", "-c:a", "libmp3lame", "-q:a", "2");
    public static string CompressVideo(string src) => Ff(src, OutPath(src, ".mp4", "-small"), "-c:v", "libx264", "-crf", "28", "-preset", "medium", "-c:a", "aac", "-b:a", "128k", "-movflags", "+faststart");
    public static string ToMp4(string src) => Ff(src, OutPath(src, ".mp4"), "-c:v", "libx264", "-crf", "20", "-preset", "medium", "-c:a", "aac", "-b:a", "192k", "-movflags", "+faststart");
    public static string AudioToMp3(string src) => Ff(src, OutPath(src, ".mp3"), "-c:a", "libmp3lame", "-q:a", "2");
    public static string AudioToWav(string src) => Ff(src, OutPath(src, ".wav"));
    public static string Trim(string src, string start, string duration) => Ff(src, OutPath(src, Path.GetExtension(src), "-trim"), "-ss", start, "-t", duration, "-c", "copy");
    public static string VideoFrame(string src) => Ff(src, OutPath(src, ".png", "-frame"), "-frames:v", "1");

    // ------------------------------------------------------------ generic
    public static string Sha256(string src)
    {
        using var fs = File.OpenRead(src);
        return Convert.ToHexString(SHA256.HashData(fs)).ToLowerInvariant();
    }

    public static string ZipFiles(List<string> paths)
    {
        var first = paths[0];
        var dir = Path.GetDirectoryName(first) ?? "";
        var name = paths.Count == 1 ? Path.GetFileNameWithoutExtension(first) : Path.GetFileName(dir.TrimEnd('\\'));
        if (string.IsNullOrEmpty(name)) name = "archive";
        var outp = OutPath(Path.Combine(dir, name + ".zip"), ".zip");
        using var zip = ZipFile.Open(outp, ZipArchiveMode.Create);
        foreach (var p in paths)
        {
            if (Directory.Exists(p))
                foreach (var f in Directory.EnumerateFiles(p, "*", SearchOption.AllDirectories))
                    zip.CreateEntryFromFile(f, Path.Combine(Path.GetFileName(p), Path.GetRelativePath(p, f)).Replace('\\', '/'));
            else zip.CreateEntryFromFile(p, Path.GetFileName(p));
        }
        return outp;
    }

    /// <summary>Plain text of a file for AI actions (txt/md/csv/json, docx, pdf).</summary>
    public static string TextOf(string path)
    {
        var kind = Kind(path);
        if (kind == "docx") return DocxText(path, true);
        if (kind == "pdf")
        {
            var sb = new StringBuilder();
            using var pdf = UglyToad.PdfPig.PdfDocument.Open(path);
            foreach (var page in pdf.GetPages()) sb.Append(UglyToad.PdfPig.DocumentLayoutAnalysis.TextExtractor.ContentOrderTextExtractor.GetText(page)).Append("\n\n");
            return sb.ToString();
        }
        if (kind is "text" or "markdown" or "csv" or "json" or "yaml" || new FileInfo(path).Length < 2_000_000) return File.ReadAllText(path, Encoding.UTF8);
        throw new InvalidOperationException("Not a text-like file: " + Path.GetFileName(path));
    }
}
