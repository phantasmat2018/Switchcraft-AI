using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace Switchcraft;

public sealed class LanguageDetection : IDisposable
{
    private readonly NativeEngine engine;
    private readonly SettingsStore settings;
    public ApiKeyStore Keys { get; }
    public TypeSafeDetector Api { get; }
    public LayaDetector Local { get; }
    public bool UsesCloud => settings.Value.DetectionMode == "typesafe";
    public bool UsesLocalModel => settings.Value.DetectionMode == "laya";
    public bool UsesAi => UsesCloud || UsesLocalModel;
    public string ModeLabel => UsesCloud ? "TypeSafe AI" : "Laya · локально";
    public string Status => UsesCloud ? Api.Status : Local.Status;
    public LanguageDetection(NativeEngine engine, SettingsStore settings, TypeSafeDetector? api = null, LayaDetector? localModel = null)
    { this.engine = engine; this.settings = settings; Api = api ?? new(); Local = localModel ?? new(); Local.Configure(settings.Value.LayaDevice, settings.Value.LayaCpuThreads); Keys = new(settings.DirectoryPath); }
    public void Reset() { Api.Reset(); Local.Configure(settings.Value.LayaDevice, settings.Value.LayaCpuThreads); Local.Reset(); }
    public async Task PrepareAsync(CancellationToken cancellation = default)
    {
        if (!UsesLocalModel) return;
        try { await Local.PrepareAsync(settings.Value.LayaModelPath, cancellation).ConfigureAwait(false); }
        catch (OperationCanceledException) { }
    }
    public static bool Sendable(string word) => word.Length is >= 1 and <= 80
        && word.Any(char.IsLetter)
        && Regex.IsMatch(word.Normalize(NormalizationForm.FormC), @"^[\p{L}'’ʼ\-.,!?«»“”""():;\[\]{}<>`~]+$", RegexOptions.None, TimeSpan.FromMilliseconds(25))
        && !Regex.IsMatch(word, @"\p{L}\.\p{L}", RegexOptions.None, TimeSpan.FromMilliseconds(25));
    private static readonly Regex TokenRegex = new(@"\A[«“""(\[{]*(?<word>[А-Яа-яЁёІіЇїЄєҐґA-Za-z]+(?:['’ʼ-][А-Яа-яЁёІіЇїЄєҐґA-Za-z]+)*)[.,!?;:…»”"")\]}]*\z", RegexOptions.Compiled);
    public static string Core(string input) => TokenRegex.Match(input) is { Success: true } m ? m.Groups["word"].Value : input;
    internal static bool IsShortMismatch(string word) => Core(word) is "ы" or "Ы";
    internal static bool ShortLatinAcronym(string word)
    {
        var core = word.Trim('«', '»', '“', '”', '"', '(', ')', '[', ']', '{', '}', '.', ',', '!', '?', ':', ';');
        return core.Length is >= 2 and <= 5 && core.All(ch => ch is >= 'A' and <= 'Z');
    }
    internal static bool LetterMention(string word, string context) => word.IndexOfAny(new[] { '«', '»', '“', '”', '"' }) >= 0
        || Regex.IsMatch(context, @"\b(букв[\p{L}]*|літер[\p{L}]*|символ[\p{L}]*|звук[\p{L}]*|алфав[\p{L}]*|абет[\p{L}]*|letter[\p{L}]*)\b", RegexOptions.IgnoreCase, TimeSpan.FromMilliseconds(25));
    private Correction ShortFallback(Correction original, string preceding, string following)
    {
        if (!IsShortMismatch(original.Original) || !settings.Value.Languages.Contains("uk") || LetterMention(original.Original, preceding + " " + following)) return original;
        // ы is not a Ukrainian word. Require nearby, independently recognized Ukrainian
        // text; never make a global ы -> і rule or rewrite a discussion of the letter.
        var evidence = (preceding + " " + following).Split(' ', StringSplitOptions.RemoveEmptyEntries)
            .Select(engine.IdentifyLanguage).Where(x => x.Length > 0).ToArray();
        if (evidence.Length == 0 || evidence.Any(x => x != "uk")) return original;
        string changed = original.Original.Replace('ы', 'і').Replace('Ы', 'І');
        return new(original.Original, changed, true, .99, "uk", "short_context");
    }
    public async Task<Correction> EvaluateAsync(string word, string source, string languageContext, string precedingWords, CancellationToken cancellation, string followingWords = "")
    {
        cancellation.ThrowIfCancellationRequested();
        string mode = settings.Value.DetectionMode;
        var local = engine.Evaluate(word, source, languageContext);
        if (!settings.Value.Enabled || mode is not ("typesafe" or "laya") || !Sendable(word) || local.Reason is "user_pair" or "user_exception" or "protected_pattern" or "length_limit" or "engine_stopped") return local;
        // Short Latin capitals are commonly identifiers (RGB, VNC, API). Some
        // map to real Cyrillic words, so dictionary validity alone is insufficient.
        if (ShortLatinAcronym(word)) return new(word, word, false, 0, "en", "protected_pattern");
        string context = string.Join(" ", precedingWords.Split(' ', StringSplitOptions.RemoveEmptyEntries).Where(Sendable).TakeLast(6));
        if (context.Length > 180) context = "";
        string following = string.Join(" ", followingWords.Split(' ', StringSplitOptions.RemoveEmptyEntries).Where(Sendable).Take(3));
        if (following.Length > 160) following = "";
        if ((word.Length < 3 || IsShortMismatch(word)) && (context.Length == 0 && following.Length == 0 || LetterMention(word, context + " " + following))) return local;
        local = ShortFallback(local, context, following);
        var originalLanguages = engine.KnownLanguages(word);
        bool knownOriginal = originalLanguages.Length > 0;
        // A known word needs much stronger evidence than keyboard gibberish.
        // Only UK/RU grammatical ambiguities are eligible, with at least two
        // independently recognized nearby words in the other language.
        if (knownOriginal && !AmbiguityEvidence(word, originalLanguages, context, following))
            return new(word, word, false, 0, engine.IdentifyLanguage(word), "known_original");
        if (source.Length == 0) source = engine.InferLanguage(word);
        if (source.Length == 0) source = languageContext is "uk" or "ru" ? languageContext : "uk";
        var candidates = Candidates(word, source, local);
        // Some native paths overlook wrappers such as (руддщ). The same strict
        // dictionary check can still prove a unique mechanical layout repair.
        if (!knownOriginal && !local.Changed && candidates.Count == 2 && word.Count(char.IsLetter) >= 3)
        {
            var unique = candidates.Single(x => x.Key != "keep");
            local = new(word, unique.Value, true, .99, unique.Key, "layout_dictionary");
        }
        var candidateLanguages = candidates.ToDictionary(x => x.Key, x => engine.KnownLanguages(x.Value));
        Correction Fallback(string reason) => knownOriginal
            ? new(word, word, false, 0, "", reason)
            : local with { Target = local.Changed ? ResolveTarget(local.Text, source, languageContext) : local.Target, Reason = reason };
        if (candidates.Count < 2) return Fallback(local.Reason);
        // Provider selection is captured for this decision. Local inference never
        // escalates to a cloud request, including startup, timeout or model errors.
        var answer = mode == "laya"
            ? await Local.DecideAsync(settings.Value.LayaModelPath, word, context, Readings(word, source, local), candidates, cancellation, followingWords: following).ConfigureAwait(false)
            : await Api.DecideAsync(Keys.Key, settings.Value.TypeSafeModel, word, context, candidates, cancellation,
                followingWords: following, candidateLanguages: candidateLanguages).ConfigureAwait(false);
        cancellation.ThrowIfCancellationRequested();
        if (settings.Value.DetectionMode != mode) return engine.Evaluate(word, source, languageContext);
        string prefix = mode == "laya" ? "local_ai_" : "ai_";
        if (answer == null) return Fallback(prefix + "fallback");
        if (!answer.Certain) return Fallback(prefix + "uncertain");
        if (answer.Choice == "keep") return new(word, word, false, answer.Confidence, "", prefix + "keep");
        if (!candidates.TryGetValue(answer.Choice, out var chosen) || !settings.Value.Languages.Contains(answer.Choice)) return Fallback(prefix + "fallback");
        return new(word, chosen, true, answer.Confidence, ResolveTarget(chosen, source, languageContext), mode == "laya" ? "laya_ai" : "typesafe_ai");
    }
    private Dictionary<string, string> Readings(string word, string source, Correction local)
    {
        var readings = new[] { "en", "uk", "ru" }.ToDictionary(language => language, language => engine.Convert(word, language, source).Text);
        // Keep the verified single-letter/word repair available to the word-choice head.
        if (local.Changed && readings.ContainsKey(local.Target)) readings[local.Target] = local.Text;
        return readings;
    }
    public Dictionary<string, string> Candidates(string word, string source, Correction local)
    {
        var candidates = new Dictionary<string, string> { ["keep"] = word };
        foreach (var target in settings.Value.Languages.ToArray())
        {
            string converted = engine.Convert(word, target, source).Text;
            if (converted != word) candidates[target] = converted;
        }
        // Keep repair available even when its language is already active layout.
        if (local.Changed && local.Text != word && settings.Value.Languages.Contains(local.Target))
            candidates[local.Target] = local.Text;
        // Identical spellings must be one choice, otherwise probability is split
        // between UK/RU labels even when the model agrees on the actual text.
        return candidates.GroupBy(x => x.Value, StringComparer.Ordinal).ToDictionary(x => x.First().Key, x => x.Key);
    }
    private string ResolveTarget(string text, string source, string context)
    {
        var languages = engine.KnownLanguages(text).Where(settings.Value.Languages.Contains).ToArray();
        if (languages.Length == 1) return languages[0];
        if (languages.Contains(context)) return context;
        return languages.Contains(source) ? source : "";
    }
    public bool NeedsContext(string word, string source)
    {
        if (!Sendable(word) || ShortLatinAcronym(word)) return false;
        var local = engine.Evaluate(word, source);
        if (local.Reason is "user_pair" or "user_exception" or "protected_pattern" or "length_limit" or "engine_stopped") return false;
        if (local.Reason == "known_original")
        {
            var languages = engine.KnownLanguages(word);
            return settings.Value.Sensitivity != "careful" && languages.Length == 1 && languages[0] is "uk" or "ru"
                && Candidates(word, source, local).Any(x => x.Key != "keep" && engine.KnownLanguages(x.Value).Any(l => l is "uk" or "ru" && l != languages[0]));
        }
        return local.Changed || IsShortMismatch(word) || Candidates(word, source, local).Count > 1;
    }
    private bool AmbiguityEvidence(string word, string[] originalLanguages, string preceding, string following)
    {
        if (settings.Value.Sensitivity == "careful" || originalLanguages.Length != 1 || originalLanguages[0] is not ("uk" or "ru")
            || LetterMention(word, preceding + " " + following)) return false;
        string other = originalLanguages[0] == "uk" ? "ru" : "uk";
        if (!settings.Value.Languages.Contains(other)) return false;
        var neighbors = preceding.Split(' ', StringSplitOptions.RemoveEmptyEntries).TakeLast(3)
            .Concat(following.Split(' ', StringSplitOptions.RemoveEmptyEntries).Take(3))
            .Select(engine.IdentifyLanguage).Where(x => x.Length > 0).ToArray();
        return neighbors.Count(x => x == other) >= 2 && !neighbors.Contains(originalLanguages[0]);
    }
    public string ContextLanguage(string words)
    {
        var context = new TypingContext();
        foreach (var word in words.Split(' ', StringSplitOptions.RemoveEmptyEntries).TakeLast(6))
        { context.Before(word); context.Observe(engine.IdentifyLanguage(word)); context.After(word); }
        return context.Language;
    }
    public Task<AiDecision?> TestAsync(CancellationToken cancellation) => Api.DecideAsync(Keys.Key, settings.Value.TypeSafeModel, "ghbdsn", "", new() { ["keep"] = "ghbdsn", ["uk"] = "привіт", ["ru"] = "привыт" }, cancellation, test: true);
    public Task<AiDecision?> TestLocalAsync(CancellationToken cancellation) => Local.DecideAsync(settings.Value.LayaModelPath,
        "ghbdsn", "", new() { ["en"] = "ghbdsn", ["uk"] = "привіт", ["ru"] = "привыт" },
        new() { ["keep"] = "ghbdsn", ["uk"] = "привіт" }, cancellation, test: true);
    public void Dispose() { Api.Dispose(); Local.Dispose(); }
}
