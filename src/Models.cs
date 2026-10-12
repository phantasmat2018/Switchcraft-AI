using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Switchcraft;

public sealed class Preferences
{
    public bool Enabled { get; set; } = true;
    public bool LearningMode { get; set; }
    public bool SwitchLayout { get; set; } = true;
    public bool KeepHistory { get; set; } = true;
    public bool StartMinimized { get; set; }
    public string Sensitivity { get; set; } = "balanced";
    public string DetectionMode { get; set; } = "laya";
    public string TypeSafeModel { get; set; } = "jev-latest";
    public string LayaModelPath { get; set; } = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Switchcraft", "Laya", "model");
    public string LayaDevice { get; set; } = "auto";
    public int LayaCpuThreads { get; set; }
    public List<string> Languages { get; set; } = new() { "en", "uk", "ru" };
    public List<string> ExcludedApps { get; set; } = new();
    public Dictionary<string, string> Hotkeys { get; set; } = new()
    {
        ["convert"] = "Ctrl+Alt+Space", ["undo"] = "Ctrl+Alt+Backspace", ["pause"] = "Ctrl+Alt+P",
        ["convert_uk"] = "Ctrl+Alt+1", ["convert_en"] = "Ctrl+Alt+2", ["convert_ru"] = "Ctrl+Alt+3"
    };
}

