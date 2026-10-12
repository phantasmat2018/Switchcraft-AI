using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using Microsoft.Win32;

namespace Switchcraft;

public partial class MainWindow : Window
{
    private App Host => App.Current;
    private SettingsStore Store => Host.Store;
    private bool ready;
    private HotkeyRegistry? hotkeys;
    private CancellationTokenSource? quickRequest, apiTest, localModelTest, localSetup, localPreparation;
    private readonly Dictionary<string, TextBox> hotkeyBoxes = new();
    private const string StartupValueName = "Switchcraft.Keyboard";
    internal static string AppVersion => (System.Reflection.CustomAttributeExtensions.GetCustomAttribute<System.Reflection.AssemblyInformationalVersionAttribute>(typeof(MainWindow).Assembly)?.InformationalVersion ?? "").Split('+')[0];
    private sealed record LanguageRow(string Code, string Label);
    private sealed record PairRow(string Source, string Target) { public string Label => Source + "  →  " + Target; }
    public MainWindow()
    {
        InitializeComponent();
        // The shown version always comes from the build itself, never from text typed into the layout.
        SidebarVersion.Text = "SWITCHCRAFT AI  /  " + AppVersion; AboutTitle.Text += " " + AppVersion;
        UseLayoutRounding = true; SnapsToDevicePixels = true;
        System.Windows.Media.TextOptions.SetTextFormattingMode(this, System.Windows.Media.TextFormattingMode.Display);
        System.Windows.Media.TextOptions.SetTextRenderingMode(this, System.Windows.Media.TextRenderingMode.Grayscale);
        Height = Math.Min(850, SystemParameters.WorkArea.Height - 30);
        Width = Math.Min(1180, SystemParameters.WorkArea.Width - 30);
        foreach (var (box, hint) in new[] { (RuleSearch, "Пошук правил…"), (PairSource, "Що замінити"), (PairTarget, "На що замінити"), (NeverWord, "Слово-виняток"), (ExcludedName, "Наприклад, Code.exe"), (SourceText, "Вставте або введіть текст…") })
        { box.Style = (Style)FindResource("Watermark"); box.Tag = hint; }
        StateChanged += (_, _) => UpdateWindowState(); UpdateWindowState();
        ready = true;
        HistoryList.ItemsSource = Host.History;
        StartMinimized.IsChecked = Store.Value.StartMinimized; SwitchLayoutCheck.IsChecked = Store.Value.SwitchLayout;
        LearningCheck.IsChecked = Store.Value.LearningMode; HistoryCheck.IsChecked = Store.Value.KeepHistory;
        SensitivityBox.SelectedIndex = Store.Value.Sensitivity == "careful" ? 1 : 0;
        TypeSafeModelBox.Text = Store.Value.TypeSafeModel;
        LayaModelPathBox.Text = Store.Value.LayaModelPath;
        LayaDeviceBox.SelectedIndex = Store.Value.LayaDevice == "cpu" ? 1 : Store.Value.LayaDevice == "cuda" ? 2 : 0;
        RefreshAiSettings();
        if (Host.Detection.UsesLocalModel && !LayaSetup.IsPrepared(Store.Value.LayaModelPath)) LocalModelConfigStatus.Text = LayaSetupText;
        using (var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Run")) LaunchAtLogin.IsChecked = key?.GetValue(StartupValueName) != null;
        RefreshLanguages(); RefreshRules(); RefreshExcluded(); BuildHotkeys(); RefreshStatus(); UpdateQuick();
        SourceInitialized += (_, _) =>
        {
            hotkeys = new HotkeyRegistry((HwndSource)PresentationSource.FromVisual(this));
            hotkeys.Pressed += HandleHotkey; ApplyHotkeys();
            ((HwndSource)PresentationSource.FromVisual(this)).AddHook(WindowMessage);
        };
        Closing += (_, e) => { e.Cancel = true; Hide(); };
        Host.Exit += (_, _) => localSetup?.Cancel();
    }
    public void ShowStatus(string value) { StatusText.Text = value; StatusText.ToolTip = value; }
    public void RefreshStatus()
    {
        if (!ready) return;
        PrivacyTitle.Text = "●  " + Host.Detection.ModeLabel;
        PrivacyDescription.Text = Host.Detection.UsesCloud ? "Мову визначає API.\nКлюч захищено."
            : "Модель на цьому комп’ютері.\nВаш текст залишається локально.";
        AiServiceStatus.Text = Host.Detection.Api.Status;
        LocalModelServiceStatus.Text = Host.Detection.Local.Status;
        if (localSetup == null)
            SetupLocalModelButton.Content = LayaSetup.IsPrepared(Store.Value.LayaModelPath) ? "Перевірити компоненти" : LayaSetup.IsOutdated(Store.Value.LayaModelPath) ? "Оновити модель" : "Завантажити й підготувати";
        bool running = Host.Keyboard.Running;
        EnabledToggle.IsChecked = running;
        StateLabel.Text = running ? "●  Активний" : "Ⅱ  На паузі";
        AutoDescription.Text = running ? "Допомагає у фоновому режимі" : Host.Keyboard.PausedUntil > DateTime.Now ? "Пауза до " + Host.Keyboard.PausedUntil.Value.ToString("HH:mm") : "Увімкніть, коли знадобиться допомога";
        if (Host.Keyboard.Health.Length > 0) { StateLabel.Text = "●  Потрібна увага"; AutoDescription.Text = Host.Keyboard.Health; }
        CorrectionCount.Text = Host.SessionCorrections.ToString();
        CorrectionLabel.Text = Plural(Host.SessionCorrections, "виправлення", "виправлення", "виправлень") + " за сесію";
        LanguageCount.Text = Store.Value.Languages.Count.ToString();
        LanguageCountLabel.Text = Plural(Store.Value.Languages.Count, "мова", "мови", "мов") + " у вашому просторі";
        bool paused = Host.Keyboard.SessionPaused || Host.Keyboard.PausedUntil > DateTime.Now;
        PauseButton.Content = paused ? "Продовжити роботу" : "Пауза на 15 хв";
        ClearHistoryButton.Visibility = Host.History.Count == 0 ? Visibility.Collapsed : Visibility.Visible;
        FooterLanguages.Text = string.Join("   /   ", Store.Value.Languages.Select(x => x == "uk" ? "УК" : x.ToUpperInvariant()));
        HistoryEmpty.Visibility = Host.History.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        HistoryEmpty.Text = Store.Value.KeepHistory ? "Поки що тихо. Ваші виправлення з’являться тут." : "Історію вимкнено в налаштуваннях.";
    }
    private void Nav_Checked(object sender, RoutedEventArgs e)
    {
        if (Pages == null || sender is not RadioButton { Tag: string target }) return;
        foreach (FrameworkElement child in Pages.Children) child.Visibility = child.Name == target ? Visibility.Visible : Visibility.Collapsed;
    }
    private void Minimize_Click(object sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;
    private void Maximize_Click(object sender, RoutedEventArgs e) => WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;
    /// <summary>Maximized, the window fills the work area (see WindowMessage) without its own border; the button shows
    /// and says what it will do.</summary>
    private void UpdateWindowState()
    {
        bool maximized = WindowState == WindowState.Maximized;
        if (Content is System.Windows.Controls.Border root) root.BorderThickness = new Thickness(maximized ? 0 : 1);
        string label = maximized ? "Відновити розмір" : "Розгорнути";
        MaximizeButton.Content = maximized ? "\uE923" : "\uE922"; MaximizeButton.ToolTip = label;
        System.Windows.Automation.AutomationProperties.SetName(MaximizeButton, label);
    }
    private static IntPtr WindowMessage(IntPtr window, int message, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        // WM_GETMINMAXINFO: without a system frame Windows would maximize over the taskbar and cut off the status bar.
        if (message == 0x0024) Win32.FitMaximizedToWorkArea(window, lParam);
        return IntPtr.Zero;
    }
    /// <summary>Ukrainian plural: 1 виправлення, 2 виправлення, 5 виправлень (11–14 take the "many" form).</summary>
    internal static string Plural(long count, string one, string few, string many)
    {
        long n = Math.Abs(count) % 100, last = n % 10;
        return n is >= 11 and <= 14 ? many : last == 1 ? one : last is >= 2 and <= 4 ? few : many;
    }
    private void Close_Click(object sender, RoutedEventArgs e) => Hide();
    private void Enabled_Click(object sender, RoutedEventArgs e)
    {
        if (EnabledToggle.IsChecked == true) Host.Keyboard.Resume(); else { Store.Value.Enabled = false; Host.Keyboard.Reset(); }
        Store.Save(); RefreshStatus(); ShowStatus(Host.Keyboard.Running ? "Автоматичне виправлення ввімкнено." : "Автоматичне виправлення вимкнено.");
    }
    private void QuickInput_Changed(object sender, TextChangedEventArgs e) { if (ready) UpdateQuick(); }
    private async void UpdateQuick()
    {
        quickRequest?.Cancel();
        using var cancellation = new CancellationTokenSource(); quickRequest = cancellation;
        string text = QuickInput.Text;
        try
        {
            if (Host.Detection.UsesAi) { QuickReason.Text = "Очікування " + Host.Detection.ModeLabel + "…"; await Task.Delay(400, cancellation.Token); }
            var result = await Task.Run(() => Host.Detection.EvaluateAsync(text, "", "", "", cancellation.Token));
            if (cancellation.IsCancellationRequested || QuickInput.Text != text) return;
            QuickOutput.Text = result.Text;
            QuickReason.Text = result.Reason switch
            {
                "typesafe_ai" => $"{LanguageName(result.Target)} · TypeSafe AI · впевненість {result.Confidence:P0}",
                "laya_ai" => $"{LanguageName(result.Target)} · Laya · впевненість {result.Confidence:P0}",
                "ai_keep" => "TypeSafe AI · залишено без змін",
                "local_ai_keep" => "Laya · залишено без змін",
                "ai_uncertain" => "TypeSafe не впевнений",
                "local_ai_uncertain" => "Laya не впевнена",
                "ai_fallback" => Host.Detection.Api.Status,
                "local_ai_fallback" => Host.Detection.Local.Status,
                _ => result.Changed ? $"{LanguageName(result.Target)} · локальний рушій" : text.Length == 0 ? "Введіть слово для перевірки" : "Виправлення не потрібне або впевненості недостатньо"
            };
        }
        catch (OperationCanceledException) { }
        catch { if (!cancellation.IsCancellationRequested) QuickReason.Text = "Не вдалося завершити перевірку."; }
        finally { if (quickRequest == cancellation) quickRequest = null; }
    }
    private static string LanguageName(string code) => code switch { "uk" => "Українська", "en" => "English", "ru" => "Російська", "" => "УК / RU", _ => code };
    private void ClearHistory_Click(object sender, RoutedEventArgs e) { Host.History.Clear(); RefreshStatus(); ShowStatus("Історію сесії очищено."); }
    private void Convert_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            if (SourceText.Text.Length == 0) { ShowStatus("Введіть або вставте текст для перетворення."); SourceText.Focus(); return; }
            var target = (TargetLanguage.SelectedItem as ComboBoxItem)?.Tag?.ToString();
            ResultText.Text = target == "correct" ? Host.Engine.CorrectText(SourceText.Text) : Host.Engine.Convert(SourceText.Text, target == "auto" ? null : target).Text;
            ShowStatus($"Перетворено {SourceText.Text.Length:N0} {Plural(SourceText.Text.Length, "символ", "символи", "символів")}.");
        }
        catch (Exception ex) { ShowStatus(ex.Message); }
    }
    private void Paste_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            if (!Clipboard.ContainsText()) { ShowStatus("У буфері обміну немає тексту."); return; }
            string text = Clipboard.GetText(); SourceText.Text = text[..Math.Min(text.Length, 100_000)];
            ShowStatus(text.Length > 100_000 ? "Вставлено перші 100 000 символів." : "Текст вставлено.");
        }
        catch { ShowStatus("Буфер обміну зайнятий."); }
    }
    private void Copy_Click(object sender, RoutedEventArgs e) { try { if (ResultText.Text.Length == 0) { ShowStatus("Немає результату для копіювання. Спочатку натисніть «Перетворити»."); return; } Clipboard.SetText(ResultText.Text); ShowStatus("Результат скопійовано."); } catch { ShowStatus("Буфер обміну зайнятий."); } }
    private void RefreshLanguages()
    {
        string? selected = (LanguageList.SelectedItem as LanguageRow)?.Code;
        var order = Store.Value.Languages.Concat(new[] { "en", "uk", "ru" }).Distinct();
        string State(string code) => Store.Value.Languages.IndexOf(code) switch { 0 => "активна, головна", > 0 => "активна", _ => "вимкнена" };
        var rows = order.Select((code, i) => new LanguageRow(code, $"{i + 1:00}    {LanguageName(code)}    ·    {State(code)}" + (Win32.LayoutFor(code) == IntPtr.Zero ? "   /   розкладку не встановлено" : ""))).ToList();
        LanguageList.ItemsSource = rows; LanguageList.SelectedItem = rows.FirstOrDefault(x => x.Code == selected) ?? rows.FirstOrDefault();
        RefreshStatus();
    }
    private void MoveLanguage(int direction)
    {
        if (LanguageList.SelectedItem is not LanguageRow row) { ShowStatus("Виберіть мову в списку."); return; }
        int index = Store.Value.Languages.IndexOf(row.Code), next = index + direction;
        if (index < 0) { ShowStatus("Спочатку ввімкніть цю мову."); return; }
        if (next < 0 || next >= Store.Value.Languages.Count) return;
        (Store.Value.Languages[index], Store.Value.Languages[next]) = (Store.Value.Languages[next], Store.Value.Languages[index]);
        Store.Save(); Host.Engine.Reload(); Host.Keyboard.Reset(); RefreshLanguages(); UpdateQuick();
        ShowStatus(index == 0 || next == 0 ? "Головна мова: " + LanguageName(Store.Value.Languages[0]) + "." : "Порядок мов змінено.");
    }
    private void LanguageUp_Click(object sender, RoutedEventArgs e) => MoveLanguage(-1);
    private void LanguageDown_Click(object sender, RoutedEventArgs e) => MoveLanguage(1);
    private void LanguageToggle_Click(object sender, RoutedEventArgs e)
    {
        if (LanguageList.SelectedItem is not LanguageRow row) { ShowStatus("Виберіть мову в списку."); return; }
        var (name, accusative) = LanguageForms(row.Code);
        if (Store.Value.Languages.Contains(row.Code))
        {
            if (Store.Value.Languages.Count <= 2) { ShowStatus("Залиште принаймні дві активні мови."); return; }
            if (Store.Value.Languages[0] == row.Code)
            {
                // The primary language goes: one of the remaining active languages takes its place.
                var primary = AskPrimaryLanguage(row.Code, Store.Value.Languages.Where(x => x != row.Code).ToList());
                if (primary == null) { ShowStatus(char.ToUpper(name[0]) + name[1..] + " залишається активною й головною."); return; }
                Store.Value.Languages.Remove(row.Code); Store.Value.Languages.Remove(primary); Store.Value.Languages.Insert(0, primary);
                ShowStatus(char.ToUpper(accusative[0]) + accusative[1..] + " вимкнено. Головна мова: " + LanguageName(primary) + ".");
            }
            else { Store.Value.Languages.Remove(row.Code); ShowStatus(char.ToUpper(accusative[0]) + accusative[1..] + " вимкнено."); }
        }
        else { Store.Value.Languages.Add(row.Code); ShowStatus(char.ToUpper(accusative[0]) + accusative[1..] + " увімкнено."); }
        Store.Save(); Host.Engine.Reload(); Host.Keyboard.Reset(); RefreshLanguages(); UpdateQuick();
    }
    private static (string Name, string Accusative) LanguageForms(string code) => code switch { "en" => ("англійська", "англійську"), "uk" => ("українська", "українську"), _ => ("російська", "російську") };
    /// <summary>Asks which of the remaining active languages becomes the primary one; null when the user keeps the current one.</summary>
    private string? AskPrimaryLanguage(string leaving, IReadOnlyList<string> candidates)
    {
        string? chosen = null;
        var dialog = new Window
        {
            Owner = this, Title = "Головна мова", WindowStyle = WindowStyle.None, ResizeMode = ResizeMode.NoResize, ShowInTaskbar = false,
            SizeToContent = SizeToContent.WidthAndHeight, WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Background = (System.Windows.Media.Brush)FindResource("Panel")
        };
        var panel = new StackPanel { Margin = new Thickness(28, 24, 28, 22), Width = 440 };
        panel.Children.Add(new TextBlock { Text = "Оберіть головну мову", FontSize = 19, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 0, 0, 10) });
        panel.Children.Add(new TextBlock
        {
            Text = $"{char.ToUpper(LanguageForms(leaving).Name[0])}{LanguageForms(leaving).Name[1..]} більше не буде активною. Головна мова стоїть першою у списку й має пріоритет, коли неоднозначне слово перетворюється вручну.",
            Style = (Style)FindResource("MutedText"), Margin = new Thickness(0, 0, 0, 20)
        });
        var choices = new WrapPanel();
        foreach (var code in candidates)
        {
            var choice = new Button { Content = LanguageName(code), Style = (Style)FindResource("Primary"), MinWidth = 150, Margin = new Thickness(0, 0, 10, 10) };
            System.Windows.Automation.AutomationProperties.SetName(choice, "Зробити головною: " + LanguageName(code));
            choice.Click += (_, _) => { chosen = code; dialog.DialogResult = true; };
            choices.Children.Add(choice);
        }
        panel.Children.Add(choices);
        panel.Children.Add(new Button { Content = "Скасувати", IsCancel = true, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 10, 0, 0) });
        dialog.Content = new System.Windows.Controls.Border { BorderBrush = (System.Windows.Media.Brush)FindResource("Line"), BorderThickness = new Thickness(1), Child = panel };
        dialog.MouseLeftButtonDown += (_, e) => { if (e.ButtonState == MouseButtonState.Pressed) dialog.DragMove(); };
        dialog.Loaded += (_, _) => (choices.Children[0] as Button)?.Focus();
        return dialog.ShowDialog() == true ? chosen : null;
    }
    private void BuildHotkeys()
    {
        HotkeyFields.Children.Clear(); hotkeyBoxes.Clear();
        var labels = new Dictionary<string, string> { ["convert"] = "Перетворити виділений текст", ["undo"] = "Скасувати останнє виправлення", ["pause"] = "Пауза / продовжити", ["convert_uk"] = "Перетворити на українську", ["convert_en"] = "Перетворити на англійську", ["convert_ru"] = "Перетворити на російську" };
        foreach (var item in labels)
        {
            var row = new Grid { Margin = new Thickness(0, 8, 0, 8) };
            row.ColumnDefinitions.Add(new ColumnDefinition()); row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(235) });
            row.Children.Add(new TextBlock { Text = item.Value, VerticalAlignment = VerticalAlignment.Center });
            var box = new TextBox { Text = Store.Value.Hotkeys.GetValueOrDefault(item.Key, ""), IsReadOnly = true, Style = (Style)FindResource("Watermark"), Tag = "Не призначено", ToolTip = "Натисніть комбінацію. Delete — прибрати, Esc — повернути збережену." };
            System.Windows.Automation.AutomationProperties.SetName(box, item.Value);
            box.GotKeyboardFocus += (_, _) => hotkeys?.Apply(new());
            box.LostKeyboardFocus += (_, _) => ApplyHotkeys();
            box.PreviewKeyDown += CaptureHotkey; Grid.SetColumn(box, 1); row.Children.Add(box); HotkeyFields.Children.Add(row); hotkeyBoxes[item.Key] = box;
        }
    }
    private void CaptureHotkey(object sender, System.Windows.Input.KeyEventArgs e)
    {
        var box = (TextBox)sender; var key = e.Key == Key.System ? e.SystemKey : e.Key;
        if (key == Key.Tab) return;
        e.Handled = true;
        if (key == Key.Escape) { box.Text = Store.Value.Hotkeys.GetValueOrDefault(hotkeyBoxes.First(x => x.Value == box).Key, ""); return; }
        if (key == Key.Delete || key == Key.Back && Keyboard.Modifiers == ModifierKeys.None) { box.Text = ""; return; }
        if (key is Key.LeftCtrl or Key.RightCtrl or Key.LeftAlt or Key.RightAlt or Key.LeftShift or Key.RightShift or Key.LWin or Key.RWin) return;
        var mods = Keyboard.Modifiers;
        if (mods == ModifierKeys.None || (mods & ModifierKeys.Windows) != 0) { ShowStatus("Додайте Ctrl, Alt або Shift."); return; }
        string result = (mods.HasFlag(ModifierKeys.Control) ? "Ctrl+" : "") + (mods.HasFlag(ModifierKeys.Alt) ? "Alt+" : "") + (mods.HasFlag(ModifierKeys.Shift) ? "Shift+" : "");
        result += key switch { Key.Back => "Backspace", >= Key.D0 and <= Key.D9 => ((int)key - (int)Key.D0).ToString(), _ => key.ToString() };
        box.Text = result;
    }
    private void SaveHotkeys_Click(object sender, RoutedEventArgs e)
    {
        var values = hotkeyBoxes.ToDictionary(x => x.Key, x => x.Value.Text);
        var duplicate = values.Values.Where(x => x.Length > 0).GroupBy(x => x, StringComparer.OrdinalIgnoreCase).FirstOrDefault(x => x.Count() > 1);
        if (duplicate != null) { ShowStatus("Комбінація повторюється: " + duplicate.Key); return; }
        Store.Value.Hotkeys = values; Store.Save(); ApplyHotkeys(); ShowStatus(("Гарячі клавіші збережено. " + HotkeyWarning.Text).Trim());
    }
    private void ResetHotkeys_Click(object sender, RoutedEventArgs e) { Store.Value.Hotkeys = new Preferences().Hotkeys; Store.Save(); BuildHotkeys(); ApplyHotkeys(); if (HotkeyWarning.Text.Length == 0) ShowStatus("Стандартні гарячі клавіші відновлено."); }
    private void ApplyHotkeys()
    {
        var failures = hotkeys?.Apply(Store.Value.Hotkeys) ?? new();
        HotkeyWarning.Text = failures.Count == 0 ? "" : "Комбінації зайняті іншою програмою або некоректні: " + string.Join(", ", failures);
        if (failures.Count > 0) ShowStatus(HotkeyWarning.Text);
    }
    private async void HandleHotkey(string action)
    {
        // Capturing a shortcut in our editor must not execute that shortcut.
        if (IsActive && hotkeyBoxes.Values.Any(x => x.IsKeyboardFocused)) return;
        if (action == "pause") { if (Host.Keyboard.Running) Host.Keyboard.Pause(); else Host.Keyboard.Resume(); Store.Save(); RefreshStatus(); }
        else if (action == "undo") await Host.Keyboard.UndoAsync();
        else await Host.Keyboard.ConvertSelectionAsync(action == "convert" ? null : action[8..]);
    }
    public void DisposeHotkeys() { quickRequest?.Cancel(); apiTest?.Cancel(); localModelTest?.Cancel(); localPreparation?.Cancel(); localSetup?.Cancel(); hotkeys?.Dispose(); }
    private void RuleSearch_Changed(object sender, TextChangedEventArgs e) { if (ready) RefreshRules(); }
    public void RefreshRules()
    {
        if (!ready) return;
        string query = RuleSearch.Text.Trim();
        PairsList.ItemsSource = Store.Pairs.Select(x => new PairRow(x.Key, x.Value?.GetValue<string>() ?? "")).Where(x => x.Label.Contains(query, StringComparison.OrdinalIgnoreCase)).OrderBy(x => x.Source).ToList();
        NeverList.ItemsSource = Store.Never.Select(x => x!.GetValue<string>()).Where(x => x.Contains(query, StringComparison.OrdinalIgnoreCase)).OrderBy(x => x).ToList();
        RulesCount.Text = $"{Store.Pairs.Count} {Plural(Store.Pairs.Count, "правило", "правила", "правил")} заміни · {Store.Never.Count} {Plural(Store.Never.Count, "слово-виняток", "слова-винятки", "слів-винятків")}";
    }
    private void SaveRules() { Store.SaveRules(); Host.Engine.Reload(); Host.Keyboard.Reset(); RefreshRules(); UpdateQuick(); ShowStatus("Правила збережено й застосовано."); }
    private void AddPair_Click(object sender, RoutedEventArgs e)
    {
        var source = PairSource.Text.Trim(); var target = PairTarget.Text.Trim();
        if (source.Length is 0 or > 256 || target.Length is 0 or > 256) { ShowStatus("Заповніть обидва поля: від 1 до 256 символів."); return; }
        Store.Pairs[source.ToLowerInvariant()] = target;
        Store.UserPairs[source.ToLowerInvariant()] = target;
        Store.Rules["manual_overrides"]!["exact_pairs_meta"]?.AsObject().Remove(source.ToLowerInvariant());
        SaveRules(); PairSource.Clear(); PairTarget.Clear();
    }
    private void RemovePair_Click(object sender, RoutedEventArgs e)
    {
        if (PairsList.SelectedItem is not PairRow) { ShowStatus("Виберіть правило в списку, щоб видалити його."); return; }
        if (PairsList.SelectedItem is PairRow row) { Store.Pairs.Remove(row.Source); Store.UserPairs.Remove(row.Source); Store.Rules["manual_overrides"]!["exact_pairs_meta"]?.AsObject().Remove(row.Source); SaveRules(); }
    }
    private void AddNever_Click(object sender, RoutedEventArgs e)
    {
        if (NeverWord.Text.Trim().Length is 0 or > 256) { ShowStatus("Введіть слово від 1 до 256 символів."); return; }
        Store.AddNever(NeverWord.Text); SaveRules(); NeverWord.Clear();
    }
    private void RemoveNever_Click(object sender, RoutedEventArgs e)
    {
        if (NeverList.SelectedItem is not string) { ShowStatus("Виберіть слово в списку, щоб видалити його."); return; }
        if (NeverList.SelectedItem is string word) { var item = Store.Never.FirstOrDefault(x => x?.GetValue<string>() == word); if (item != null) Store.Never.Remove(item); SaveRules(); }
    }
    private void Import_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog { Filter = "Правила Switchcraft (*.json)|*.json" };
        if (dialog.ShowDialog(this) != true) return;
        try { Store.Import(dialog.FileName); SaveRules(); } catch (Exception ex) { ShowStatus("Імпорт не виконано: " + ex.Message); }
    }
    private void Export_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new SaveFileDialog { Filter = "Правила Switchcraft (*.json)|*.json", FileName = "switchcraft-rules.json" };
        if (dialog.ShowDialog(this) != true) return;
        try { Store.Export(dialog.FileName); ShowStatus("Правила експортовано."); } catch (Exception ex) { ShowStatus("Експорт не виконано: " + ex.Message); }
    }
    public void RefreshExcluded()
    {
        ExcludedList.ItemsSource = Store.Value.ExcludedApps.OrderBy(x => x).ToList();
        ExcludedEmpty.Visibility = Store.Value.ExcludedApps.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
    }
    private void AddExcluded(string value)
    {
        string name = Path.GetFileName(value.Trim());
        if (!name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) || name.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 || name.Length < 5) { ShowStatus("Укажіть ім’я виконуваного файлу з розширенням .exe."); return; }
        if (!Store.Value.ExcludedApps.Contains(name, StringComparer.OrdinalIgnoreCase)) Store.Value.ExcludedApps.Add(name);
        Store.Save(); Host.Keyboard.Reset(); RefreshExcluded(); ExcludedName.Clear(); ShowStatus("Виняток додано: " + name);
    }
    private void AddExcluded_Click(object sender, RoutedEventArgs e) => AddExcluded(ExcludedName.Text);
    private void BrowseExcluded_Click(object sender, RoutedEventArgs e) { var dialog = new OpenFileDialog { Filter = "Програми Windows (*.exe)|*.exe" }; if (dialog.ShowDialog(this) == true) AddExcluded(dialog.FileName); }
    private void RemoveExcluded_Click(object sender, RoutedEventArgs e)
    {
        if (ExcludedList.SelectedItem is not string value) { ShowStatus("Виберіть програму в списку, щоб видалити її."); return; }
        Store.Value.ExcludedApps.Remove(value); Store.Save(); Host.Keyboard.Reset(); RefreshExcluded(); ShowStatus("Виняток видалено: " + value);
    }
    private void Preference_Click(object sender, RoutedEventArgs e)
    {
        Store.Value.StartMinimized = StartMinimized.IsChecked == true; Store.Value.SwitchLayout = SwitchLayoutCheck.IsChecked == true;
        Store.Value.LearningMode = LearningCheck.IsChecked == true; Store.Value.KeepHistory = HistoryCheck.IsChecked == true;
        if (!Store.Value.KeepHistory) Host.History.Clear(); Store.Save(); RefreshStatus(); ShowStatus("Налаштування збережено.");
    }
    private void RefreshAiSettings()
    {
        TypeSafeMode.IsChecked = Host.Detection.UsesCloud;
        LayaMode.IsChecked = Host.Detection.UsesLocalModel;
        AiOptions.Visibility = Host.Detection.UsesCloud ? Visibility.Visible : Visibility.Collapsed;
        LocalModelOptions.Visibility = Host.Detection.UsesLocalModel ? Visibility.Visible : Visibility.Collapsed;
        AiModeDescription.Text = Host.Detection.UsesCloud
            ? Host.Detection.Keys.HasKey ? "TypeSafe визначає мову через API, а Switchcraft перевіряє й виконує заміну." : "Збережіть ключ API для виправлення через хмару."
            : "Laya визначає мову локально за допомогою штучного інтелекту.";
        ApiKeyHint.Text = Host.Detection.Keys.HasKey ? "Ключ збережено. Залиште поле порожнім, щоб не змінювати його." : "Вставте ключ зі свого облікового запису TypeSafe та збережіть підключення.";
    }
    private void CancelAiWork()
    {
        quickRequest?.Cancel(); apiTest?.Cancel(); localModelTest?.Cancel(); localPreparation?.Cancel();
        apiTest = null; localModelTest = null;
        TestAiButton.IsEnabled = true; TestLocalModelButton.IsEnabled = localSetup == null;
        Host.Keyboard.Reset(); Host.Detection.Reset();
    }
    private void DetectionMode_Checked(object sender, RoutedEventArgs e)
    {
        if (!ready || sender is not RadioButton { Tag: string mode } || mode is not ("typesafe" or "laya")) return;
        if (Store.Value.DetectionMode == mode) return;
        Store.Value.DetectionMode = mode;
        CancelAiWork(); Store.Save(); RefreshAiSettings(); RefreshStatus();
        _ = PrepareLocalModelAsync();
        if (mode == "laya" && localSetup == null && !LayaSetup.IsPrepared(Store.Value.LayaModelPath)) LocalModelConfigStatus.Text = LayaSetupText;
        // Switching mode itself does not send the example text. The next live edit does.
        QuickReason.Text = Host.Detection.ModeLabel + " · введіть слово для перевірки";
        ShowStatus("Режим: " + Host.Detection.ModeLabel + ".");
    }
    private void SaveAi_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            string model = TypeSafeModelBox.Text.Trim();
            if (!Regex.IsMatch(model, @"^[A-Za-z0-9][A-Za-z0-9._/-]{0,79}$")) { AiConfigStatus.Text = "Введіть назву моделі, наприклад jev-latest."; return; }
            if (TypeSafeKey.Password.Length > 0) Host.Detection.Keys.Save(TypeSafeKey.Password);
            TypeSafeKey.Clear(); Store.Value.TypeSafeModel = model;
            CancelAiWork(); Store.Save(); RefreshAiSettings();
            AiConfigStatus.Text = Host.Detection.Keys.HasKey ? "Підключення збережено. Можна перевірити API." : "Ключ не задано.";
        }
        catch { AiConfigStatus.Text = "Не вдалося зберегти підключення. Перевірте ключ і доступ до теки налаштувань."; }
    }
    private async void TestAi_Click(object sender, RoutedEventArgs e)
    {
        if (!Host.Detection.Keys.HasKey || TypeSafeKey.Password.Length > 0 || TypeSafeModelBox.Text.Trim() != Store.Value.TypeSafeModel)
        { AiConfigStatus.Text = "Спочатку збережіть ключ і модель."; return; }
        apiTest?.Cancel(); using var cancellation = new CancellationTokenSource(); apiTest = cancellation;
        TestAiButton.IsEnabled = false; AiConfigStatus.Text = "Перевіряємо API на тестовому слові…";
        try
        {
            var answer = await Host.Detection.TestAsync(cancellation.Token);
            if (!cancellation.IsCancellationRequested) AiConfigStatus.Text = answer != null ? "API підключено · " + (!answer.Certain ? "модель не впевнена" : answer.Choice == "keep" ? "тестове слово залишено без змін" : "тест: " + LanguageName(answer.Choice)) : Host.Detection.Api.Status;
        }
        catch (OperationCanceledException) { if (apiTest == cancellation) AiConfigStatus.Text = "Перевірку скасовано."; }
        catch { if (apiTest == cancellation) AiConfigStatus.Text = "Не вдалося перевірити API."; }
        finally { if (apiTest == cancellation) { apiTest = null; TestAiButton.IsEnabled = true; } }
    }
    private void ClearAiKey_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            CancelAiWork(); Host.Detection.Keys.Clear(); TypeSafeKey.Clear();
            if (Host.Detection.UsesCloud) Store.Value.DetectionMode = "laya";
            Store.Save(); RefreshAiSettings(); RefreshStatus();
            AiConfigStatus.Text = "Ключ прибрано."; ShowStatus("Ключ TypeSafe прибрано. Режим: " + Host.Detection.ModeLabel + ".");
        }
        catch { AiConfigStatus.Text = "Не вдалося прибрати ключ."; }
    }
    private void BrowseLocalModel_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            string selected = Environment.ExpandEnvironmentVariables(LayaModelPathBox.Text.Trim());
            var dialog = new OpenFolderDialog
            {
                Title = "Тека локальної моделі Laya", Multiselect = false,
                InitialDirectory = Directory.Exists(selected) ? selected : Store.DirectoryPath
            };
            if (dialog.ShowDialog(this) != true) return;
            LayaModelPathBox.Text = dialog.FolderName;
            LocalModelConfigStatus.Text = "Теку обрано. Збережіть шлях і перевірте модель.";
        }
        catch { LocalModelConfigStatus.Text = "Не вдалося відкрити вибір теки. Введіть шлях у полі вище."; }
    }
    private void SaveLocalModel_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            string selected = Environment.ExpandEnvironmentVariables(LayaModelPathBox.Text.Trim().Trim('"'));
            if (!Path.IsPathFullyQualified(selected)) { LocalModelConfigStatus.Text = "Укажіть повний шлях до теки моделі."; return; }
            string path = Path.GetFullPath(selected);
            if (File.Exists(path)) { LocalModelConfigStatus.Text = "За цим шляхом розташований файл. Укажіть теку моделі."; return; }
            Store.Value.LayaModelPath = path;
            CancelAiWork(); Store.Save(); LayaModelPathBox.Text = path; RefreshStatus();
            _ = PrepareLocalModelAsync();
            LocalModelConfigStatus.Text = LayaSetup.HasModel(path) ? "Шлях збережено. Перевірте готовність моделі." : "Шлях збережено. Натисніть «Завантажити й підготувати», щоб встановити Laya.";
        }
        catch { LocalModelConfigStatus.Text = "Не вдалося зберегти шлях. Перевірте теку й доступ до налаштувань."; }
    }
    private async void TestLocalModel_Click(object sender, RoutedEventArgs e)
    {
        if (localSetup != null) return;
        if (!string.Equals(LayaModelPathBox.Text.Trim(), Store.Value.LayaModelPath, StringComparison.OrdinalIgnoreCase))
        { LocalModelConfigStatus.Text = "Спочатку збережіть шлях до моделі."; return; }
        Host.Detection.Local.RetryLoading();
        localModelTest?.Cancel(); using var cancellation = new CancellationTokenSource(); localModelTest = cancellation;
        TestLocalModelButton.IsEnabled = false;
        LocalModelConfigStatus.Text = "Завантажуємо Laya й перевіряємо тестове слово…";
        try
        {
            var answer = await Host.Detection.TestLocalAsync(cancellation.Token);
            if (!cancellation.IsCancellationRequested)
                LocalModelConfigStatus.Text = answer != null ? "Laya працює локально · "
                    + (!answer.Certain ? "модель не впевнена" : answer.Choice == "keep" ? "тестове слово залишено без змін" : "тест: " + LanguageName(answer.Choice))
                    : Host.Detection.Local.Status;
        }
        catch (OperationCanceledException) { if (localModelTest == cancellation) LocalModelConfigStatus.Text = "Перевірку скасовано."; }
        catch { if (localModelTest == cancellation) LocalModelConfigStatus.Text = "Не вдалося перевірити модель. Перевірте теку та стан Laya."; }
        finally
        {
            if (localModelTest == cancellation) { localModelTest = null; TestLocalModelButton.IsEnabled = true; }
            RefreshStatus();
        }
    }
    private async Task PrepareLocalModelAsync()
    {
        if (!Host.Detection.UsesLocalModel || localSetup != null) return;
        localPreparation?.Cancel();
        using var cancellation = new CancellationTokenSource(); localPreparation = cancellation;
        LocalModelServiceStatus.Text = "Завантажуємо Laya в пам’ять…";
        try { await Host.Detection.PrepareAsync(cancellation.Token); }
        catch (OperationCanceledException) { }
        catch { if (!cancellation.IsCancellationRequested) LocalModelServiceStatus.Text = "Не вдалося підготувати Laya. Перевірте компоненти."; }
        finally { if (localPreparation == cancellation) { localPreparation = null; RefreshStatus(); } }
    }
    private void LayaDevice_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (!ready || localSetup != null || LayaDeviceBox.SelectedItem is not ComboBoxItem { Tag: string device } || Store.Value.LayaDevice == device) return;
        Store.Value.LayaDevice = device; CancelAiWork(); Store.Save(); RefreshStatus();
        LocalModelConfigStatus.Text = "Пристрій збережено. За потреби натисніть кнопку підготовки компонентів.";
        _ = PrepareLocalModelAsync();
    }
    private void SetSetupControls(bool busy)
    {
        LayaModelPathBox.IsEnabled = LayaDeviceBox.IsEnabled = BrowseLocalModelButton.IsEnabled = SaveLocalModelButton.IsEnabled = TestLocalModelButton.IsEnabled = SetupLocalModelButton.IsEnabled = !busy;
        CancelLocalSetupButton.Visibility = LocalSetupProgress.Visibility = busy ? Visibility.Visible : Visibility.Collapsed;
        CancelLocalSetupButton.IsEnabled = busy;
        LocalSetupProgress.IsIndeterminate = busy;
        if (busy) SetupLocalModelButton.Content = "Підготовка…";
    }
    private const string LayaMissingText = "Laya ще не встановлена на цьому комп’ютері. Натисніть «Завантажити й підготувати»: потрібен інтернет, модель ≈600 МБ і компоненти ≈1 ГБ. ";
    private string LayaSetupText => LayaSetup.IsOutdated(Store.Value.LayaModelPath)
        ? "Доступна новіша модель Laya (" + LayaSetup.Release + "). Натисніть «Оновити модель»: буде завантажено ≈600 МБ, а попередню модель після цього буде видалено. Поки що працює встановлена модель."
        : LayaMissingText;
    /// <summary>Opens the Laya section and prepares missing components; used after the installer's optional Laya step.</summary>
    public void BeginLayaSetup()
    {
        Host.ShowShell(); SettingsNav.IsChecked = true;
        if (Store.Value.DetectionMode != "laya")
        {
            Store.Value.DetectionMode = "laya";
            CancelAiWork(); Store.Save(); RefreshAiSettings(); RefreshStatus();
        }
        LayaModelPathBox.Text = Store.Value.LayaModelPath;
        Dispatcher.InvokeAsync(() => LocalModelOptions.BringIntoView(), System.Windows.Threading.DispatcherPriority.Loaded);
        _ = RunLocalSetupAsync();
    }
    private void SetupLocalModel_Click(object sender, RoutedEventArgs e)
    {
        if (!string.Equals(LayaModelPathBox.Text.Trim(), Store.Value.LayaModelPath, StringComparison.OrdinalIgnoreCase))
        { LocalModelConfigStatus.Text = "Спочатку збережіть шлях до моделі."; return; }
        _ = RunLocalSetupAsync();
    }
    private async Task RunLocalSetupAsync()
    {
        if (localSetup != null) return;
        using var cancellation = new CancellationTokenSource(); localSetup = cancellation;
        CancelAiWork(); Host.Detection.Local.SetPreparing(true); SetSetupControls(true);
        LocalModelConfigStatus.Text = "Перевіряємо готовність Laya…";
        string modelPath = Store.Value.LayaModelPath, device = Store.Value.LayaDevice;
        var progress = new Progress<LayaSetupProgress>(value =>
        {
            if (localSetup != cancellation || cancellation.IsCancellationRequested) return;
            LocalModelConfigStatus.Text = value.Message;
            LocalSetupProgress.IsIndeterminate = !value.Percent.HasValue;
            if (value.Percent.HasValue) LocalSetupProgress.Value = Math.Clamp(value.Percent.Value, 0, 100);
        });
        try
        {
            using var setup = new LayaSetup();
            await Task.Run(() => setup.InstallAsync(modelPath, device, progress, cancellation.Token));
            if (!cancellation.IsCancellationRequested) LocalModelConfigStatus.Text = "Компоненти Laya готові. Модель завантажується в пам’ять.";
        }
        catch (OperationCanceledException) { LocalModelConfigStatus.Text = "Підготовку скасовано. Завантажену частину збережено; можна продовжити цією ж кнопкою."; }
        catch (Exception ex) { LocalModelConfigStatus.Text = "Підготовку не завершено: " + ex.Message; }
        finally
        {
            Host.Detection.Local.SetPreparing(false);
            if (localSetup == cancellation) localSetup = null;
            SetSetupControls(false); RefreshStatus();
            if (!cancellation.IsCancellationRequested && Host.Detection.UsesLocalModel) _ = PrepareLocalModelAsync();
        }
    }
    private void CancelLocalSetup_Click(object sender, RoutedEventArgs e)
    {
        localSetup?.Cancel(); CancelLocalSetupButton.IsEnabled = false;
        LocalModelConfigStatus.Text = "Зупиняємо підготовку…";
    }
    private void Sensitivity_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (!ready) return;
        Store.Value.Sensitivity = (SensitivityBox.SelectedItem as ComboBoxItem)?.Tag?.ToString() ?? "balanced";
        Store.Save(); UpdateQuick();
    }
    private void Startup_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            using var key = Registry.CurrentUser.CreateSubKey(@"Software\Microsoft\Windows\CurrentVersion\Run");
            if (LaunchAtLogin.IsChecked == true) key.SetValue(StartupValueName, "\"" + Environment.ProcessPath + "\" --minimized"); else key.DeleteValue(StartupValueName, false);
            ShowStatus("Налаштування автозапуску збережено.");
        }
        catch (Exception ex) { LaunchAtLogin.IsChecked = false; ShowStatus("Не вдалося змінити автозапуск: " + ex.Message); }
    }
    private void OpenSettingsFolder_Click(object sender, RoutedEventArgs e) => Process.Start(new ProcessStartInfo(Store.DirectoryPath) { UseShellExecute = true });
    private void Pause_Click(object sender, RoutedEventArgs e)
    {
        if (Host.Keyboard.SessionPaused || Host.Keyboard.PausedUntil > DateTime.Now) { Host.Keyboard.Resume(); Store.Save(); RefreshStatus(); ShowStatus("Автоматичне виправлення ввімкнено."); return; }
        Host.Keyboard.Pause(15); RefreshStatus(); ShowStatus("Виправлення призупинено на 15 хвилин.");
    }
    private void Quit_Click(object sender, RoutedEventArgs e) => Host.Quit();
    private async void CheckUpdate_Click(object sender, RoutedEventArgs e)
    {
        CheckUpdateButton.IsEnabled = false;
        UpdateStatus.Text = "Перевіряємо…";
        try
        {
            using var checker = new UpdateChecker();
            var info = await checker.CheckAsync();
            if (info.Error != null)
            {
                UpdateStatus.Text = "Не вдалося перевірити оновлення: " + info.Error;
            }
            else if (info.UpdateAvailable && info.LatestVersion != null)
            {
                UpdateStatus.Text = $"Доступна нова версія: {info.LatestVersion}";
                var result = MessageBox.Show(
                    $"Доступна нова версія Switchcraft AI {info.LatestVersion}.\n\nВідкрити сторінку завантаження?",
                    "Оновлення", MessageBoxButton.YesNo, MessageBoxImage.Information);
                if (result == MessageBoxResult.Yes && info.ReleaseUrl != null)
                    UpdateChecker.OpenReleasePage(info.ReleaseUrl);
            }
            else
            {
                UpdateStatus.Text = $"У вас найновіша версія ({UpdateChecker.CurrentVersion}).";
            }
        }
        catch (Exception ex)
        {
            UpdateStatus.Text = "Помилка: " + ex.Message;
        }
        finally
        {
            CheckUpdateButton.IsEnabled = true;
        }
    }
}
