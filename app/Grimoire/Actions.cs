using System.Text.RegularExpressions;

namespace Spellbook;

/// <summary>Everything the menu and hotkeys can do. Runs on the UI thread; AI and file work is awaited off-thread.</summary>
internal sealed class Actions
{
    readonly TrayApp _app;
    Settings S => _app.Settings;

    /// <summary>The window that had focus when the menu/hotkey fired; results go back there.</summary>
    public IntPtr Target;

    public Actions(TrayApp app) { _app = app; }

    public const string IN_GRAMMAR = "Correct only the spelling, grammar, and punctuation. Preserve meaning, tone, length, and formatting. Output ONLY the corrected text — no preamble, no quotes, no commentary.";
    public const string IN_TIGHTEN = "Tighten this text: cut redundancy and wordiness while preserving meaning and voice. Output ONLY the revised text.";
    public const string IN_CLARITY = "Rewrite for clarity and flow at roughly the same length, preserving meaning and voice. Output ONLY the rewritten text.";
    public const string IN_HEADLINE = "Rewrite this as a single concise, accurate AP-style headline. Output ONLY the headline.";
    public const string IN_HEADIDEAS = "Write 5 strong, accurate headline options for this text. Number them 1-5.";
    public const string IN_SUMMARY = "Summarize this text in 3-5 clear sentences.";
    public const string IN_BULLETS = "Summarize this as concise bullet points.";
    public const string IN_EXPLAIN = "Explain clearly and concisely what this text or code does/means.";
    public const string IN_BUGS = "Review this code for bugs, edge cases, and issues. List concrete findings with brief fixes.";
    public const string IN_COMMENT = "Add clear, idiomatic comments/docstrings to this code. Output ONLY the commented code — no markdown fences, no explanation.";

    static void Notify(string m, int ms = 3500) => Toast.Show(m, ms);

    void FocusTarget() { if (Target != IntPtr.Zero) Input.ForceForeground(Target); }

    string Selection()
    {
        FocusTarget();
        var t = Clip.GetSelection();
        if (t.Length == 0) Notify("No text selected");
        return t;
    }

    static string StyleFor(string text) => TextStats.LooksLikeCode(text) ? Paths.StyleCode : Paths.StyleProse;

    readonly OperationLifecycle _operations = new();

    // These entry points share Actions' UI-thread ownership. StopAsync waits through cleanup,
    // not merely worker completion, before TrayApp exits its message loop.
    public void CancelActive() => _operations.CancelActive();
    public Task CancelAndWaitAsync() => _operations.StopAsync();

    /// <summary>One operation at a time; cancellation suppresses all result delivery.
    /// Worker APIs inherit OperationContext.Token. A five-minute deadline covers complete loops.</summary>
    public async Task<string?> Busy(string busy, Func<string> work)
    {
        using var operation = _operations.TryBegin();
        if (operation == null)
        {
            if (!_operations.IsQuitting) Notify("An operation is already running. Cancel it or wait for it to finish.");
            return null;
        }
        var token = operation.Token;
        OperationProgressForm? progress = null;
        try
        {
            // Construction and Show can fail; the lease must still be released in either case.
            progress = new OperationProgressForm(busy, operation.Cancel);
            progress.Show();
            var task = Task.Run(() =>
            {
                using var scope = OperationContext.Push(token);
                token.ThrowIfCancellationRequested();
                var value = work();
                token.ThrowIfCancellationRequested();
                return value;
            });
            var result = await task;
            if (token.IsCancellationRequested || _operations.IsQuitting) return null;
            if (string.IsNullOrWhiteSpace(result)) { Notify("No result"); return null; }
            return result;
        }
        catch (OperationCanceledException) { if (!_operations.IsQuitting) Notify("Operation cancelled"); return null; }
        catch (Exception e)
        {
            Log.Error("Busy", e);
            if (!_operations.IsQuitting && !token.IsCancellationRequested) Notify("[x] " + Util.Trunc(e.Message, 140), 6000);
            return null;
        }
        finally
        {
            try { progress?.Finish(); }
            finally { progress?.Dispose(); }
        }
    }

