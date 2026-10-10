using System.Diagnostics;
using System.IO;
using System.Security.Cryptography;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.RegularExpressions;
using Switchcraft;
using WeCantSpell.Hunspell;

// Offline diagnostics: this process never installs hooks, sends input, or reads user settings.
// Synthetic dictionary-derived samples diagnose coverage, not real-world typing accuracy.
if (args.Length != 1) throw new ArgumentException("Pass the report JSON path.");
var output = Path.GetFullPath(args[0]);
var data = AppContext.BaseDirectory;
var settingsPath = Path.Combine(Path.GetTempPath(), "Switchcraft-accuracy-" + Guid.NewGuid().ToString("N"));
var settings = new SettingsStore(settingsPath);
using var engine = new NativeEngine(settings);
var lexicons = new Dictionary<string, WordList>();
var samples = new Dictionary<string, string[]>();
const int seed = 20261010, sampleSize = 1500;
foreach (var (language, file) in new[] { ("uk", "uk_UA"), ("ru", "ru_RU") })
{
    var path = Path.Combine(data, "Dictionaries", file + ".dic");
    var lexicon = lexicons[language] = WordList.CreateFromFiles(path);
    var candidates = File.ReadLines(path).Skip(1)
        .Select(line => line.Split('/', '\t', ' ')[0])
        .Where(word => Regex.IsMatch(word, "^[А-Яа-яЁёІіЇїЄєҐґ]{3,24}$"))
        .Distinct(StringComparer.Ordinal).OrderBy(word => word, StringComparer.Ordinal)
        .Where(word => lexicon.Check(word)).ToArray();
    var rng = new Random(seed);
    rng.Shuffle(candidates);
    samples[language] = candidates.Take(sampleSize).ToArray();
}
var english = settings.Rules["languages"]!["en"]!["common_words"]!.AsArray()
    .Select(node => node!.GetValue<string>()).Where(word => Regex.IsMatch(word, "^[a-z]{3,24}$"))
    .Distinct(StringComparer.Ordinal).OrderBy(word => word, StringComparer.Ordinal).ToArray();
