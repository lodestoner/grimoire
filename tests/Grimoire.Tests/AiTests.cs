using Spellbook;

namespace Spellbook.Tests;

/// <summary>
/// Prompt composition and role routing. Offline: no provider is contacted, no credential is read,
/// and the fake providers below fail the test if anything tries to reach them.
/// </summary>
internal static class AiTests
{
    static void Check(bool value, string message) { if (!value) throw new InvalidOperationException(message); }

    /// <summary>Unverified CLI adapters cannot run; saved choices remain visible without substitution.</summary>
    public static void DisabledCliProviders()
    {
        var providers = new[] { "codex-cli", "gemini-cli" }
            .Select(id => Providers.ById(id) ?? throw new InvalidOperationException("Saved provider is no longer discoverable: " + id)).ToArray();

        // Safety gate for the red run: baseline Status only checks local file existence.
        // Assert every adapter is disabled before any test can call Complete or construct its UI.
        foreach (var provider in providers)
        {
            var status = provider.Status();
            Check(status.Contains("temporarily unavailable", StringComparison.OrdinalIgnoreCase) &&
                status.Contains("isolation", StringComparison.OrdinalIgnoreCase),
                provider.Id + " must report temporary unavailability until file isolation is verified");
            Check(!provider.IsReady(), provider.Id + " still advertises readiness");
        }

        foreach (var provider in providers)
        {
            bool refused = false;
            try { provider.Complete(new AiRequest("", "Synthetic instruction", "Synthetic selection", null, "synthetic-model"), 1000); }
            catch (InvalidOperationException error)
            {
                refused = true;
                Check(error.Message.Contains("temporarily unavailable", StringComparison.OrdinalIgnoreCase) &&
                    error.Message.Contains("isolation", StringComparison.OrdinalIgnoreCase),
                    provider.Id + " did not explain its isolation restriction");
            }
            Check(refused, provider.Id + " accepted a completion while disabled");
        }

        var settings = new Settings { RoleQuick = "codex-cli:retained-codex-model", RolePrecise = "gemini-cli:retained-gemini-model", RoleVision = "codex-cli:retained-vision-model" };
        Check(Ai.Resolve(AiRole.Quick, settings).provider.Id == "codex-cli", "disabled Quick role was substituted");
        Check(Ai.Resolve(AiRole.Precise, settings).provider.Id == "gemini-cli", "disabled Precise role was substituted");
        Check(Ai.Resolve(AiRole.Vision, settings).provider.Id == "codex-cli", "disabled Vision role was substituted");

        // Use the actual production card controls, but never show the form or invoke its actions.
        // Only these disabled adapters are present, so no API credential/status discovery is possible.
        Settings? saved = null;
        using var form = new ProviderDialog(settings, providers, draft => saved = draft.Copy(), synthetic: false);
        foreach (var provider in providers)
        {
            foreach (var suffix in new[] { "Install", "Auth", "Test", "TestModel" })
                Check(!form.Controls.Find(provider.Id + suffix, true).Single().Enabled,
                    provider.Id + " exposes an enabled " + suffix + " control");
            Check(form.Controls.Find(provider.Id + "Status", true).Single().Text.Contains("isolation", StringComparison.OrdinalIgnoreCase),
                provider.Id + " card does not explain why it is unavailable");
        }
        Check(form.TrySave(), "disabled provider choices could not be saved");
        Check(saved?.RoleQuick == "codex-cli:retained-codex-model" && saved.RolePrecise == "gemini-cli:retained-gemini-model" && saved.RoleVision == "codex-cli:retained-vision-model",
            "saving the provider dialog changed disabled role IDs or models");
        Check(settings.RoleQuick == "codex-cli:retained-codex-model" && settings.RolePrecise == "gemini-cli:retained-gemini-model" && settings.RoleVision == "codex-cli:retained-vision-model",
            "saving substituted the configured provider choices");
    }

    static void Throws<T>(Action action, string message) where T : Exception
    {
        try { action(); } catch (T) { return; }
        throw new InvalidOperationException(message);
    }

    static int Occurrences(string haystack, string needle)
    {
        int count = 0, at = 0;
        while (needle.Length > 0 && (at = haystack.IndexOf(needle, at, StringComparison.Ordinal)) >= 0) { count++; at += needle.Length; }
        return count;
    }