public sealed class SettingsStore
{
    public string DirectoryPath { get; }
    public string RulesPath => Path.Combine(DirectoryPath, "rules.json");
    public string NativePreferencesPath => Path.Combine(DirectoryPath, "user_preferences.json");
    public string PreferencesPath => Path.Combine(DirectoryPath, "settings.json");
    public Preferences Value { get; private set; } = new();
    public JsonObject Rules { get; private set; }
    public string? LoadWarning { get; private set; }
    public SettingsStore(string? directory = null)
    {
        DirectoryPath = directory ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Switchcraft");
        Directory.CreateDirectory(DirectoryPath);
        if (File.Exists(PreferencesPath))
        {
            try { Value = JsonSerializer.Deserialize<Preferences>(File.ReadAllText(PreferencesPath)) ?? new(); }
            catch (JsonException) { BackupCorrupt(PreferencesPath); }
        }
        Normalize();
        if (!File.Exists(RulesPath)) File.Copy(Path.Combine(AppContext.BaseDirectory, "Runtime", "rules.json"), RulesPath);
        try { Rules = ReadRules(RulesPath); }
        catch (Exception ex) when (ex is JsonException or InvalidDataException)
        {
            BackupCorrupt(RulesPath);
            File.Copy(Path.Combine(AppContext.BaseDirectory, "Runtime", "rules.json"), RulesPath, true);
            Rules = ReadRules(RulesPath);
        }
        Save();
    }
    private static JsonObject ReadRules(string path)
    {
        var rules = JsonNode.Parse(File.ReadAllText(path))?.AsObject() ?? throw new InvalidDataException("Порожній файл правил.");
        if (rules["languages"] is not JsonObject || rules["keyboard_layout"] is not JsonObject || rules["manual_overrides"] is not JsonObject)
            throw new InvalidDataException("Некоректна структура правил.");
        return rules;
    }
    private void BackupCorrupt(string path)
    {
        File.Copy(path, path + ".invalid-" + DateTime.UtcNow.ToString("yyyyMMddHHmmssfff"), true);
        LoadWarning = "Пошкоджені налаштування збережено в резервну копію. Відновлено стандартні значення.";
    }
    private void Normalize()
    {
        if (Value.DetectionMode is not ("typesafe" or "laya")) Value.DetectionMode = "laya";
        if (string.IsNullOrWhiteSpace(Value.TypeSafeModel)) Value.TypeSafeModel = "jev-latest";
        if (string.IsNullOrWhiteSpace(Value.LayaModelPath)) Value.LayaModelPath = new Preferences().LayaModelPath;
        if (Value.LayaDevice is not ("auto" or "cpu" or "cuda")) Value.LayaDevice = "auto";
        Value.LayaCpuThreads = Math.Clamp(Value.LayaCpuThreads, 0, 8);
        // The order is the priority; the first active language is the primary one (English by default, but it can be
        // switched off). At least two languages stay active.
        Value.Languages = (Value.Languages ?? new()).Where(x => x is "en" or "uk" or "ru").Distinct().ToList();
        foreach (var fallback in new[] { "en", "uk", "ru" }) if (Value.Languages.Count < 2 && !Value.Languages.Contains(fallback)) Value.Languages.Add(fallback);
        Value.ExcludedApps ??= new();
        Value.Hotkeys ??= new Preferences().Hotkeys;
        foreach (var item in new Preferences().Hotkeys) Value.Hotkeys.TryAdd(item.Key, item.Value);
    }
    public void Save()
    {
        AtomicWrite(PreferencesPath, JsonSerializer.Serialize(Value, new JsonSerializerOptions { WriteIndented = true }));
        AtomicWrite(NativePreferencesPath, JsonSerializer.Serialize(new { active_languages = Value.Languages, ui_language = "uk" }));
    }
    public static void AtomicWrite(string path, string value)
    {
        var tmp = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        File.WriteAllText(tmp, value, new System.Text.UTF8Encoding(false));
        File.Move(tmp, path, true);
    }
    public JsonObject Pairs => Rules["manual_overrides"]!["exact_pairs"]!.AsObject();
    public JsonArray Never => Rules["manual_overrides"]!["never_correct"]!.AsArray();
    public JsonObject UserPairs
    {
        get
        {
            var overrides = Rules["manual_overrides"]!.AsObject();
            if (overrides["switchcraft_user_pairs"] is not JsonObject) overrides["switchcraft_user_pairs"] = new JsonObject();
            return overrides["switchcraft_user_pairs"]!.AsObject();
        }
    }
    private static JsonSerializerOptions JsonOptions => new(JsonSerializerOptions.Default) { WriteIndented = true };
    public void SaveRules() => AtomicWrite(RulesPath, Rules.ToJsonString(JsonOptions));
    public void AddNever(string word)
    {
        word = word.Trim().ToLowerInvariant();
        if (word.Length > 0 && !Never.Any(x => string.Equals(x?.GetValue<string>(), word, StringComparison.OrdinalIgnoreCase))) { Never.Add(word); SaveRules(); }
    }
    public void Import(string path)
    {
        var json = JsonNode.Parse(File.ReadAllText(path))?.AsObject() ?? throw new InvalidDataException("Некоректний JSON.");
        var pairs = json["exact_pairs"]?.AsObject(); var never = json["never_correct"]?.AsArray();
        if (pairs == null && never == null) throw new InvalidDataException("Файл має містити exact_pairs або never_correct.");
        // Validate the entire import before mutating live data.
        var validatedPairs = pairs?.Select(x => (x.Key.Trim(), Value: x.Value?.GetValue<string>()?.Trim())).ToArray();
        var validatedNever = never?.Select(x => x?.GetValue<string>()?.Trim()).ToArray();
        if (validatedPairs?.Any(x => string.IsNullOrEmpty(x.Item1) || string.IsNullOrEmpty(x.Value) || x.Item1.Length > 256 || x.Value.Length > 256) == true || validatedNever?.Any(x => string.IsNullOrEmpty(x) || x.Length > 256) == true)
            throw new InvalidDataException("Порожнє або надто довге правило (максимум 256 символів).");
        File.Copy(RulesPath, RulesPath + ".backup", true);
        var shipped = JsonNode.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Runtime", "rules.json")))!["manual_overrides"]!["exact_pairs"]!;
        if (validatedPairs != null) foreach (var pair in validatedPairs)
        {
            var key = pair.Item1.ToLowerInvariant(); Pairs[key] = pair.Value;
            if (json["switchcraft_user_pairs"] is JsonObject explicitPairs && explicitPairs.ContainsKey(key) || shipped[key]?.GetValue<string>() != pair.Value) UserPairs[key] = pair.Value;
        }
        if (validatedNever != null) foreach (var word in validatedNever) if (!Never.Any(x => string.Equals(x?.GetValue<string>(), word, StringComparison.OrdinalIgnoreCase))) Never.Add(word!.ToLowerInvariant());
        SaveRules();
    }
    public void Export(string path) => AtomicWrite(path, new JsonObject { ["exact_pairs"] = Pairs.DeepClone(), ["never_correct"] = Never.DeepClone(), ["switchcraft_user_pairs"] = UserPairs.DeepClone() }.ToJsonString(JsonOptions));
}

public sealed record Correction(string Original, string Text, bool Changed, double Confidence, string Target, string Reason);
public sealed record HistoryItem(DateTime Time, string Original, string Text, string Language, string App)
{
    public string TimeLabel => Time.ToString("HH:mm");
    public string LanguageLabel => Language.Length == 0 ? "УК / RU" : Language.ToUpperInvariant();
}