new Random(seed).Shuffle(english);
samples["en"] = english.Take(sampleSize).ToArray();
var groups = new List<object>();
var elapsed = new List<double>();
Correction Evaluate(string input, string source, string context)
{
    var timer = Stopwatch.StartNew();
    var result = engine.Evaluate(input, source, context);
    elapsed.Add(timer.Elapsed.TotalMilliseconds);
    return result;
}
void Measure(string name, IEnumerable<(string Input, string Expected)> items, string source, string context)
{
    int total = 0, correct = 0, unchanged = 0, incorrectChange = 0;
    var failures = new List<object>();
    foreach (var (input, expected) in items)
    {
        total++;
        var result = Evaluate(input, source, context);
        if (result.Text == expected) correct++;
        else
        {
            if (!result.Changed) unchanged++; else incorrectChange++;
            failures.Add(new { input, expected, actual = result.Text, result.Target, result.Reason, result.Confidence });
        }
    }
    groups.Add(new { name, source, context, total, correct, unchanged, incorrectChange, failures });
    Console.WriteLine($"{name}: correct {correct}/{total}; missed {unchanged}; wrong changes {incorrectChange}");
}
string MapCyrillic(string input, string target) => new(input.Select(ch => target == "ru" ? ch switch
{
    'і' => 'ы', 'І' => 'Ы', 'ї' => 'ъ', 'Ї' => 'Ъ', 'є' => 'э', 'Є' => 'Э', '\'' or '’' or 'ʼ' => 'ё', _ => ch
} : ch switch
{
    'ы' => 'і', 'Ы' => 'І', 'ъ' => 'ї', 'Ъ' => 'Ї', 'э' => 'є', 'Э' => 'Є', 'ё' or 'Ё' => '\'', _ => ch
}).ToArray());
bool IsKnown(string word) => lexicons.Values.Any(words => words.Check(word));
foreach (var language in new[] { "uk", "ru", "en" })
{
    var opposite = language == "uk" ? "ru" : "uk";
    var valid = samples[language].Select(word => (word, word)).ToArray();
    Measure($"valid_{language}_no_context", valid, language, "");
    Measure($"valid_{language}_matching_context", valid, language, language == "en" ? "" : language);
    Measure($"valid_{language}_opposite_context", valid, language, opposite);
    settings.Value.Sensitivity = "careful";
    Measure($"valid_{language}_opposite_context_careful", valid, language, opposite);
    settings.Value.Sensitivity = "balanced";
    if (language == "en") continue;
    var wrong = samples[language].Select(word => (Input: MapCyrillic(word, opposite), Expected: word))
        .Where(pair => pair.Input != pair.Expected).ToArray();
    foreach (var ambiguous in new[] { false, true })
    {
        var subset = wrong.Where(pair => IsKnown(pair.Input) == ambiguous).ToArray();
        var kind = ambiguous ? "ambiguous" : "unknown";
        Measure($"wrong_{opposite}_to_{language}_{kind}_no_context", subset, opposite, "");
        Measure($"wrong_{opposite}_to_{language}_{kind}_matching_context", subset, opposite, language);
    }
    // Generate English-key input from the shipped physical-key maps; omit unsupported letters.
    var map = new Dictionary<char, char>();
    foreach (var suffix in new[] { "", "_shift" })
        foreach (var item in settings.Rules["keyboard_layout"]!["maps"]![$"en_to_{language}{suffix}"]!.AsObject())
            map.TryAdd(item.Value!.GetValue<string>()[0], item.Key[0]);
    var latinInput = samples[language].Where(word => word.All(map.ContainsKey))
        .Select(word => (Input: new string(word.Select(ch => map[ch]).ToArray()), Expected: word)).ToArray();
    Measure($"wrong_en_to_{language}_no_context", latinInput, "en", "");
    Measure($"wrong_en_to_{language}_matching_context", latinInput, "en", language);
}
var curated = new List<object>();
foreach (var (input, expected, source, context) in new[]
{
    ("руку", "руку", "ru", "ru"),
    ("Кіргізстан", "Кыргызстан", "uk", "ru"),
    ("кіргізстан", "кыргызстан", "uk", "ru"),
    ("косматій", "косматій", "uk", ""),
    ("косматій", "косматый", "uk", "ru"),
    ("косматій", "косматій", "uk", "uk"),
    ("косматій", "косматый", "uk", "ru"), // Unicode decomposed й
    ("свыт", "світ", "ru", "uk"),
    ("украънський", "український", "ru", "uk"),
    ("ghbdtn", "привет", "en", "ru"),
    ("ghbdsn", "привіт", "en", "uk"),
    ("ghbdsn@example.com", "ghbdsn@example.com", "en", "uk"),
    ("https://ghbdsn.dev", "https://ghbdsn.dev", "en", "uk"),
    (@"C:\ghbdsn\file.txt", @"C:\ghbdsn\file.txt", "en", "uk")
})
{
    foreach (var sensitivity in new[] { "balanced", "careful" })
    {
        settings.Value.Sensitivity = sensitivity;
        var result = Evaluate(input, source, context);
        var policyExpected = sensitivity == "careful" && input is "косматій" or "косматій" ? input : expected;
        curated.Add(new { input, expected = policyExpected, source, context, sensitivity, actual = result.Text, passed = result.Text == policyExpected, result.Reason, result.Confidence });
    }
}
settings.Value.Sensitivity = "balanced";
var phrases = new List<object>();
foreach (var (input, expected) in new[]
{
    ("Когда я на почте служил ямщиком, ко мне постучался косматій\nЗеленоглазое такси притормози притормози Кіргізстан",
     "Когда я на почте служил ямщиком, ко мне постучался косматый\nЗеленоглазое такси притормози притормози Кыргызстан"),
    ("Он держал мою руку", "Он держал мою руку"),
    ("Вона стоїть у косматій шапці", "Вона стоїть у косматій шапці"),
    ("Когда служил, записал: «Вона у косматій шапці».", "Когда служил, записал: «Вона у косматій шапці»."),
    ("Когда служил. «Косматій» — українське слово.", "Когда служил. «Косматій» — українське слово."),
    ("Когда служил\nкосматій", "Когда служил\nкосматій")
})
{
    var actual = engine.CorrectText(input);
    phrases.Add(new { input, expected, actual, passed = actual == expected });
}
elapsed.Sort();
var hashes = new[] { "Runtime/rules.json", "Runtime/SwitchcraftNative.dll" }
    .Where(path => File.Exists(Path.Combine(data, path)))
    .ToDictionary(path => path, path => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(Path.Combine(data, path)))));
var report = new
{
    generatedUtc = DateTime.UtcNow, version = typeof(NativeEngine).Assembly.GetName().Version?.ToString(), seed, sampleSize,
    method = "Deterministic sampling from bundled dictionary roots (UK/RU) and native common words (EN). Validated Cyrillic entries, 3–24 letters; both capitals and lowercase. Synthetic wrong layouts use the shipped character mappings. No live keyboard input. Defaults in isolated temporary settings.",
    limitations = "Dictionary-derived diagnostic, NOT an independent or frequency-weighted estimate of real-world accuracy. Opposite context simulates stale language evidence/code switching; manual layout changes normally reset that evidence. Decomposed text and lowercase proper names are deliberate edge cases. CorrectText phrase tests exercise only the engine, not live hook layout resets.",
    hashes, sampleWords = samples, groups, curated, phrases,
    timing = new { calls = elapsed.Count, medianMs = elapsed[elapsed.Count / 2], p95Ms = elapsed[(int)(elapsed.Count * .95)], maxMs = elapsed[^1] }
};
Directory.CreateDirectory(Path.GetDirectoryName(output)!);
File.WriteAllText(output, JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true, Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping }));
Console.WriteLine($"Report: {output}");
// Delete only the unique settings directory created above; never user settings.
var resolvedSettings = Path.GetFullPath(settingsPath);
if (!string.Equals(Path.GetDirectoryName(resolvedSettings), Path.TrimEndingDirectorySeparator(Path.GetFullPath(Path.GetTempPath())), StringComparison.OrdinalIgnoreCase)
    || !Path.GetFileName(resolvedSettings).StartsWith("Switchcraft-accuracy-", StringComparison.Ordinal))
    throw new InvalidOperationException("Unexpected audit settings location; cleanup refused.");
Directory.Delete(resolvedSettings, recursive: true);