    /// <summary>The selected text reaches the provider exactly once, whether or not the prompt places it.</summary>
    public static void Composition()
    {
        const string text = "Selected paragraph.";
        const string other = "A second paragraph {text} that merely mentions the placeholder.";

        var (instruction, payload) = Spells.Compose("Summarize this:", text);
        Check(instruction == "Summarize this:" && payload == text, "a prompt without a placeholder must send the text once, separately");

        (instruction, payload) = Spells.Compose("Translate {text} into Spanish.", text);
        Check(instruction == "Translate " + text + " into Spanish.", "the placeholder must be filled in place");
        Check(payload.Length == 0, "text placed by {text} was also passed separately");
        Check(Occurrences(instruction + "\n\n" + payload, text) == 1, "selected text composed more than once");

        (instruction, payload) = Spells.Compose("Before {text} and after {text}.", text);
        Check(instruction == "Before " + text + " and after " + text + "." && payload.Length == 0, "repeated placeholders must all be filled and nothing appended");

        // Selected text that itself contains the placeholder is inserted literally, never re-expanded.
        (instruction, payload) = Spells.Compose("Rewrite: {text}", other);
        Check(instruction == "Rewrite: " + other && payload.Length == 0, "substituted text was re-expanded");

        // File prompts expand {file} and {text} the same way.
        const string file = @"C:\fixtures\notes.md";
        (instruction, payload) = Spells.Compose("Summarize {file}:", text, file);
        Check(instruction == "Summarize " + file + ":" && payload == text, "a file prompt without {text} must still send the contents once");

        (instruction, payload) = Spells.Compose("In {file}, rewrite {text}", text, file);
        Check(instruction == "In " + file + ", rewrite " + text && payload.Length == 0, "file prompt duplicated the file contents");
        Check(Occurrences(instruction + "\n\n" + payload, text) == 1, "file contents composed more than once");

        (instruction, payload) = Spells.Compose("Nothing to expand.", "", file);
        Check(instruction == "Nothing to expand." && payload.Length == 0, "empty selection changed the instruction");
    }

    /// <summary>An image only ever reaches the provider chosen for the role; no other backend is searched.</summary>
    public static void ImageRouting()
    {
        var textOnly = Providers.All.First(p => !p.SupportsImages);
        var imageCapable = Providers.All.First(p => p.SupportsImages);

        // An unconditionally ready, image-capable provider is in the table: exactly the kind of backend a
        // search would have borrowed. Only this one provider's readiness is read; no credential is touched.
        var borrowable = Providers.ById("local") ?? throw new InvalidOperationException("fixture expects the local provider");
        Check(borrowable.SupportsImages && borrowable.IsReady(), "fixture expects a ready image-capable provider to exist");

        var settings = new Settings { RoleVision = textOnly.Id, RoleQuick = textOnly.Id };
        Throws<InvalidOperationException>(() => Ai.Plan(AiRole.Vision, settings, hasImage: true), "an image was accepted by a provider that cannot read images");
        try { Ai.Plan(AiRole.Vision, settings, hasImage: true); }
        catch (InvalidOperationException e)
        {
            Check(e.Message.Contains(textOnly.Label, StringComparison.Ordinal), "the refusal must name the configured provider");
            Check(e.Message.Contains("Providers", StringComparison.Ordinal) && e.Message.Contains("Vision", StringComparison.Ordinal), "the refusal must say where to configure Vision");
            Check(!e.Message.Contains(imageCapable.Label, StringComparison.Ordinal), "the refusal must not offer to route to another provider");
        }

        // Text through the same role is unaffected.
        Check(Ai.Plan(AiRole.Vision, settings, hasImage: false).provider.Id == textOnly.Id, "text routing changed");

        // The chosen image-capable provider and its model are returned unchanged.
        var chosen = new Settings { RoleVision = imageCapable.Id + ":fixture-model" };
        var planned = Ai.Plan(AiRole.Vision, chosen, hasImage: true);
        Check(ReferenceEquals(planned.provider, Providers.ById(imageCapable.Id)) && planned.model == "fixture-model", "the configured image provider or model was replaced");

        // An empty Precise/Vision role is no override: it reuses the configured Quick provider, nothing else.
        var quickOnly = new Settings { RoleQuick = imageCapable.Id };
        Check(Ai.Plan(AiRole.Vision, quickOnly, hasImage: true).provider.Id == imageCapable.Id, "empty Vision role did not reuse the Quick provider");
        Check(Ai.Plan(AiRole.Precise, quickOnly, hasImage: false).provider.Id == imageCapable.Id, "empty Precise role did not reuse the Quick provider");

        var quickTextOnly = new Settings { RoleQuick = textOnly.Id };
        Throws<InvalidOperationException>(() => Ai.Plan(AiRole.Vision, quickTextOnly, hasImage: true), "an empty Vision role searched for an image-capable provider");

        // Every role unset stays local-only, and an unknown id is an error rather than a substitution.
        Throws<InvalidOperationException>(() => Ai.Plan(AiRole.Quick, new Settings(), hasImage: false), "an unconfigured install resolved a provider");
        Throws<InvalidOperationException>(() => Ai.Plan(AiRole.Vision, new Settings(), hasImage: true), "an unconfigured install resolved an image provider");
        Throws<InvalidOperationException>(() => Ai.Plan(AiRole.Quick, new Settings { RoleQuick = "no-such-provider" }, hasImage: false), "an unknown provider id was silently replaced");
    }
}
