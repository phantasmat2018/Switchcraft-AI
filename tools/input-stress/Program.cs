using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using Switchcraft;

// Opt-in product test, restricted to its own freshly created editor. Never targets
// another application or reads user documents. Production classes are referenced,
// not reimplemented. A paused control distinguishes injector errors from corrections.
internal static class Program
{
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern short VkKeyScanEx(char character, IntPtr layout);
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true, Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };
    [STAThread]
    private static void Main(string[] args)
    {
        if (args.Length != 2) throw new ArgumentException("Pass corpus.txt and output directory.");
        var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        System.Windows.Media.RenderOptions.ProcessRenderMode = RenderMode.SoftwareOnly;
        app.Startup += async (_, _) => await Run(Path.GetFullPath(args[0]), Path.GetFullPath(args[1]));
        app.Run();
    }
    private static async Task Run(string corpusPath, string output)
    {
        Directory.CreateDirectory(output);
        string expected = File.ReadAllText(corpusPath).Replace("\r\n", "\n").TrimEnd() + " ";
        // All mutations preserve character counts and have explicit expected text.
        var mutations = new Dictionary<string, string> { ["Привіт"] = "Ghbdsn", ["привет"] = "ghbdtn", ["hello"] = "руддщ", ["world"] = "цщкдв", ["світ"] = "свыт", ["Кыргызстан"] = "Кіргізстан" };
        string noisy = Regex.Replace(expected, @"[\p{L}]+", m => mutations.GetValueOrDefault(m.Value, m.Value));
        var corruptionCount = Regex.Matches(expected, @"[\p{L}]+").Count(m => mutations.ContainsKey(m.Value));
        File.WriteAllText(Path.Combine(output, "expected.txt"), expected);
        File.WriteAllText(Path.Combine(output, "wrong-layout-input.txt"), noisy);
        var runs = new List<object>();
        var settingsDir = Path.Combine(Path.GetTempPath(), "Switchcraft-stress-" + Guid.NewGuid().ToString("N"));
        var field = new TextBox { AcceptsReturn = true, AcceptsTab = true, FontFamily = new FontFamily("Consolas"), FontSize = 17, TextWrapping = TextWrapping.Wrap, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Margin = new Thickness(16), Background = new SolidColorBrush(Color.FromRgb(25, 28, 36)), Foreground = Brushes.White };
        var heading = new TextBlock { FontSize = 18, Margin = new Thickness(16), Foreground = Brushes.White, Text = "Switchcraft · стрес-тест трьох мов" };
        var panel = new DockPanel(); DockPanel.SetDock(heading, Dock.Top); panel.Children.Add(heading); panel.Children.Add(field);
        var window = new Window { Title = "Switchcraft — 3000 символів: стрес-тест", Width = 1060, Height = 740, Background = new SolidColorBrush(Color.FromRgb(17, 20, 27)), Content = panel, WindowStartupLocation = WindowStartupLocation.CenterScreen };
        NativeEngine? engine = null; KeyboardService? keyboard = null; string? error = null;
        var currentCorrections = new List<object>(); var currentMessages = new List<string>();
        var heartbeat = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(50) };
        long previousTick = 0; double largestUiGap = 0;
        heartbeat.Tick += (_, _) => { long now = Stopwatch.GetTimestamp(); if (previousTick != 0) largestUiGap = Math.Max(largestUiGap, Stopwatch.GetElapsedTime(previousTick, now).TotalMilliseconds); previousTick = now; };
        void Progress(object progress) => SettingsStore.AtomicWrite(Path.Combine(output, "progress.json"), JsonSerializer.Serialize(progress, Json));
        try
        {
            var store = new SettingsStore(settingsDir); store.Value.SwitchLayout = true; store.Value.KeepHistory = false;
            engine = new NativeEngine(store); keyboard = new KeyboardService(engine, store, allowOwnProcess: true);
            keyboard.Corrected += (result, _) => { lock (currentCorrections) currentCorrections.Add(new { result.Original, result.Text, result.Target, result.Reason }); };
            keyboard.Status += message => { lock (currentMessages) currentMessages.Add(message); };
            window.Show(); window.Activate(); field.Focus();
            IntPtr hwnd = new WindowInteropHelper(window).Handle;
            void RequireFocus() { if (Win32.GetForegroundWindow() != hwnd || !field.IsKeyboardFocused) throw new InvalidOperationException("Stress test aborted on focus loss; no input is sent to another app."); }
            Progress(new { status = "waiting_for_own_window", expectedCharacters = expected.Length, corruptionCount });
            for (int i = 0; i < 120 && Win32.GetForegroundWindow() != hwnd; i++) await Task.Delay(250);
            RequireFocus(); Win32.SwitchLayout(hwnd, "en"); keyboard.Start(); heartbeat.Start(); await Task.Delay(900);
            var plans = new[] {
                (Name: "control-paused-fast", Text: expected, Mode: "balanced", Delay: 2, Boundary: 0, Paused: true),
                (Name: "balanced-clean-normal", Text: expected, Mode: "balanced", Delay: 10, Boundary: 80, Paused: false),
                (Name: "balanced-wrong-layout-normal", Text: noisy, Mode: "balanced", Delay: 10, Boundary: 80, Paused: false),
                (Name: "balanced-wrong-layout-fast", Text: noisy, Mode: "balanced", Delay: 2, Boundary: 0, Paused: false),
                (Name: "careful-clean-normal", Text: expected, Mode: "careful", Delay: 10, Boundary: 80, Paused: false)
            };
            foreach (var plan in plans)
            {
                RequireFocus(); keyboard.Pause(); field.Clear(); field.Focus();
                store.Value.Sensitivity = plan.Mode; engine.Reload(); Win32.SwitchLayout(hwnd, "en");
                keyboard.Reset(); if (!plan.Paused) keyboard.Resume();
                await Task.Delay(600); currentCorrections.Clear(); currentMessages.Clear(); previousTick = 0; largestUiGap = 0;
                heading.Text = $"{plan.Name} · 0 / {plan.Text.Length}";
                var watch = Stopwatch.StartNew(); var process = Process.GetCurrentProcess(); var cpuStart = process.TotalProcessorTime;
                int sent = 0, layoutChanges = 0, unicodeFallbacks = 0;
                foreach (Match chunk in Regex.Matches(plan.Text, @"\S+|\s+"))
                {
                  var text = chunk.Value;
                  string layoutLanguage = Regex.IsMatch(text, "[іїєґІЇЄҐ]") ? "uk" : Regex.IsMatch(text, "[А-Яа-яЁё]") ? "ru" : Regex.IsMatch(text, "[A-Za-z]") ? "en" : "";
                  if (layoutLanguage.Length > 0 && Win32.LayoutLanguage(hwnd) != layoutLanguage)
                  {
                    await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
                    RequireFocus(); Win32.SwitchLayout(hwnd, layoutLanguage); layoutChanges++;
                    for (int attempt = 0; attempt < 20 && Win32.LayoutLanguage(hwnd) != layoutLanguage; attempt++) await Task.Delay(5);
                    if (Win32.LayoutLanguage(hwnd) != layoutLanguage) throw new InvalidOperationException("Requested installed layout was not activated: " + layoutLanguage);
                  }
                  foreach (char ch in text)
                  {
                    RequireFocus();
                    var keys = new List<Win32.Input>();
                    short scan = ch == '\n' ? (short)13 : VkKeyScanEx(ch, Win32.GetKeyboardLayout(Win32.GetWindowThreadProcessId(hwnd, out _)));
                    if (scan == -1 || (scan >> 8 & ~1) != 0)
                    {
                        // Typographic punctuation absent from installed layouts is
                        // isolated from surrounding events; ordinary letters use VKs.
                        await Task.Delay(30); RequireFocus(); unicodeFallbacks++;
                        keys.Add(Win32.Unicode(ch)); keys.Add(Win32.Unicode(ch, true));
                    }
                    else
                    {
                        bool shift = (scan & 0x100) != 0;
                        if (char.IsLetter(ch) && (Win32.GetKeyState(0x14) & 1) != 0) shift = !shift;
                        if (shift) keys.Add(Win32.Key(0x10));
                        keys.Add(Win32.Key((ushort)(scan & 255))); keys.Add(Win32.Key((ushort)(scan & 255), true));
                        if (shift) keys.Add(Win32.Key(0x10, true));
                    }
                    for (int k = 0; k < keys.Count; k++) { var item = keys[k]; item.Data.Keyboard.Extra = UIntPtr.Zero; keys[k] = item; }
                    if (!Win32.Send(keys)) throw new InvalidOperationException("Windows rejected stress input.");
                    sent++;
                    // Async continuations normally outrank Input in WPF. Explicitly
                    // drain editor input before generating the next key or changing
                    // layout, otherwise the test itself starves its own input queue.
                    await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
                    await Task.Delay(scan == -1 || (scan >> 8 & ~1) != 0 ? Math.Max(30, plan.Delay) : plan.Delay);
                    if (plan.Boundary > 0 && ch is ' ' or '\n') await Task.Delay(plan.Boundary);
                    if (sent % 100 == 0) { heading.Text = $"{plan.Name} · {sent} / {plan.Text.Length}"; Progress(new { status = "typing", run = plan.Name, sent, total = plan.Text.Length, elapsedSeconds = watch.Elapsed.TotalSeconds, completedRuns = runs.Count }); }
                  }
                }
                watch.Stop(); await Task.Delay(1200); RequireFocus();
                string actual = field.Text.Replace("\r\n", "\n");
                File.WriteAllText(Path.Combine(output, plan.Name + "-actual.txt"), actual);
                var differences = Differences(expected, actual);
                object[] corrections; string[] messages;
                lock (currentCorrections) corrections = currentCorrections.ToArray();
                lock (currentMessages) messages = currentMessages.ToArray();
                process.Refresh();
                var result = new { name = plan.Name, sensitivity = plan.Mode, paused = plan.Paused, sentCharacters = sent, actualCharacters = actual.Length,
                    targetDelayMs = plan.Delay, wordPauseMs = plan.Boundary, layoutChanges, unicodeFallbacks, typingSeconds = watch.Elapsed.TotalSeconds, actualCharactersPerSecond = sent / watch.Elapsed.TotalSeconds,
                    exactInputPreserved = actual == plan.Text, exactExpectedText = actual == expected, sameCharacterCount = actual.Length == expected.Length,
                    inputEditDistance = EditDistance(plan.Text, actual), expectedEditDistance = EditDistance(expected, actual),
                    expectedCorrections = plan.Text == noisy ? corruptionCount : 0, confirmedCorrections = corrections.Length,
                    corrections, differences, messages, hookStillRunning = plan.Paused ? keyboard.Health.Length == 0 : keyboard.Running, health = keyboard.Health,
                    maxUiHeartbeatGapMs = largestUiGap, cpuSeconds = (process.TotalProcessorTime - cpuStart).TotalSeconds, workingSetBytes = process.WorkingSet64 };
                runs.Add(result); SettingsStore.AtomicWrite(Path.Combine(output, "runs.json"), JsonSerializer.Serialize(runs, Json));
                Progress(new { status = "run_completed", run = plan.Name, completedRuns = runs.Count, exactExpectedText = actual == expected, characters = actual.Length });
                if (plan.Paused && actual != expected) throw new InvalidOperationException("Paused control failed: stop before attributing injector/editor errors to Switchcraft.");
            }
        }
        catch (Exception ex) { error = ex.ToString(); }
        finally
        {
            heartbeat.Stop(); keyboard?.Dispose(); engine?.Dispose();
            var report = new { timestamp = DateTimeOffset.Now, applicationVersion = typeof(KeyboardService).Assembly.GetName().Version?.ToString(),
                expectedCharacters = expected.Length, words = Regex.Matches(expected, @"\S+").Count, intentionalWrongLayoutWords = corruptionCount,
                method = "External untagged virtual-key SendInput using VkKeyScanEx and installed EN/UK/RU layouts, own WPF editor, production KeyboardService/NativeEngine, isolated default rules. Layout selected per token to produce its literal input. Typographic characters unavailable on keyboards use isolated Unicode packets. Focus checked before each character. UI heartbeat 50 ms. Normal typing adds 80 ms after word boundaries; fast typing does not.",
                limitations = "Not a Notepad++/Scintilla test. Automatic and explicit layout switches are exercised, but deliberate per-token layout selection can reset language context. No switches between apps. Timing includes Windows scheduler overhead. Finite generated sample, not a universal accuracy estimate.",
                completedRuns = runs.Count, error, runs };
            SettingsStore.AtomicWrite(Path.Combine(output, "report.json"), JsonSerializer.Serialize(report, Json));
            Progress(new { status = error == null ? "completed" : "aborted", completedRuns = runs.Count, error });
            window.Close();
            // Only the harness-created, explicitly validated temporary directory is removed.
            if (Path.GetFullPath(settingsDir).StartsWith(Path.GetFullPath(Path.GetTempPath()), StringComparison.OrdinalIgnoreCase) && Directory.Exists(settingsDir)) Directory.Delete(settingsDir, true);
            Application.Current.Shutdown(error == null ? 0 : 1);
        }
    }
    private static object[] Differences(string expected, string actual)
    {
        var left = Regex.Matches(expected, @"\S+").Select(m => m.Value).ToArray();
        var right = Regex.Matches(actual, @"\S+").Select(m => m.Value).ToArray();
        return Enumerable.Range(0, Math.Max(left.Length, right.Length)).Where(i => (i < left.Length ? left[i] : null) != (i < right.Length ? right[i] : null))
            .Select(i => (object)new { token = i, expected = i < left.Length ? left[i] : null, actual = i < right.Length ? right[i] : null }).ToArray();
    }
    private static int EditDistance(string a, string b)
    {
        var row = Enumerable.Range(0, b.Length + 1).ToArray();
        for (int i = 1; i <= a.Length; i++) { int diagonal = row[0]; row[0] = i; for (int j = 1; j <= b.Length; j++) { int old = row[j]; row[j] = Math.Min(Math.Min(row[j] + 1, row[j - 1] + 1), diagonal + (a[i - 1] == b[j - 1] ? 0 : 1)); diagonal = old; } }
        return row[b.Length];
    }
}