    Task<string?> Ask(string busy, Func<string> work) => Busy(busy, work);

    // ------------------------------------------------------------ local transforms
    public void Apply(string key)
    {
        Log.Write($"Apply({key}) target=0x{Target:X}");
        var text = Selection();
        if (text.Length == 0) return;
        string outText;
        try { outText = Transforms.Run(text, key); }
        catch (Exception e) { Log.Error("Transform " + key, e); Notify("[x] " + key + " failed: " + Util.Trunc(e.Message, 90)); return; }
        if (outText.Length == 0) return;
        Clip.PasteText(Util.MatchNewlines(outText, text));
    }

    public void PastePlain() { FocusTarget(); Clip.PastePlain(); }

    public void PasteString(string text) { FocusTarget(); Clip.PasteText(text); }

    public void Insert(string kind)
    {
        var now = DateTime.Now;
        var txt = kind switch
        {
            "isodate" => now.ToString("yyyy-MM-dd"),
            "longdate" => now.ToString("MMMM d, yyyy"),
            "stamp" => now.ToString("yyyy-MM-dd HH:mm"),
            "dateline" => "CITY, " + now.ToString("MMMM d") + " — ",
            "uuid" => Guid.NewGuid().ToString(),
            "lorem" => "Lorem ipsum dolor sit amet, consectetur adipiscing elit, sed do eiusmod tempor incididunt ut labore et dolore magna aliqua. Ut enim ad minim veniam, quis nostrud exercitation ullamco laboris nisi ut aliquip ex ea commodo consequat.",
            _ => "",
        };
        if (txt.Length == 0) return;
        FocusTarget();
        Input.TypeText(txt);
    }

    // ------------------------------------------------------------ AI
    /// <param name="mode">replace = paste in place; show = read-only popup; review = editable popup.</param>
    public async void ClaudeAct(string instruction, string mode, AiRole role = AiRole.Quick)
    {
        var target = Target;
        var text = Selection();
        if (text.Length == 0) return;
        var outText = await Ask(Ai.Describe(role) + " is thinking...", () => Ai.Run(role, text, instruction, mode != "show"));
        if (outText == null) return;
        Deliver(mode, "AI", outText, text, target);
    }

    void Deliver(string mode, string title, string outText, string original, IntPtr target)
    {
        if (mode == "show") ResultForm.Show(title, outText);
        else if (mode == "review") EditForm.Show(title, outText, t => Clip.PasteText(Util.MatchNewlines(t, original), target));
        else Clip.PasteText(Util.MatchNewlines(outText, original), target);
    }

    public void ClaudeCustom()
    {
        var target = Target;
        var text = Selection();
        if (text.Length == 0) return;
        var instr = InputDialog.Show("Ask AI", "Instruction (runs on the highlighted text):");
        if (string.IsNullOrWhiteSpace(instr)) return;
        RunNamed("AI", instr.Trim(), "show", text, target, AiRole.Quick, null);
    }

    async void RunNamed(string title, string instr, string mode, string text, IntPtr target, AiRole role, string? style)
    {
        var outText = await Ask(Ai.Describe(role) + " is thinking...", () => Ai.Run(role, text, instr, mode != "show", style));
        if (outText == null) return;
        Deliver(mode, title, outText, text, target);
    }

    public void PolishPrecise()
    {
        var target = Target;
        var text = Selection();
        if (text.Length == 0) return;
        bool code = TextStats.LooksLikeCode(text);
        var instr = code
            ? "Carefully polish this code: fix bugs and tighten it to the house style while preserving behavior. Output ONLY the code."
            : "Polish this text precisely: fix every grammar, spelling, and punctuation issue and improve clarity and flow, preserving meaning, voice, and length. Be strictly correct (including pronoun case, agreement, and AP style). Output ONLY the polished text.";
        RunNamed("Polish (precise)", instr, "review", text, target, AiRole.Precise, StyleFor(text));
    }

