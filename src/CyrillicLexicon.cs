using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using WeCantSpell.Hunspell;

namespace Switchcraft;

/// <summary>Local word validation only: never guesses spelling or translates words.</summary>
internal sealed class CyrillicLexicon
{
    private static readonly Lazy<CyrillicLexicon> shared = new(() => new());
    public static CyrillicLexicon Shared => shared.Value;
    private readonly Dictionary<string, WordList> words = new();
    private static readonly Regex token = new("""\A[«“"(\[{]*(?<word>[А-Яа-яЁёІіЇїЄєҐґ]+(?:['’ʼ-][А-Яа-яЁёІіЇїЄєҐґ]+)*)[.,!?;:…»”")\]}]*\z""", RegexOptions.Compiled, TimeSpan.FromMilliseconds(30));
    private CyrillicLexicon()
    {
        foreach (var (language, file) in new[] { ("uk", "uk_UA"), ("ru", "ru_RU") })
            words[language] = WordList.CreateFromFiles(Path.Combine(AppContext.BaseDirectory, "Dictionaries", file + ".dic"));
    }
    public static string Core(string input)
    {
        var match = token.Match(input);
        return match.Success ? match.Groups["word"].Value : "";
    }
    internal bool Known(string word, string language)
    {
        if (word.Length is 0 or > 64 || !words.ContainsKey(language)) return false;
        word = word.Normalize(NormalizationForm.FormC).Replace('’', '\'').Replace('ʼ', '\'');
        return words[language].Check(word) || char.IsLower(word[0]) && words[language].Check(char.ToUpperInvariant(word[0]) + word[1..]);
    }
    public string Identify(string input)
    {
        var core = Core(input);
        bool uk = Known(core, "uk"), ru = Known(core, "ru");
        return uk == ru ? "" : uk ? "uk" : "ru";
    }
    public bool IsKnown(string input) { var core = Core(input); return Known(core, "uk") || Known(core, "ru"); }
    public Correction? Repair(string input, string context, bool careful = false)
    {
        var match = token.Match(input);
        if (!match.Success) return null;
        var group = match.Groups["word"]; string original = group.Value;
        if (original.Length is < 3 or > 64) return null;
        bool originalKnown = Known(original, "uk") || Known(original, "ru");
        var candidates = new List<(string Text, string Language)>();
        foreach (var language in new[] { "uk", "ru" })
        {
            var mapped = Map(original, language);
            if (mapped != original && Known(mapped, language)) candidates.Add((mapped, language));
        }
        // A real word is ambiguous, not wrong. Resolve it only with two recent
        // unambiguous words in the other language, and never rewrite a word valid there.
        if (originalKnown) candidates.RemoveAll(x => careful || x.Language != context || Known(original, context));
        if (candidates.Count != 1) return null;
        var candidate = candidates[0];
        return new(input, input[..group.Index] + candidate.Text + input[(group.Index + group.Length)..], true,
            originalKnown ? .97 : .99, candidate.Language, originalKnown ? "cyrillic_context" : "cyrillic_dictionary");
    }
    private static string Map(string input, string target) => new(input.Select(ch => target == "ru" ? ch switch
    {
        'і' => 'ы', 'І' => 'Ы', 'ї' => 'ъ', 'Ї' => 'Ъ', 'є' => 'э', 'Є' => 'Э', '\'' or '’' or 'ʼ' => 'ё', _ => ch
    } : ch switch
    {
        'ы' => 'і', 'Ы' => 'І', 'ъ' => 'ї', 'Ъ' => 'Ї', 'э' => 'є', 'Э' => 'Є', 'ё' or 'Ё' => '\'', _ => ch
    }).ToArray());
}
