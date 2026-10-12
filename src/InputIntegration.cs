using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;

namespace Switchcraft;

/// <summary>Opt-in integration harness. Exercises the production input policy with
/// untagged external SendInput events; only the exclusion of our own test window is lifted.</summary>
internal static class InputIntegration
{
    [System.Runtime.InteropServices.DllImport("user32.dll")] private static extern bool OpenClipboard(IntPtr owner);
    [System.Runtime.InteropServices.DllImport("user32.dll")] private static extern bool CloseClipboard();
    internal static async void Run(string output, bool aiOnly = false, bool liveShort = false, bool contextOnly = false, bool layaShort = false)
    {
        string dir = Path.Combine(Path.GetTempPath(), "Switchcraft-input-tests-" + Guid.NewGuid().ToString("N"));
        var checks = new List<object>(); var messages = new List<string>(); int failed = 0;
        void Check(string name, bool passed, string actual) { checks.Add(new { name, passed, actual }); if (!passed) failed++; }
        var field = new TextBox { AcceptsReturn = true, Height = 160, Margin = new Thickness(20), FontSize = 22 };
        var password = new PasswordBox { Height = 40, Margin = new Thickness(20) };
        var panel = new StackPanel(); panel.Children.Add(new TextBlock { Text = "Switchcraft · ізольована перевірка вводу", Margin = new Thickness(20), FontSize = 18 }); panel.Children.Add(field); panel.Children.Add(password);
        var window = new Window { Title = "Switchcraft — тест вводу", Width = 680, Height = 370, Content = panel, WindowStartupLocation = WindowStartupLocation.CenterScreen, Background = System.Windows.Media.Brushes.DarkSlateGray };
        SettingsStore? store = null; NativeEngine? engine = null; KeyboardService? keyboard = null; LanguageDetection? detection = null;
        try
        {
            store = new SettingsStore(dir); store.Value.SwitchLayout = false;
            // The core input checks run on the shared dictionary engine, exactly as in the full version;
            // the TypeSafe and Laya paths below switch modes explicitly.
            store.Value.DetectionMode = "local";
            engine = new NativeEngine(store);
            var aiHandler = new AiTests.FakeHandler();
            detection = new LanguageDetection(engine, store, new TypeSafeDetector(liveShort ? null : aiHandler));
            keyboard = new KeyboardService(engine, store, allowOwnProcess: true, detection: detection);
            keyboard.Status += x => { lock (messages) messages.Add(x); };
            window.Show(); window.Activate(); field.Focus();
            var hwnd = new WindowInteropHelper(window).Handle;
            // Windows can deny foreground activation for a process started in the
            // background. Never send test keystrokes into the user's current app.
            for (int i = 0; i < 120 && Win32.GetForegroundWindow() != hwnd; i++) await Task.Delay(250);
            void RequireTestFocus()
            {
                if (Win32.GetForegroundWindow() != hwnd) throw new InvalidOperationException("Activate the isolated input-test window; tests never type into another app.");
            }
            RequireTestFocus(); field.Focus();
            Win32.SwitchLayout(hwnd, "en"); keyboard.Start(); await Task.Delay(1000);
            async Task Type(string text, int settle = 350)
            {
                foreach (char ch in text)
                {
                    RequireTestFocus();
                    ushort vk = ch switch { ' ' => 32, '\n' => 13, ';' => 0xBA, ',' => 0xBC, '.' => 0xBE, '\'' => 0xDE, _ => (ushort)char.ToUpperInvariant(ch) };
                    var down = Win32.Key(vk); down.Data.Keyboard.Extra = UIntPtr.Zero;
                    var up = Win32.Key(vk, true); up.Data.Keyboard.Extra = UIntPtr.Zero;
                    Win32.Send(new[] { down, up }); await Task.Delay(30);
                }
                await Task.Delay(settle);
            }
            async Task Reset()
            {
                RequireTestFocus();
                keyboard.Resume(); keyboard.Reset(); field.Clear(); field.Focus(); Win32.SwitchLayout(hwnd, "en"); await Task.Delay(300);
            }
            async Task TypeUnicode(string text)
            {
                foreach (char ch in text)
                {
                    RequireTestFocus();
                    var down = Win32.Unicode(ch); down.Data.Keyboard.Extra = UIntPtr.Zero;
                    var up = Win32.Unicode(ch, true); up.Data.Keyboard.Extra = UIntPtr.Zero;
                    Win32.Send(new[] { down, up }); await Task.Delay(30);
                }
                await Task.Delay(350);
            }
            if (contextOnly)
            {
                detection.Keys.Save("synthetic-context-key"); store.Value.DetectionMode = "typesafe";
                aiHandler.Choice = "keep"; aiHandler.Confidence = .45; aiHandler.Probability = .6;
                async Task ResetContext()
                {
                    await Reset(); detection.Api.Reset(); Win32.SwitchLayout(hwnd, "ru"); await Task.Delay(200);
                }
                await ResetContext(); await TypeUnicode("Былий і чорний"); await Task.Delay(1200);
                Check("Whole-word retry after idle without final space", field.Text == "Білий і чорний", field.Text);
                string? learned = null; keyboard.LearnWord += value => learned = value; store.Value.LearningMode = true;
                await keyboard.UndoAsync(); await Task.Delay(200);
                Check("Idle undo adds no trailing space", field.Text == "Былий і чорний", field.Text);
                Check("Idle undo learns only the corrected word", learned == "Былий", learned ?? "none");
                store.Value.LearningMode = false;
                await ResetContext(); await TypeUnicode("Былий і чорний "); await Task.Delay(300);
                Check("Whole-word retry after two following words", field.Text == "Білий і чорний ", field.Text);
                await ResetContext(); await TypeUnicode("Свыт дуже гарний "); await Task.Delay(300);
                Check("Retry also handles other dictionary corrections", field.Text == "Світ дуже гарний ", field.Text);
                await ResetContext(); await TypeUnicode("Червоний ы зелений"); await Task.Delay(1200);
                Check("Previous single-letter example works without final space", field.Text == "Червоний і зелений", field.Text);
                await ResetContext(); await TypeUnicode("Білий і чорний"); await Task.Delay(1200);
                Check("Correct Ukrainian remains unchanged", field.Text == "Білий і чорний", field.Text);
                await ResetContext(); await TypeUnicode("Белый и чёрный"); await Task.Delay(1200);
                Check("Correct Russian remains unchanged", field.Text == "Белый и чёрный", field.Text);
                await ResetContext(); await TypeUnicode("Былий і чо"); await Task.Delay(1200);
                Check("Idle retry leaves the unfinished word untouched", field.Text == "Білий і чо", field.Text);
                await TypeUnicode("рний "); await Task.Delay(300);
                Check("Typing continues correctly after idle replacement", field.Text == "Білий і чорний ", field.Text);
                await ResetContext(); aiHandler.DelayMs = 850;
                await TypeUnicode("Былий і чорний "); field.CaretIndex = 0; await Task.Delay(1300);
                Check("Caret movement rejects whole-word retry", field.Text == "Былий і чорний ", field.Text);
                await ResetContext(); await TypeUnicode("Былий і чорний");
                field.Text = "Збережений текст"; field.CaretIndex = field.Text.Length; await Task.Delay(1800);
                Check("Changed suffix rejects idle retry", field.Text == "Збережений текст", field.Text);
                await ResetContext(); await TypeUnicode("Былий і чорний");
                store.Value.DetectionMode = "local"; keyboard.Reset(); detection.Api.Reset(); await Task.Delay(1700);
                Check("Mode off cancels idle retry", field.Text == "Былий і чорний", field.Text);
                store.Value.DetectionMode = "typesafe"; aiHandler.DelayMs = 0;
                await ResetContext(); await TypeUnicode("Былий і дуже гарний чорний "); await Task.Delay(1200);
                Check("A word interrupted by fast typing is still corrected after four following words", field.Text == "Білий і дуже гарний чорний ", field.Text);
                await ResetContext(); await TypeUnicode("Былий і дуже гарний чорний кіт спить на теплому вікні "); await Task.Delay(1500);
                Check("Retry does not reach beyond eight following words", field.Text.StartsWith("Былий "), field.Text);
                aiHandler.Bodies.Clear(); await ResetContext(); await TypeUnicode("Былий свыт чорний "); await Task.Delay(700);
                Check("Two completed errors are both corrected after continuous typing", field.Text == "Білий світ чорний ", field.Text);
                string? LastPreceding(string word) => aiHandler.Bodies.Select(body =>
                {
                    using var request = JsonDocument.Parse(body); var state = request.RootElement.GetProperty("state");
                    return (Word: state.GetProperty("word").GetString(), Preceding: state.GetProperty("preceding_words").GetString());
                }).LastOrDefault(x => x.Word == word).Preceding;
                Check("A later word of the batch is judged after the corrected earlier word", LastPreceding("свыт") == "Білий", LastPreceding("свыт") ?? "no request");
                await keyboard.UndoAsync(); await Task.Delay(200);
                Check("Undo after a correction batch restores only its last changed word", field.Text == "Білий свыт чорний ", field.Text);
                await ResetContext(); await TypeUnicode("Былий свыт чорний"); await Task.Delay(1400);
                Check("Idle retries every completed error while preserving the unfinished word", field.Text == "Білий світ чорний", field.Text);
                await Reset(); detection.Api.Reset(); aiHandler.Choice = "uk"; aiHandler.Confidence = .98; aiHandler.Probability = .98;
                await TypeUnicode("cbh смачний "); await Task.Delay(500);
                Check("Ambiguous keyboard word receives following context", field.Text == "сир смачний ", field.Text);
                await Reset(); detection.Api.Reset(); aiHandler.Choice = "keep";
                await TypeUnicode("cbh "); await Task.Delay(200);
                string keptWithoutContext = field.Text;
                aiHandler.Choice = "uk"; await TypeUnicode("смачний "); await Task.Delay(500);
                Check("Unknown word kept initially is reconsidered with new following context",
                    keptWithoutContext == "cbh " && field.Text == "сир смачний ", keptWithoutContext + " -> " + field.Text);
                await Reset(); detection.Api.Reset(); aiHandler.Choice = "uk";
                // Keep A confidently while independently accepting B. This tests
                // suffix rebasing without relying on uncertain fallback policy.
                aiHandler.WordAnswers["cbh"] = ("keep", .98, .98);
                await TypeUnicode("cbh "); await Task.Delay(200);
                await TypeUnicode("свыт "); await Task.Delay(500);
                string afterLaterRepair = field.Text;
                aiHandler.WordAnswers.TryRemove("cbh", out _);
                await TypeUnicode("смачний "); await Task.Delay(500);
                Check("Older pending suffix tracks a later word repair before reconsideration",
                    afterLaterRepair == "cbh світ " && field.Text == "сир світ смачний ", afterLaterRepair + " -> " + field.Text);
                aiHandler.Choice = "keep"; aiHandler.Confidence = .45; aiHandler.Probability = .6;
                await Reset(); detection.Api.Reset(); await TypeUnicode("ghbdtn cke;bk hello"); await Task.Delay(1400);
                Check("Wrong-layout punctuation keys do not split the pending sentence", field.Text == "привет служил hello", field.Text);
                await ResetContext(); await TypeUnicode("Червоний. ы "); await Task.Delay(500);
                Check("AI evidence does not leak across a sentence boundary", field.Text == "Червоний. ы ", field.Text);
                await ResetContext(); await TypeUnicode("Червоний «hello» ы "); await Task.Delay(500);
                Check("AI evidence does not leak through a language quotation", field.Text == "Червоний «hello» ы ", field.Text);
                await ResetContext(); aiHandler.DelayMs = 850;
                await TypeUnicode("Былий і чорний "); password.Clear(); password.Focus(); await Task.Delay(1500);
                Check("Focus change rejects a delayed correction", field.Text == "Былий і чорний " && password.Password.Length == 0,
                    field.Text + " / password field unchanged");
                aiHandler.DelayMs = 0;
                if (Win32.LayoutFor("en") != IntPtr.Zero && Win32.LayoutFor("uk") != IntPtr.Zero && Win32.LayoutFor("ru") != IntPtr.Zero)
                {
                    store.Value.SwitchLayout = true;
                    await Reset(); detection.Api.Reset(); await TypeUnicode("ghbdsn hel"); await Task.Delay(1200);
                    Check("Deferred Ukrainian repair preserves current English layout",
                        field.Text == "привіт hel" && Win32.LayoutLanguage(hwnd) == "en", Win32.LayoutLanguage(hwnd) + ": " + field.Text);
                    await Type("lo ");
                    Check("Physical English typing continues after deferred repair", field.Text == "привіт hello ", field.Text);
                    await ResetContext(); await TypeUnicode("Былий белый"); await Task.Delay(1200);
                    Check("Deferred Ukrainian repair preserves following Russian layout",
                        field.Text == "Білий белый" && Win32.LayoutLanguage(hwnd) == "ru", Win32.LayoutLanguage(hwnd) + ": " + field.Text);
                    await Reset(); detection.Api.Reset(); Win32.SwitchLayout(hwnd, "uk"); await Task.Delay(200);
                    await TypeUnicode("Белій чорний"); await Task.Delay(1200);
                    Check("Deferred Russian repair preserves following Ukrainian layout",
                        field.Text == "Белый чорний" && Win32.LayoutLanguage(hwnd) == "uk", Win32.LayoutLanguage(hwnd) + ": " + field.Text);
                    await ResetContext(); await TypeUnicode("Былий "); await Task.Delay(300);
                    Check("Immediate correction still switches layout",
                        field.Text == "Білий " && Win32.LayoutLanguage(hwnd) == "uk", Win32.LayoutLanguage(hwnd) + ": " + field.Text);
                    store.Value.SwitchLayout = false;
                }
                store.AddNever("Былий"); engine.Reload();
                await ResetContext(); await TypeUnicode("Былий і чорний"); await Task.Delay(1200);
                Check("User exception bypasses deferred correction", field.Text == "Былий і чорний", field.Text);
            }
            else if (liveShort || layaShort)
            {
                string provider = layaShort ? "Local Laya" : "Live TypeSafe";
                if (layaShort)
                {
                    store.Value.DetectionMode = "laya";
                    var warmup = await detection.TestLocalAsync(System.Threading.CancellationToken.None);
                    Check("Local Laya model loaded", warmup != null, detection.Local.Status);
                    if (warmup == null) throw new InvalidOperationException("Local synthetic test requires a ready Laya model.");
                }
                else
                {
                    var savedKeys = new ApiKeyStore(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Switchcraft"));
                    if (!savedKeys.HasKey) throw new InvalidOperationException("Live synthetic test requires a saved API key.");
                    detection.Keys.Save(savedKeys.Key); store.Value.DetectionMode = "typesafe";
                }
                keyboard.Corrected += (correction, _) => { lock (messages) messages.Add(correction.Original + " -> " + correction.Text + " / " + correction.Reason); };
                await Reset(); Win32.SwitchLayout(hwnd, "ru"); await Task.Delay(200);
                await TypeUnicode("Червоний ы зелений "); await Task.Delay(1700);
                Check(provider + ": user's continuous phrase", field.Text == "Червоний і зелений ", field.Text);
                lock (messages) messages.Add(detection.Status);
                await keyboard.UndoAsync(); await Task.Delay(200);
                Check(provider + ": undo preserves the whole phrase", field.Text == "Червоний ы зелений ", field.Text);
                await Reset(); Win32.SwitchLayout(hwnd, "ru"); await Task.Delay(200);
                await TypeUnicode("Українська буква ы "); await Task.Delay(1700);
                Check(provider + ": literal letter stays unchanged", field.Text == "Українська буква ы ", field.Text);
                await Reset(); Win32.SwitchLayout(hwnd, "ru"); await Task.Delay(200);
                await TypeUnicode("Былий і чорний"); await Task.Delay(2000);
                Check(provider + ": whole word corrected after idle", field.Text == "Білий і чорний", field.Text);
                lock (messages) messages.Add(detection.Status);
                await keyboard.UndoAsync(); await Task.Delay(200);
                Check(provider + ": whole-word undo without extra space", field.Text == "Былий і чорний", field.Text);
                foreach (string source in new[] { "uk", "ru", "en" })
                {
                    await Reset(); Win32.SwitchLayout(hwnd, source); await Task.Delay(200);
                    await TypeUnicode("ой чий то кінь стоъть "); await Task.Delay(1800);
                    Check(provider + ": horse phrase with " + source + " layout", field.Text == "ой чий то кінь стоїть ", field.Text);
                }
                if (layaShort) Check("Local Laya never calls cloud API", aiHandler.Calls == 0, aiHandler.Calls.ToString());
            }
            else
            {
            if (!aiOnly)
            {
            await Type("ghbdsn "); Check("Actual hook auto-correction", field.Text == "привіт ", field.Text);
            await keyboard.UndoAsync(); await Task.Delay(200); Check("Undo restores word and boundary", field.Text == "ghbdsn ", field.Text);
            await Reset(); await Type("ghbdtn "); Check("Russian correction from external virtual keys", field.Text == "привет ", field.Text);
            await Reset();
            foreach (char ch in "ghbdtn ")
            {
                RequireTestFocus();
                var down = Win32.Unicode(ch); down.Data.Keyboard.Extra = new UIntPtr(0x1234);
                var up = Win32.Unicode(ch, true); up.Data.Keyboard.Extra = new UIntPtr(0x1234);
                Win32.Send(new[] { down, up }); await Task.Delay(30);
            }
            await Task.Delay(350); Check("Russian correction from external Unicode packets", field.Text == "привет ", field.Text);
            await Reset();
            foreach (char ch in "ghbdtn") { RequireTestFocus(); Win32.Send(new[] { Win32.Unicode(ch), Win32.Unicode(ch, true) }); }
            await Task.Delay(100); await Type(" ");
            Check("Own tagged replacements are never reprocessed", field.Text == "ghbdtn ", field.Text);
            await Reset(); await Type("hello "); Check("Valid English remains unchanged", field.Text == "hello ", field.Text);
            await Reset(); await TypeUnicode("Он держал мою руку ");
            Check("Valid Russian is not changed by a legacy seed", field.Text == "Он держал мою руку ", field.Text);
            await Reset(); await Type("ghbdsn "); var correctedBeforeMove = field.Text;
            field.CaretIndex = 0; await keyboard.UndoAsync();
            Check("Programmatic caret move invalidates undo in same HWND", field.Text == correctedBeforeMove, field.Text);
            await Reset(); await Type("ghbdsn "); field.Text = "важливий текст "; field.CaretIndex = field.Text.Length;
            await keyboard.UndoAsync();
            Check("Programmatic text change invalidates undo", field.Text == "важливий текст ", field.Text);
            await Reset();
            const string burst = "ghbdtn hello ghbdsn hello ";
            var burstInputs = burst.SelectMany(ch => new[] { Win32.Key((ushort)char.ToUpperInvariant(ch)), Win32.Key((ushort)char.ToUpperInvariant(ch), true) }).ToArray();
            for (int i = 0; i < burstInputs.Length; i++) burstInputs[i].Data.Keyboard.Extra = UIntPtr.Zero;
            RequireTestFocus(); Win32.Send(burstInputs); await Task.Delay(600);
            Check("Rapid burst cancels stale replacements without losing text", field.Text == burst, field.Text);
            await Reset(); keyboard.Pause(); await Type("ghbdsn "); Check("Paused hook leaves input unchanged", field.Text == "ghbdsn ", field.Text); keyboard.Resume();
            await Reset(); store.Value.ExcludedApps.Add("Switchcraft.exe"); await Type("ghbdsn "); Check("Excluded app leaves input unchanged", field.Text == "ghbdsn ", field.Text); store.Value.ExcludedApps.Clear();
            await Reset(); await Type("ghbdsn "); await Type("a"); await keyboard.UndoAsync(); await Task.Delay(100); Check("Typing invalidates stale undo", field.Text == "привіт a", field.Text);
            await Reset(); password.Focus(); await Task.Delay(500); await Type("ghbdsn "); Check("Password field skipped", password.Password == "ghbdsn ", password.Password == "ghbdsn " ? "unchanged" : "unexpected transformation");
            await Reset(); field.Text = "ghbdsn"; field.SelectAll();
            // GetDataObject may return a live OLE proxy, not a snapshot. Render its
            // formats before replacing the clipboard; republishing that old proxy
            // after the sentinel can leave subsequent clipboard reads unusable.
            var clipboardBefore = new DataObject();
            var clipboardSource = Clipboard.GetDataObject();
            if (clipboardSource != null) foreach (var format in clipboardSource.GetFormats(false))
            {
                var value = clipboardSource.GetData(format, false);
                if (value is MemoryStream memory) value = new MemoryStream(memory.ToArray());
                else if (value is byte[] bytes) value = bytes.ToArray();
                else if (value is string[] files) value = files.ToArray();
                if (value != null) clipboardBefore.SetData(format, value, false);
            }
            try
            {
                Clipboard.SetText("Switchcraft clipboard sentinel");
                await keyboard.ConvertSelectionAsync("uk"); await Task.Delay(300);
                Check("Selected text is converted", field.Text == "привіт", field.Text);
                Check("Clipboard text restored", Clipboard.GetText() == "Switchcraft clipboard sentinel", "clipboard checked");
            }
            finally
            {
                if (clipboardBefore.GetFormats(false).Length > 0) Clipboard.SetDataObject(clipboardBefore, true);
                else Clipboard.Clear();
            }
            await Reset(); field.Text = "ghbdsn"; field.SelectAll();
            await Task.WhenAll(keyboard.ConvertSelectionAsync("uk"), keyboard.ConvertSelectionAsync("ru"));
            Check("Concurrent conversion shortcuts run only once", field.Text == "привіт", field.Text);
            await Reset(); await Type("ghbdsn\n"); Check("Generic Enter is passed once without resubmitting", field.Text == "ghbdsn\r\n" || field.Text == "ghbdsn\n", field.Text);
            await Reset(); keyboard.Pause(); field.Text = "ghbdsn"; field.SelectAll();
            await keyboard.ConvertSelectionAsync("uk");
            Check("Explicit selection conversion remains available while paused", field.Text == "привіт", field.Text);
            if (Win32.LayoutFor("uk") != IntPtr.Zero)
            {
                await Reset(); store.Value.SwitchLayout = true; await Type("ghbdsn ");
                Check("System layout switches after correction", Win32.LayoutLanguage(hwnd) == "uk", Win32.LayoutLanguage(hwnd));
                await keyboard.UndoAsync(); await Task.Delay(200);
                Check("Undo restores previous system layout", Win32.LayoutLanguage(hwnd) == "en" && field.Text == "ghbdsn ", Win32.LayoutLanguage(hwnd) + ": " + field.Text);
                // A fast typist's next word arrives while the correction runs: those keys are held, and the layout must be
                // switched before they are replayed. The clipboard is kept open so the correction waits with the hold active.
                await Reset(); bool clipboardHeld = OpenClipboard(hwnd);
                try
                {
                    await Type("ghbdsn ", 0);
                    for (int i = 0; i < 100 && !keyboard.HoldingKeys; i++) await Task.Delay(10);
                    await Type("zr", 0);
                }
                finally { if (clipboardHeld) CloseClipboard(); }
                await Task.Delay(1000);
                Check("Keys typed during a correction are replayed in the switched layout",
                    clipboardHeld && field.Text == "привіт як" && Win32.LayoutLanguage(hwnd) == "uk", Win32.LayoutLanguage(hwnd) + ": " + field.Text);
                store.Value.SwitchLayout = false;
            }
            if (Win32.LayoutFor("ru") != IntPtr.Zero)
            {
                await Reset(); Win32.SwitchLayout(hwnd, "ru"); await Task.Delay(300); await Type("hello ");
                Check("English typed in the Russian layout is corrected", field.Text == "hello ", Win32.LayoutLanguage(hwnd) + ": " + field.Text);
            }
            if (Win32.LayoutFor("uk") != IntPtr.Zero)
            {
                // English switched off, Ukrainian primary: the English layout is still followed, nothing becomes English.
                store.Value.Languages = new() { "uk", "ru" }; store.Save(); engine.Reload();
                await Reset(); await Type("ghbdsn ");
                Check("Without English the English layout is still corrected", field.Text == "привіт ", Win32.LayoutLanguage(hwnd) + ": " + field.Text);
                await Reset(); Win32.SwitchLayout(hwnd, "uk"); await Task.Delay(300); await Type("hello ");
                Check("Without English nothing is converted to English", field.Text == "руддщ ", Win32.LayoutLanguage(hwnd) + ": " + field.Text);
                store.Value.Languages = new() { "en", "uk", "ru" }; store.Save(); engine.Reload();
            }
            keyboard.DiagnosticsEnabled = true; await Reset(); await Type("ptktyt' ;bnj ptktyt "); await Task.Delay(800);
            Check("Folk word missing from the dictionary is converted once its sentence proves Ukrainian", field.Text == "зеленеє жито зелене ", field.Text + " | " + System.Text.Json.JsonSerializer.Serialize(keyboard.InputDiagnostics())); keyboard.DiagnosticsEnabled = false;
            if (Win32.LayoutFor("uk") != IntPtr.Zero && Win32.LayoutFor("ru") != IntPtr.Zero)
            {
                await Reset(); store.Value.SwitchLayout = true; await Type("ghbdtn ");
                Check("Russian seed selects RU, not first Cyrillic layout", Win32.LayoutLanguage(hwnd) == "ru" && field.Text == "привет ", Win32.LayoutLanguage(hwnd) + ": " + field.Text);
                await Reset(); Win32.SwitchLayout(hwnd, "uk"); await Task.Delay(300);
                await Type("rjulf z yf gjxnt cke;bk ");
                Check("Three independent Russian words confirm layout", field.Text == "когда я на почте служил " && Win32.LayoutLanguage(hwnd) == "ru", Win32.LayoutLanguage(hwnd) + ": " + field.Text);
                store.Value.SwitchLayout = false;
                await Reset(); Win32.SwitchLayout(hwnd, "uk"); await Task.Delay(300);
                await TypeUnicode("Когда я на почте служил ямщиком, ко мне постучался косматій ");
                Check("Live user's Russian phrase with Ukrainian letter", field.Text == "Когда я на почте служил ямщиком, ко мне постучался косматый ", field.Text);
                for (int attempt = 0; attempt < 3; attempt++)
                {
                    await Reset(); await TypeUnicode("Когда я на почте служил ямщиком, ко мне постучался косматій ");
                    Check("Repeated long phrase " + attempt, field.Text == "Когда я на почте служил ямщиком, ко мне постучался косматый ", field.Text);
                }
                await Reset(); Win32.SwitchLayout(hwnd, "uk"); await Task.Delay(300);
                await TypeUnicode("Зеленоглазое такси притормози притормози Кіргізстан ");
                Check("Live user's country-name phrase", field.Text == "Зеленоглазое такси притормози притормози Кыргызстан ", field.Text);
                await Reset(); Win32.SwitchLayout(hwnd, "ru"); await Task.Delay(300);
                await TypeUnicode("свыт "); Check("Live reverse Russian to Ukrainian correction", field.Text == "світ ", field.Text);
                await Reset(); await TypeUnicode("Вона стоїть у косматій шапці ");
                Check("Live valid Ukrainian phrase is preserved", field.Text == "Вона стоїть у косматій шапці ", field.Text);
                await Reset(); await TypeUnicode("Когда служил, записал: «Вона у косматій шапці». ");
                Check("Live language switch inside quotation preserved", field.Text == "Когда служил, записал: «Вона у косматій шапці». ", field.Text);
                await Reset(); await TypeUnicode("Когда служил "); field.Select(field.Text.Length, 0);
                await Type("\n"); await TypeUnicode("косматій ");
                Check("Paragraph boundary clears language context", field.Text.EndsWith("\nкосматій "), field.Text);
            }
            await Reset(); store.Value.LearningMode = true;
            string? learned = null; keyboard.LearnWord += word => learned = word;
            await Type("ghbdsn "); await keyboard.UndoAsync(); await Task.Delay(100);
            Check("Learning mode receives only undone word", learned == "ghbdsn", learned ?? "none");
            var source = (HwndSource)PresentationSource.FromVisual(window); using var registry = new HotkeyRegistry(source);
            bool hit = false; registry.Pressed += _ => hit = true;
            var conflicts = registry.Apply(new() { ["test"] = "Ctrl+Alt+F11" });
            Check("System hotkey registers", conflicts.Count == 0, string.Join(",", conflicts));
            var input = new List<Win32.Input> { Win32.Key(17), Win32.Key(18), Win32.Key(0x7A), Win32.Key(0x7A, true), Win32.Key(18, true), Win32.Key(17, true) };
            RequireTestFocus();
            Win32.Send(input); await Task.Delay(300); Check("System hotkey dispatches", hit, hit.ToString());
            }
            store.Value.LearningMode = false; store.Value.SwitchLayout = false;
            detection.Keys.Save("synthetic-integration-key"); store.Value.DetectionMode = "typesafe";
            await Reset(); await Type("ghbdsn "); await Task.Delay(250);
            Check("AI hook applies the selected candidate", field.Text == "привіт " && aiHandler.Calls > 0, field.Text);
            await keyboard.UndoAsync(); await Task.Delay(200);
            Check("AI correction uses existing undo", field.Text == "ghbdsn ", field.Text);
            await Reset(); detection.Api.Reset(); aiHandler.DelayMs = 850;
            await Type("ghbdsn ");
            Check("Input stays healthy while HTTP is pending", keyboard.Running, keyboard.Health);
            await Type("hello");
            Check("New input cancels pending AI replacement", field.Text == "ghbdsn hello" && aiHandler.Cancelled, field.Text);
            await Task.Delay(1700);
            Check("Idle retries the completed word with fresh text verification", field.Text == "привіт hello", field.Text);
            await Reset(); detection.Api.Reset(); aiHandler.Cancelled = false;
            await Type("ghbdsn "); store.Value.DetectionMode = "local"; keyboard.Reset(); detection.Api.Reset(); await Task.Delay(1000);
            Check("Mode off cancels in-flight AI work", field.Text == "ghbdsn " && aiHandler.Cancelled, field.Text);
            int aiCalls = aiHandler.Calls;
            await Reset(); await Type("ghbdsn ");
            Check("Mode off restores local correction with no HTTP", field.Text == "привіт " && aiHandler.Calls == aiCalls, field.Text);
            store.Value.DetectionMode = "typesafe"; aiHandler.DelayMs = 0; detection.Api.Reset();
            aiHandler.Choice = "keep"; aiHandler.Confidence = .45; aiHandler.Probability = .6;
            async Task ResetShort()
            {
                await Reset(); detection.Api.Reset(); Win32.SwitchLayout(hwnd, "ru"); await Task.Delay(200);
            }
            await ResetShort(); await TypeUnicode("Червоний ы зелений "); await Task.Delay(300);
            Check("Short mismatch corrected after continuous three-word typing", field.Text == "Червоний і зелений ", field.Text);
            string? learnedShort = null;
            keyboard.LearnWord += value => learnedShort = value;
            store.Value.LearningMode = true;
            await keyboard.UndoAsync(); await Task.Delay(200);
            Check("Short retry undo preserves following text", field.Text == "Червоний ы зелений ", field.Text);
            Check("Short retry learning uses only corrected word", learnedShort == "ы", learnedShort ?? "none");
            store.Value.LearningMode = false;
            await ResetShort(); await TypeUnicode("Червоний ы "); await Task.Delay(300);
            Check("Short mismatch corrected at first pause", field.Text == "Червоний і ", field.Text);
            await ResetShort(); await TypeUnicode("Українська буква ы "); await Task.Delay(300);
            Check("Letter discussion is preserved during live typing", field.Text == "Українська буква ы ", field.Text);
            await ResetShort(); await TypeUnicode("Красный и зелёный "); await Task.Delay(300);
            Check("Valid Russian conjunction preserved during live typing", field.Text == "Красный и зелёный ", field.Text);
            await ResetShort(); await TypeUnicode("Червоний і зелений "); await Task.Delay(300);
            Check("Valid Ukrainian conjunction preserved during live typing", field.Text == "Червоний і зелений ", field.Text);
            await ResetShort(); aiHandler.DelayMs = 850;
            await TypeUnicode("Червоний ы зелений "); field.CaretIndex = 0; await Task.Delay(1000);
            Check("Moving caret invalidates delayed short retry", field.Text == "Червоний ы зелений ", field.Text);
            await ResetShort(); await TypeUnicode("Червоний ы зелений ");
            field.Text = "Збережений текст "; field.CaretIndex = field.Text.Length; await Task.Delay(1000);
            Check("Changed editor suffix rejects delayed short retry", field.Text == "Збережений текст ", field.Text);
            await ResetShort(); await TypeUnicode("Червоний ы зелений "); await Type("\b"); await Task.Delay(1000);
            Check("Backspace cancels delayed short retry", field.Text == "Червоний ы зелений", field.Text);
            aiHandler.DelayMs = 0; aiHandler.Choice = "uk"; aiHandler.Confidence = .98; aiHandler.Probability = .98;
            aiCalls = aiHandler.Calls;
            await Reset(); password.Clear(); password.Focus(); await Task.Delay(500); await Type("ghbdsn ");
            Check("AI mode never sends password-field input", aiHandler.Calls == aiCalls && password.Password == "ghbdsn ", "synthetic password untouched");
            await Reset(); store.Value.ExcludedApps.Add("Switchcraft.exe"); await Type("ghbdsn ");
            Check("AI mode respects app exclusions before HTTP", aiHandler.Calls == aiCalls && field.Text == "ghbdsn ", field.Text);
            }
        }
        catch (Exception ex) { Check("Harness exception", false, ex.ToString()); }
        finally
        {
            keyboard?.Dispose(); detection?.Dispose(); engine?.Dispose(); window.Close();
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(output))!);
            File.WriteAllText(output, JsonSerializer.Serialize(new { timestamp = DateTimeOffset.Now, total = checks.Count, passed = checks.Count - failed, failed, checks, messages }, new JsonSerializerOptions { WriteIndented = true }));
            if (Directory.Exists(dir) && Path.GetFullPath(dir).StartsWith(Path.GetFullPath(Path.GetTempPath()), StringComparison.OrdinalIgnoreCase)) Directory.Delete(dir, true);
            Application.Current.Shutdown(failed == 0 ? 0 : 1);
        }
    }
}