    public void ApplyHouseStyle()
    {
        var target = Target;
        var text = Selection();
        if (text.Length == 0) return;
        bool code = TextStats.LooksLikeCode(text);
        var instr = code
            ? "Revise this code to follow the provided house style and fix obvious issues, preserving behavior. Output ONLY the code."
            : "Edit this text to follow the provided house style, fixing style and consistency issues while preserving meaning, facts, and voice. Output ONLY the edited text.";
        RunNamed("House " + (code ? "code" : "prose") + " style", instr, "review", text, target, AiRole.Quick, StyleFor(text));
    }

    public async void MetricRewrite(string kind)
    {
        var target = Target;
        var text = Selection();
        if (text.Length == 0) return;
        var ans = kind == "grade"
            ? InputDialog.Show("Reading grade", "Target reading grade level (lower = simpler):", "8")
            : InputDialog.Show("Word count", "Target maximum word count:", "400");
        if (ans == null || !double.TryParse(ans.Trim(), out var targetVal)) return;

        var cur = await Ask("Rewriting to the target metric...", () =>
        {
            var current = text;
            for (int pass = 1; pass <= 4; pass++)
            {
                OperationContext.Token.ThrowIfCancellationRequested();
                double metric = kind == "grade" ? TextStats.Grade(current) : TextStats.Words(current);
                if ((kind == "grade" && metric <= targetVal + 0.3) || (kind == "words" && metric <= targetVal)) break;
                var instruction = kind == "grade"
                    ? $"Rewrite the text so its Flesch-Kincaid grade level is {targetVal} or lower, while preserving meaning, facts, names, and voice. It currently reads at about grade {metric:0.0}. Use shorter sentences and simpler word choices. Output ONLY the rewritten text."
                    : $"Cut this text to {targetVal} words or fewer while preserving the key facts, meaning, and voice. It is currently {metric:0} words. Output ONLY the revised text.";
                current = Ai.Run(AiRole.Quick, current, instruction, true, Paths.StyleProse);
            }
            return current;
        });
        if (cur == null) return;
        var final = kind == "grade" ? $"grade ~{TextStats.Grade(cur):0.0}" : $"{TextStats.Words(cur)} words";
        Clip.PasteText(Util.MatchNewlines(cur, text), target);
        Notify($"Done - now {final}. Pasted (Ctrl+Z to undo).", 5000);
    }

    public void Translate(string lang) => ClaudeAct("Translate the text to " + lang + ". Output ONLY the translation, nothing else.", "replace");

    // ------------------------------------------------------------ recipes
    static readonly Dictionary<string, string> ClaudeSteps = new(StringComparer.OrdinalIgnoreCase)
    {
        ["grammar"] = IN_GRAMMAR, ["tighten"] = IN_TIGHTEN, ["clarity"] = IN_CLARITY, ["headline"] = IN_HEADLINE,
    };

    string RunStep(string text, string key)
    {
        if (key.Equals("housestyle", StringComparison.OrdinalIgnoreCase))
        {
            bool code = TextStats.LooksLikeCode(text);
            var instr = code
                ? "Revise this code to follow the provided house style and fix obvious issues, preserving behavior. Output ONLY the code."
                : "Edit this text to follow the provided house style while preserving meaning, facts, and voice. Output ONLY the edited text.";
            return Ai.Run(AiRole.Quick, text, instr, true, StyleFor(text));
        }
        if (ClaudeSteps.TryGetValue(key, out var ci)) return Ai.Run(AiRole.Quick, text, ci, true);
        var spell = Spells.All().FirstOrDefault(s => s.Name.Equals(key, StringComparison.OrdinalIgnoreCase));
        if (spell != null) return Spells.RunText(spell, text);
        return Transforms.Run(text, key.ToLowerInvariant());
    }

