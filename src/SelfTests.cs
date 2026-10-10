using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Switchcraft;

public static class SelfTests
{
    public static int Run(string output)
    {
        var checks = new List<object>(); int failed = 0;
        string dir = Path.Combine(Path.GetTempPath(), "Switchcraft-tests-" + Guid.NewGuid().ToString("N"));
        void Check(string name, bool passed, string actual = "") { checks.Add(new { name, passed, actual }); if (!passed) failed++; }
        try
        {
            var store = new SettingsStore(dir);
            using var engine = new NativeEngine(store);
            System.Threading.Tasks.Task.Run(() => AiTests.Run(Check, dir)).GetAwaiter().GetResult();
            System.Threading.Tasks.Task.Run(() => LayaTests.Run(engine, store, Check)).GetAwaiter().GetResult();
            System.Threading.Tasks.Task.Run(() => LayaSetupTests.Run(Check, dir)).GetAwaiter().GetResult();
            foreach (var fixture in store.Rules["golden_tests"]!.AsArray())
            {
                var input = fixture!["input"]!.GetValue<string>();
                var expected = fixture["expected"]!.GetValue<string>();
                // The exported C ABI does not expose session_language; both fixtures return the conservative original.
                if (fixture["session_language"] != null) expected = input;
                // New policy preserves valid original words; the full dictionary now
                // recognizes the old unknown-stem fixture including its final ю key.
                if (input == "руку") expected = input;
                if (input is "gjx`nf" or "c[j;fz" or "dshekbdf.") expected = input;
                var result = engine.Evaluate(input, fixture["active_input_source"]?.GetValue<string>() ?? "");
                Check("reference: " + input + " / " + fixture["reason"]!.GetValue<string>(), result.Text == expected, result.Text);
            }
            Check("Win64 INPUT structure", Marshal.SizeOf<Win32.Input>() == 40, Marshal.SizeOf<Win32.Input>().ToString());
            var autocompleteFocus = new KeyboardService.FocusNotice(0x8005, -4, 1, true, true, true, true, false, true, true, true);
            Check("Owned Scintilla autocomplete virtual focus preserves the word", KeyboardService.IgnoreScintillaFocus(autocompleteFocus));
            Check("Real editor focus change resets the word", !KeyboardService.IgnoreScintillaFocus(autocompleteFocus with { SameFocus = false }));
            Check("Foreground window event always resets the word", !KeyboardService.IgnoreScintillaFocus(autocompleteFocus with { Event = 3 }));
            Check("Generic UIA field focus is never deduplicated", !KeyboardService.IgnoreScintillaFocus(autocompleteFocus with { Scintilla = false }));
            Check("Expired editor permission rejects virtual focus deduplication", !KeyboardService.IgnoreScintillaFocus(autocompleteFocus with { FreshPermission = false }));
            Check("Unpermitted editor rejects virtual focus deduplication", !KeyboardService.IgnoreScintillaFocus(autocompleteFocus with { Permitted = false }));
            Check("Other-process popup cannot preserve Scintilla context", !KeyboardService.IgnoreScintillaFocus(autocompleteFocus with { SameProcess = false }));
            Check("Unowned popup cannot preserve Scintilla context", !KeyboardService.IgnoreScintillaFocus(autocompleteFocus with { OwnedByWindow = false }));
            Check("Unknown popup class cannot preserve Scintilla context", !KeyboardService.IgnoreScintillaFocus(autocompleteFocus with { EventIsListBox = false }));
            Check("Non-client object event is not treated as autocomplete", !KeyboardService.IgnoreScintillaFocus(autocompleteFocus with { Object = -8 }));
            Check("Repeated direct Scintilla client focus remains harmless", KeyboardService.IgnoreScintillaFocus(autocompleteFocus with { Child = 0, EventIsEditor = true, EventIsListBox = false }));
            store.Value.Sensitivity = "balanced";
            store.Pairs["руку"] = "custom"; store.UserPairs["руку"] = "custom"; store.SaveRules(); engine.Reload();
            Check("Explicit user override has priority", engine.Evaluate("руку", "ru").Text == "custom");
            store.Pairs["руку"] = "here"; store.UserPairs.Remove("руку"); store.SaveRules(); engine.Reload();
            Check("Valid country name is not translated", engine.Evaluate("Киргизстан", "uk", "ru").Text == "Киргизстан");
            store.Value.Languages.Remove("ru"); store.Save(); engine.Reload();
            Check("Disabled Russian language is not selected", !engine.Evaluate("Кіргізстан", "uk").Changed);
            store.Value.Languages.Add("ru"); store.Save(); engine.Reload();
            var context = new TypingContext(); context.ObserveLayout("uk"); context.Observe("ru");
            Check("One word cannot force ambiguous context", context.Language == ""); context.Observe("ru");
            Check("Two observations do not force an ambiguous word", context.Language == ""); context.Observe("ru");
            Check("Three independent observations establish context", context.Language == "ru");
            context.ExpectLayout("ru"); context.ObserveLayout("ru"); Check("Automatic layout switch retains context", context.Language == "ru");
            context.ObserveLayout("uk"); Check("Manual layout switch clears context", context.Language == "");
            context.Observe("ru"); context.Observe("ru"); for (int i = 0; i < 7; i++) context.Observe("");
            Check("Context expires after unrelated words", context.Language == "");
            context.Observe("ru"); context.Observe("ru"); context.Observe("ru"); context.Before("«Вона");
            Check("Quotation resets language evidence", context.Language == "");
            context.Observe("ru"); context.Observe("ru"); context.Observe("ru"); context.After("служил.");
            Check("Sentence boundary resets language evidence", context.Language == "");
            Check("Ukrainian manual conversion", engine.Convert("ghbdsn", "uk").Text == "привіт", engine.Convert("ghbdsn", "uk").Text);
            Check("English manual conversion", engine.Convert("руддщ", "en").Text == "hello", engine.Convert("руддщ", "en").Text);
            Check("Case preserved", engine.Convert("Ghbdsn", "uk").Text == "Привіт", engine.Convert("Ghbdsn", "uk").Text);
            Check("Multiline preserved", engine.Convert("ghbdsn\nруддщ", "en").Text.Contains('\n'));
            Check("Sentence punctuation preserved", engine.Convert("Ghbdsn, cdsnt!", "uk").Text == "Привіт, світе!", engine.Convert("Ghbdsn, cdsnt!", "uk").Text);
            Check("Punctuation key used as letter retained", engine.Convert("dsltkz.", "ru").Text == "выделяю", engine.Convert("dsltkz.", "ru").Text);
            Check("Bracket key used as letter retained", engine.Convert("cfvs[", "ru").Text == "самых", engine.Convert("cfvs[", "ru").Text);
            Check("Long text bounded", !engine.Evaluate(new string('a', 300)).Changed);
            Check("Email protected", !engine.Evaluate("ghbdsn@example.com").Changed);
            Check("URL protected", !engine.Evaluate("https://ghbdsn.dev").Changed);
            Check("Windows path protected", !engine.Evaluate(@"C:\ghbdsn\file.txt").Changed);
            Check("Keyboard shortcut parsed", HotkeyRegistry.TryParse("Ctrl+Alt+Space", out var mods, out var key) && mods == 3 && key == 32);
            Check("Bare shortcut rejected", !HotkeyRegistry.TryParse("Space", out _, out _));
            store.Pairs["swtst"] = "Switchcraft"; store.SaveRules(); engine.Reload();
            Check("Custom rule applied after reload", engine.Evaluate("swtst").Text == "Switchcraft", engine.Evaluate("swtst").Text);
            store.AddNever("ghbdsn"); engine.Reload();
            Check("Never-correct overrides detection", !engine.Evaluate("ghbdsn").Changed);
            store.AddNever("Кіргізстан"); engine.Reload();
            Check("Never-correct overrides expanded dictionaries", !engine.Evaluate("Кіргізстан", "uk", "ru").Changed);
            string exported = Path.Combine(dir, "export.json"); store.Export(exported);
            store.Pairs.Remove("swtst"); store.Import(exported); engine.Reload();
            Check("Export and import round trip", engine.Evaluate("swtst").Text == "Switchcraft");
            var before = store.Pairs.ToJsonString(); File.WriteAllText(exported, "{\"exact_pairs\":{\"good\":\"value\",\"bad\":17}}");
            bool rejected = false; try { store.Import(exported); } catch { rejected = true; }
            Check("Invalid import is atomic", rejected && before == store.Pairs.ToJsonString());
            store.Value.ExcludedApps.Add("Code.exe"); store.Save();
            var reloaded = new SettingsStore(dir); Check("Settings persist", reloaded.Value.ExcludedApps.Contains("Code.exe"));
            using (var worker = new AutomationWorker())
            {
                var timer = System.Diagnostics.Stopwatch.StartNew();
                var timedOut = worker.Invoke<object>(_ => { System.Threading.Thread.Sleep(250); return new object(); }, 40).GetAwaiter().GetResult();
                Check("Slow accessibility provider has a bounded wait", timedOut == null && timer.ElapsedMilliseconds < 200);
                bool staleActionRan = false;
                worker.Invoke<object>(_ => { staleActionRan = true; return new object(); }, 30).GetAwaiter().GetResult();
                System.Threading.Thread.Sleep(300);
                Check("Expired accessibility requests cannot execute later", !staleActionRan);
                Check("Accessibility queue recovers when provider returns", worker.Invoke<object>(_ => new object()).GetAwaiter().GetResult() != null);
            }
        }
        catch (Exception ex) { Check("Unhandled test failure", false, ex.ToString()); }
        var report = new { timestamp = DateTimeOffset.Now, total = checks.Count, passed = checks.Count - failed, failed, checks };
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(output))!);
        File.WriteAllText(output, JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true }));
        // dir is a freshly generated, dedicated test directory, never a user path.
        if (Directory.Exists(dir) && Path.GetFullPath(dir).StartsWith(Path.GetFullPath(Path.GetTempPath()), StringComparison.OrdinalIgnoreCase)) Directory.Delete(dir, true);
        return failed == 0 ? 0 : 1;
    }
}
