using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;

namespace Switchcraft;

/// <summary>Native C ABI interop layer for Switchcraft layout engine.</summary>
public sealed class NativeEngine : IDisposable
{
    private const string Library = "SwitchcraftNative.dll";
    private readonly object gate = new();
    private IntPtr handle;
    private static IntPtr module;
    private static IntPtr searchCookie;
    private readonly SettingsStore settings;
    private readonly Dictionary<string, HashSet<string>> dictionaries;
    private readonly CyrillicLexicon cyrillic = CyrillicLexicon.Shared;
    private readonly Dictionary<string, Dictionary<char, char>> maps = new();
    private readonly Dictionary<string, string> shippedPairs;
    private Dictionary<string, string> pairs = new(), customPairs = new();
    private HashSet<string> never = new(), activeLanguages = new();
    public double LastElapsedMs { get; private set; }
    public int WordCount { get; }
    static NativeEngine()
    {
        var directory = Path.Combine(AppContext.BaseDirectory, "Runtime");
        searchCookie = AddDllDirectory(directory);
        module = LoadLibraryEx(Path.Combine(directory, Library), IntPtr.Zero, 0x00000100 | 0x00001000);
        if (module == IntPtr.Zero) throw new DllNotFoundException("Не вдалося завантажити мовне ядро. Збережіть теку Runtime поруч із Switchcraft.exe. Win32: " + Marshal.GetLastWin32Error());
        NativeLibrary.SetDllImportResolver(typeof(NativeEngine).Assembly, (name, _, _) => name == Library ? module : IntPtr.Zero);
    }
    public NativeEngine(SettingsStore settings)
    {
        this.settings = settings;
        dictionaries = settings.Rules["languages"]!.AsObject().ToDictionary(x => x.Key, x => x.Value!["common_words"]!.AsArray().Select(w => w!.GetValue<string>()).ToHashSet(StringComparer.OrdinalIgnoreCase));
        WordCount = settings.Rules["languages"]!.AsObject().Sum(x => x.Value!["common_words"]!.AsArray().Count);
        var shipped = System.Text.Json.Nodes.JsonNode.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Runtime", "rules.json")))!;
        shippedPairs = shipped["manual_overrides"]!["exact_pairs"]!.AsObject().ToDictionary(x => x.Key, x => x.Value!.GetValue<string>());
        foreach (var language in new[] { "uk", "ru" })
        {
            var map = maps[language] = new();
            foreach (var suffix in new[] { "", "_shift" })
                foreach (var item in settings.Rules["keyboard_layout"]!["maps"]![$"en_to_{language}{suffix}"]!.AsObject())
                    map[item.Key[0]] = item.Value!.GetValue<string>()[0];
        }
        Reload();
    }
    public void Reload()
    {
        var next = Create(settings.RulesPath, settings.NativePreferencesPath);
        if (next == IntPtr.Zero) throw new InvalidDataException("Мовне ядро не прийняло файл правил.");
        var nextPairs = settings.Pairs.ToDictionary(x => x.Key, x => x.Value!.GetValue<string>(), StringComparer.OrdinalIgnoreCase);
        var nextCustom = nextPairs.Where(x => settings.UserPairs.ContainsKey(x.Key) || !shippedPairs.TryGetValue(x.Key, out var value) || value != x.Value).ToDictionary(x => x.Key, x => x.Value, StringComparer.OrdinalIgnoreCase);
        var nextNever = settings.Never.Select(x => x!.GetValue<string>()).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var nextLanguages = settings.Value.Languages.ToHashSet();
        lock (gate)
        {
            var old = handle; handle = next; pairs = nextPairs; customPairs = nextCustom;
            never = nextNever; activeLanguages = nextLanguages;
            if (old != IntPtr.Zero) Destroy(old);
        }
    }
    public Correction Evaluate(string input, string source = "", string context = "")
    {
        var timer = Stopwatch.StartNew();
        try { return EvaluateCore(input, source, context); }
        finally { LastElapsedMs = timer.Elapsed.TotalMilliseconds; }
    }
    private Correction EvaluateCore(string input, string source, string context)
    {
        if (string.IsNullOrEmpty(input) || input.Length > 256) return new(input, input, false, 0, "", "length_limit");
        // Explicit protections also cover prefixes that are still being typed.
        if (Regex.IsMatch(input, @"(@|://|www\.|\\|^[A-Za-z]:|^[\w.-]+\.(com|org|net|dev|io|ua)(/|$))", RegexOptions.IgnoreCase, TimeSpan.FromMilliseconds(30)))
            return new(input, input, false, 0, "", "protected_pattern");
        var sw = Stopwatch.StartNew();
        lock (gate)
        {
            if (handle == IntPtr.Zero) return new(input, input, false, 0, "", "engine_stopped");
            var normalized = input.Normalize(NormalizationForm.FormC);
            var core = normalized.Trim('«', '»', '“', '”', '"', '(', ')', '[', ']', '.', ',', '!', '?', ':', ';', '…');
            bool careful = settings.Value.Sensitivity == "careful";
            if (never.Contains(normalized) || never.Contains(core)) return new(input, input, false, 0, "", "user_exception");
            if (customPairs.TryGetValue(normalized, out var explicitText) || customPairs.TryGetValue(core, out explicitText))
            {
                var value = normalized.Equals(core, StringComparison.Ordinal) ? explicitText : normalized.Replace(core, explicitText, StringComparison.Ordinal);
                return new(input, value, value != input, 1, InferLanguage(explicitText), "user_pair");
            }
            if (activeLanguages.Contains("uk") && activeLanguages.Contains("ru"))
            {
                var repair = cyrillic.Repair(normalized, context, careful);
                if (repair != null) { LastElapsedMs = sw.Elapsed.TotalMilliseconds; return repair; }
            }
            // Built-in examples are hints, never permission to overwrite an existing word.
            if (cyrillic.IsKnown(normalized) || dictionaries["en"].Contains(core))
                return new(input, input, false, 0, IdentifyLanguage(normalized), "known_original");
            if (Regex.IsMatch(normalized, "[A-Za-z]"))
            {
                var candidates = new List<(string Text, string Language)>();
                foreach (var language in new[] { "uk", "ru" }.Where(activeLanguages.Contains))
                {
                    var mapped = new string(normalized.Select(ch => maps[language].GetValueOrDefault(ch, ch)).ToArray());
                    if (cyrillic.Known(CyrillicLexicon.Core(mapped), language)) candidates.Add((mapped, language));
                    else
                    {
                        // A final comma may be punctuation or the key for б. Prefer a
                        // complete dictionary word, then try preserving its punctuation.
                        for (int count = 1; count < normalized.Length && ".,!?;:…)]}\"»”.".Contains(normalized[^count]); count++)
                        {
                            var stem = mapped[..^count];
                            if (cyrillic.Known(CyrillicLexicon.Core(stem), language)) { candidates.Add((stem + normalized[^count..], language)); break; }
                        }
                    }
                }
                var distinct = candidates.Select(x => x.Text).Distinct().ToArray();
                var contextual = candidates.Where(x => x.Language == context).ToArray();
                if (core.Length >= 3 && (distinct.Length == 1 || !careful && contextual.Length == 1))
                {
                    var chosen = contextual.FirstOrDefault();
                    if (chosen.Text == null) chosen = candidates.First();
                    var identified = IdentifyLanguage(chosen.Text);
                    return new(input, chosen.Text, chosen.Text != input, 0, identified.Length > 0 ? identified : chosen.Language, "layout_dictionary");
                }
                if (distinct.Length > 1) return new(input, input, false, 0, "", "ambiguous_dictionary_candidates");
            }
            var target = new byte[64]; var reason = new byte[2048];
            var ptr = EvaluateNative(handle, input, source, out int changed, out double confidence, target, target.Length, reason, reason.Length);
            try
            {
                var result = Marshal.PtrToStringUTF8(ptr) ?? "";
                var language = Utf8(target);
                if (string.IsNullOrWhiteSpace(language) && result.Length > 0)
                {
                    // The C ABI omits the destination for exact seed pairs. Read its
                    // metadata instead of choosing the first configured Cyrillic layout.
                    var pairKey = input.TrimEnd('.', ',', '!', '?', ';', ':').ToLowerInvariant();
                    var identified = InferLanguage(result);
                    language = identified.Length > 0 ? identified : context;
                }
                var accept = changed != 0 && result.Length > 0 && result != input;
                var resultCore = result.Trim('.', ',', '!', '?', ';', ':', '"', '«', '»', '(', ')');
                bool verified = language is "uk" or "ru" ? cyrillic.Known(CyrillicLexicon.Core(result), language) : language == "en" && dictionaries["en"].Contains(resultCore);
                if (!verified || !activeLanguages.Contains(language)) accept = false;
                if (careful && (confidence < .95 || core.Length < 3)) accept = false;
                return new(input, accept ? result : input, accept, confidence, language, Utf8(reason));
            }
            finally { if (ptr != IntPtr.Zero) Free(ptr); LastElapsedMs = sw.Elapsed.TotalMilliseconds; }
        }
    }
    public (string Text, string Target) Convert(string text, string? target = null, string source = "")
    {
        if (text.Length > 100_000) throw new InvalidOperationException("За один раз можна перетворити до 100 000 символів.");
        lock (gate)
        {
            ObjectDisposedException.ThrowIf(handle == IntPtr.Zero, this);
            var language = new byte[64];
            var ptr = target == null ? ConvertNative(handle, text, source, language, language.Length) : ConvertToNative(handle, text, target, language, language.Length);
            try
            {
                var result = Marshal.PtrToStringUTF8(ptr) ?? text;
                var destination = target ?? Utf8(language);
                return (PreserveWordPunctuation(text, result, destination), destination);
            }
            finally { if (ptr != IntPtr.Zero) Free(ptr); }
        }
    }
    private string PreserveWordPunctuation(string original, string converted, string language)
    {
        // The reference selection API maps every comma to a letter. Preserve a trailing
        // punctuation suffix when the mapped stem is a known word, but keep punctuation
        // keys that form actual letters (e.g. dsltkz. -> выделяю, cfvs[ -> самых).
        var words = dictionaries.GetValueOrDefault(language);
        if (words == null) return converted;
        bool Known(string value) => language is "uk" or "ru" ? cyrillic.Known(CyrillicLexicon.Core(value), language) : words.Contains(value);
        var originals = Regex.Matches(original, @"\S+"); int index = 0;
        const string punctuation = ".,;:!?[]{}<>\"«»…";
        return Regex.Replace(converted, @"\S+", match =>
        {
            if (index >= originals.Count) return match.Value;
            var source = originals[index++].Value; var value = match.Value;
            if (source.Length != value.Length || Known(value.Trim(punctuation.ToCharArray()))) return value;
            for (int count = 1; count < source.Length; count++)
            {
                if (!punctuation.Contains(source[^count])) break;
                if (Known(value[..^count])) return value[..^count] + source[^count..];
            }
            return value;
        });
    }
    public string CorrectText(string text)
    {
        var context = new TypingContext(); int end = 0;
        return Regex.Replace(text, @"\S+", match =>
        {
            if (text.AsSpan(end, match.Index - end).Contains('\n')) context.Reset();
            context.Before(match.Value);
            var result = Evaluate(match.Value, context: context.Language);
            context.Observe(result.Changed ? "" : IdentifyLanguage(result.Text));
            context.After(match.Value);
            end = match.Index + match.Length;
            return result.Text;
        }, RegexOptions.None, TimeSpan.FromSeconds(5));
    }
    public string IdentifyLanguage(string text) => cyrillic.Identify(text);
    public string[] KnownLanguages(string text)
    {
        var normalized = text.Normalize(NormalizationForm.FormC);
        var core = normalized.Trim('«', '»', '“', '”', '"', '(', ')', '[', ']', '{', '}', '.', ',', '!', '?', ':', ';', '…');
        var languages = new List<string>();
        lock (gate)
        {
            if (dictionaries["en"].Contains(core)) languages.Add("en");
            foreach (var language in new[] { "uk", "ru" })
                if (cyrillic.Known(CyrillicLexicon.Core(normalized), language)) languages.Add(language);
        }
        return languages.ToArray();
    }
    public string InferLanguage(string text)
    {
        if (Regex.IsMatch(text, "[A-Za-z]")) return "en";
        var identified = IdentifyLanguage(text);
        if (identified.Length > 0) return identified;
        if (Regex.IsMatch(text, "[іїєґІЇЄҐ]")) return "uk";
        if (Regex.IsMatch(text, "[ыэёъЫЭЁЪ]")) return "ru";
        return "";
    }
    private static string Utf8(byte[] bytes) => Encoding.UTF8.GetString(bytes, 0, Array.IndexOf(bytes, (byte)0) is var end && end >= 0 ? end : bytes.Length);
    public void Dispose() { lock (gate) { if (handle != IntPtr.Zero) Destroy(handle); handle = IntPtr.Zero; } }
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern IntPtr AddDllDirectory(string directory);
    [DllImport("kernel32.dll", EntryPoint = "LoadLibraryExW", CharSet = CharSet.Unicode, SetLastError = true)] private static extern IntPtr LoadLibraryEx(string path, IntPtr file, uint flags);
    [DllImport(Library, EntryPoint = "ts_engine_create", CallingConvention = CallingConvention.Cdecl)] private static extern IntPtr Create([MarshalAs(UnmanagedType.LPUTF8Str)] string rulesPath, [MarshalAs(UnmanagedType.LPUTF8Str)] string preferencesPath);
    [DllImport(Library, EntryPoint = "ts_engine_evaluate", CallingConvention = CallingConvention.Cdecl)] private static extern IntPtr EvaluateNative(IntPtr engine, [MarshalAs(UnmanagedType.LPUTF8Str)] string token, [MarshalAs(UnmanagedType.LPUTF8Str)] string source, out int changed, out double confidence, [Out] byte[] target, int targetSize, [Out] byte[] reason, int reasonSize);
    [DllImport(Library, EntryPoint = "ts_engine_convert_selection", CallingConvention = CallingConvention.Cdecl)] private static extern IntPtr ConvertNative(IntPtr engine, [MarshalAs(UnmanagedType.LPUTF8Str)] string text, [MarshalAs(UnmanagedType.LPUTF8Str)] string source, [Out] byte[] target, int targetSize);
    [DllImport(Library, EntryPoint = "ts_engine_convert_selection_to", CallingConvention = CallingConvention.Cdecl)] private static extern IntPtr ConvertToNative(IntPtr engine, [MarshalAs(UnmanagedType.LPUTF8Str)] string text, [MarshalAs(UnmanagedType.LPUTF8Str)] string targetLanguage, [Out] byte[] target, int targetSize);
    [DllImport(Library, EntryPoint = "ts_free_string", CallingConvention = CallingConvention.Cdecl)] private static extern void Free(IntPtr value);
    [DllImport(Library, EntryPoint = "ts_engine_destroy", CallingConvention = CallingConvention.Cdecl)] private static extern void Destroy(IntPtr engine);
}