    public async void RunRecipe(string name)
    {
        var target = Target;
        var text = Selection();
        if (text.Length == 0) return;
        var steps = Recipes.Steps(name).Split(',').Select(x => x.Trim()).Where(x => x.Length > 0).ToList();
        if (steps.Count == 0) { Notify($"Recipe '{name}' has no steps"); return; }
        var cur = await Ask($"Recipe '{name}'...", () =>
        {
            var current = text;
            foreach (var step in steps)
            {
                OperationContext.Token.ThrowIfCancellationRequested();
                current = RunStep(current, step);
                if (string.IsNullOrWhiteSpace(current)) throw new InvalidOperationException("A recipe step produced no result.");
            }
            return current;
        });
        if (cur == null) return;
        EditForm.Show("Recipe: " + name, cur, t => Clip.PasteText(Util.MatchNewlines(t, text), target));
    }

    // ------------------------------------------------------------ custom spells
    public async void RunSpell(Spell spell)
    {
        var target = Target;
        var text = Selection();
        if (text.Length == 0) return;
        var outText = await Ask($"Casting '{spell.Name}'...", () => Spells.RunText(spell, text));
        if (outText == null) return;
        if (spell.Type == "http" && !spell.ShowResponse) { Notify("Sent: " + spell.Name); return; }
        Deliver(spell.Mode, spell.Name, outText, text, target);
    }

    // ------------------------------------------------------------ analysis
    public void WordCount()
    {
        var text = Selection();
        if (text.Length == 0) return;
        int words = TextStats.Words(text);
        int chars = text.Length, nospace = Regex.Replace(text, @"\s", "").Length;
        int sentences = Regex.Matches(text, "[.!?]").Count;
        double mins = Math.Round(words / 200.0, 1);
        Notify($"Words: {words}      Characters: {chars} ({nospace} no spaces)\nSentences: ~{sentences}      Reading time: ~{mins} min", 7000);
    }

    public void Readability()
    {
        var text = Selection();
        if (text.Length == 0) return;
        var (fre, fk, wc, sc, syl) = TextStats.Flesch(text);
        if (wc == 0) { Notify("No words found"); return; }
        Notify($"Flesch Reading Ease: {fre:0.0}  ({TextStats.EaseLabel(fre)})\nFlesch-Kincaid Grade: ~{fk:0.0}\n{wc} words, ~{sc} sentences, {syl} syllables", 8000);
    }

    public void SearchWeb()
    {
        var text = Selection().Trim();
        if (text.Length == 0) return;
        Util.OpenUrl("https://www.google.com/search?q=" + Uri.EscapeDataString(text));
    }

    public async void AuditCode()
    {
        var text = Selection();
        if (text.Length == 0) return;
        const string instr = "Audit this code or string. Reply in three short sections with these exact headers:\n" +
            "SYNTAX: detect the language; is it valid? list any syntax errors with the fix.\n" +
            "QUOTES/ESCAPING: flag nested-quote breakage, shell-injection-prone quoting, format-string or escape mistakes.\n" +
            "STYLE/CONVENTIONS: naming, inconsistent quoting, spacing, line length.\n" +
            "Be concise. Write 'OK' for any clean section.";
        var report = await Ask("Auditing selection...", () => Ai.Run(AiRole.Quick, text, instr));
        if (report == null) return;
        ResultForm.Show("Audit", "Quick local checks:\n" + TextStats.BalanceCheck(text) + "\n\n" + (report ?? "(no report)"));
    }

    // ------------------------------------------------------------ capture / files
    Image? Snip()
    {
        try { Clipboard.Clear(); } catch { }
        Notify("Snip a screen region...", 2500);
        Vision.LaunchSnip();
        if (!Clip.WaitForImage(30)) { Notify("No region captured"); return null; }
        var img = Clip.GetImage();
        if (img == null) Notify("No image on the clipboard");
        return img;
    }

    /// <summary>mode: ocr | reproduce | describe | save | ocr-note</summary>
    public async void VisionAction(string mode)
    {
        var target = Target;
        var img = Snip();
        if (img == null) return;
        if (mode == "save")
        {
            try { var p = Vision.SavePng(img, S.ScreenshotDir, "snip_"); Notify("Saved " + p, 4000); } catch (Exception e) { Notify("[x] " + e.Message); }
            img.Dispose(); return;
        }
        var outText = await Ask(Ai.Describe(AiRole.Vision) + " is reading the image...", () => { using (img) return Vision.Run(img, mode == "ocr-note" ? "ocr" : mode); });
        if (outText == null) return;
        if (mode == "ocr-note")
        {
            var root = S.NextcloudRoot;
            var path = await Ask("Filing note...", () => Notes.Capture(outText, root));
            if (path != null) ResultForm.Show("Captured", "Saved:\n" + path);
        }
        else if (mode == "reproduce") EditForm.Show("Reproduce as HTML", outText, t => Clip.PasteText(t, target));
        else ResultForm.Show(mode == "ocr" ? "OCR" : "Describe", outText);
    }

    public async void PageSourceAction()
    {
        var url = Selection().Trim();
        if (url.Length == 0) return;
        if (!Regex.IsMatch(url, "^https?://", RegexOptions.IgnoreCase)) { Notify("Highlight an http(s) URL first"); return; }
        var content = await Ask("Fetching page source...", () => PageSource.Fetch(url));
        if (content != null) ResultForm.Show("Page source", content);
    }

    public async void CaptureNotes()
    {
        var text = Selection();
        if (text.Length == 0) return;
        var root = S.NextcloudRoot;
        var path = await Ask("Filing (AI classifying)...", () => Notes.Capture(text, root));
        if (path != null) ResultForm.Show("Captured", "Saved:\n" + path);
    }

    public async void CapturePaperless()
    {
        var text = Selection();
        if (text.Length == 0) return;
        var title = InputDialog.Show("Paperless", "Document title:", Util.Trunc(Regex.Replace(text.Trim(), @"\s+", " "), 60));
        if (string.IsNullOrWhiteSpace(title)) return;
        var s = S;
        var r = await Ask("Uploading to Paperless...", () => Paperless.UploadText(s, text, title.Trim()));
        if (r != null) Notify(r, 5000);
    }

    public async void PushNtfy()
    {
        var text = Selection();
        if (text.Length == 0) return;
        var s = S;
        var r = await Ask("Pushing to phone...", () => Ntfy.Push(s, text, Brand.Name));
        if (r != null) Notify(r, 3000);
    }

    public async void Reminder()
    {
        FocusTarget();
        var text = Clip.GetSelection();
        var r = ReminderForm.Show(text, Reminders.Targets(S));
        if (r == null) return;
        var (t, when, tgt) = r.Value;
        var s = S;
        var conf = await Ask("Scheduling reminder...", () => Reminders.Create(t, when, tgt, s));
        if (conf != null) ResultForm.Show("Reminder", conf);
    }

    public async void FleetRunAction()
    {
        FocusTarget();
        var text = Clip.GetSelection().Trim();
        var hosts = Fleet.Hosts(S);
        if (hosts.Count == 0) { Notify("No fleet hosts configured (Settings -> Integrations)"); return; }
        var r = FleetForm.Show(text, hosts);
        if (r == null) return;
        var (sel, cmd) = r.Value;
        if (sel.Count == 0) { Notify("Fleet: no hosts selected"); return; }
        if (string.IsNullOrWhiteSpace(cmd)) { Notify("Fleet: no command"); return; }
        var outText = await Ask($"Fleet: running on {sel.Count} host(s)...", () => Fleet.Run(sel, cmd.Trim()));
        if (outText != null) ResultForm.Show("Fleet run", outText);
    }

    public void SaveSnippet()
    {
        var text = Selection();
        if (text.Length == 0) return;
        var name = InputDialog.Show("Save snippet", "Snippet name:", Util.Trunc(Regex.Replace(text.Trim(), @"\s+", " "), 40));
        if (string.IsNullOrWhiteSpace(name)) return;
        Snippets.Save(name.Trim(), text);
        Notify("Saved snippet '" + name.Trim() + "'");
        _app.RebuildMenu();
    }

    // ------------------------------------------------------------ file actions (Explorer selection / drop zone)
    public async void FileBatch(List<string> files, string label, Func<string, string> op)
    {
        var results = new List<string>();
        var errors = new List<string>();
        var completed = await Busy(label + "...", () =>
        {
            for (int i = 0; i < files.Count; i++)
            {
                OperationContext.Token.ThrowIfCancellationRequested();
                try { results.Add(op(files[i])); }
                catch (OperationCanceledException) { throw; }
                catch (Exception e) { errors.Add(Path.GetFileName(files[i]) + ": " + e.Message); Log.Error("file-batch", e); }
            }
            return "completed";
        });
        if (completed == null) return;
        if (errors.Count == 0) Notify($"{label}: {results.Count} file(s) done", 4000);
        else ResultForm.Show(label, $"{results.Count} done, {errors.Count} failed\n\n" + string.Join("\n", results) + "\n\nErrors:\n" + string.Join("\n", errors));
    }

    public async void FileSummarize(List<string> files, string instruction, string title)
    {
        var text = await Busy("Reading file(s)...", () => string.Join("\n\n-----\n\n", files.Select(f => $"# {Path.GetFileName(f)}\n\n{Util.Trunc(Files.TextOf(f), 60_000)}")));
        if (text == null) return;
        var outText = await Ask(Ai.Describe(AiRole.Quick) + " is reading...", () => Ai.Run(AiRole.Quick, text, instruction));
        if (outText != null) ResultForm.Show(title, outText);
    }

    /// <summary>mode: describe | ocr, per image, through the vision provider.</summary>
    public async void FileSummarizeImage(List<string> files, string mode)
    {
        var instr = mode == "ocr"
            ? "Transcribe ALL text in this image exactly, preserving line breaks. Output only the text:"
            : "Describe this image concisely: what it shows, any text, and anything notable.";
        var outText = await Ask(Ai.Describe(AiRole.Vision) + " is looking...", () =>
        {
            var sb = new System.Text.StringBuilder();
            foreach (var f in files)
            {
                OperationContext.Token.ThrowIfCancellationRequested();
                var png = Files.ToTempPng(f);
                try { sb.Append("# ").Append(Path.GetFileName(f)).Append("\n\n").Append(Ai.Run(AiRole.Vision, "", instr, imagePath: png)).Append("\n\n"); }
                finally { try { File.Delete(png); } catch { } }
            }
            return sb.ToString().TrimEnd();
        });
        if (outText != null) ResultForm.Show(mode == "ocr" ? "OCR" : "Describe", outText);
    }

    public async void FileSpell(List<string> files, Spell spell)
    {
        if (files.Count == 1 && spell.Type == "prompt")
        {
            var f = files[0];
            var outText = await Ask($"Casting '{spell.Name}'...", () => Spells.RunFile(spell, f));
            if (outText != null) ResultForm.Show(spell.Name, outText);
            return;
        }
        FileBatch(files, spell.Name, f => Spells.RunFile(spell, f));
    }

    public async void FilePaperless(List<string> files)
    {
        var s = S;
        var r = await Busy("Uploading to Paperless...", () => string.Join("\n", files.Select(f => Paperless.Upload(s, f))));
        if (r != null) ResultForm.Show("Paperless", r);
    }
}
