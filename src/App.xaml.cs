using System;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Threading;
using System.Windows;
using System.Windows.Threading;
using Forms = System.Windows.Forms;

namespace Switchcraft;

public partial class App : Application
{
    public static new App Current => (App)Application.Current;
    public SettingsStore Store { get; private set; } = null!;
    public NativeEngine Engine { get; private set; } = null!;
    public LanguageDetection Detection { get; private set; } = null!;
    public KeyboardService Keyboard { get; private set; } = null!;
    public ObservableCollection<HistoryItem> History { get; } = new();
    public int SessionCorrections { get; private set; }
    private Forms.NotifyIcon? tray;
    private Mutex? mutex;
    private EventWaitHandle? showSignal, setupSignal;
    private RegisteredWaitHandle? showWait, setupWait;
    private bool ownsMutex;
    public MainWindow Shell { get; private set; } = null!;
    private DispatcherTimer? timer;
    private IntPtr lastExternalWindow;
    protected override void OnStartup(StartupEventArgs e)
    {
        // Keep text and rounded cards stable on remote desktops and virtual GPUs.
        System.Windows.Media.RenderOptions.ProcessRenderMode = System.Windows.Interop.RenderMode.SoftwareOnly;
        base.OnStartup(e);
        if (e.Args.Contains("--laya-input-self-test"))
        {
            InputIntegration.Run(e.Args.SkipWhile(x => x != "--laya-input-self-test").Skip(1).FirstOrDefault() ?? Path.Combine(AppContext.BaseDirectory, "laya-input-test.json"), layaShort: true);
            return;
        }
        if (e.Args.Contains("--context-input-self-test"))
        {
            InputIntegration.Run(e.Args.SkipWhile(x => x != "--context-input-self-test").Skip(1).FirstOrDefault() ?? Path.Combine(AppContext.BaseDirectory, "context-input-test.json"), contextOnly: true);
            return;
        }
        if (e.Args.Contains("--live-short-input-test"))
        {
            InputIntegration.Run(e.Args.SkipWhile(x => x != "--live-short-input-test").Skip(1).FirstOrDefault() ?? Path.Combine(AppContext.BaseDirectory, "live-short-input-test.json"), liveShort: true);
            return;
        }
        if (e.Args.Contains("--ai-input-self-test"))
        {
            InputIntegration.Run(e.Args.SkipWhile(x => x != "--ai-input-self-test").Skip(1).FirstOrDefault() ?? Path.Combine(AppContext.BaseDirectory, "ai-input-test.json"), aiOnly: true);
            return;
        }
        if (e.Args.Contains("--input-self-test"))
        {
            InputIntegration.Run(e.Args.SkipWhile(x => x != "--input-self-test").Skip(1).FirstOrDefault() ?? Path.Combine(AppContext.BaseDirectory, "input-test.json"));
            return;
        }
        if (e.Args.Contains("--self-test"))
        {
            var file = e.Args.SkipWhile(x => x != "--self-test").Skip(1).FirstOrDefault() ?? Path.Combine(AppContext.BaseDirectory, "self-test.json");
            int result = SelfTests.Run(file); Shutdown(result); return;
        }
        bool setupLaya = e.Args.Contains("--setup-laya");
        mutex = new Mutex(true, "Local\\Switchcraft.Desktop", out bool created); ownsMutex = created;
        if (!created)
        {
            try { EventWaitHandle.OpenExisting(setupLaya ? "Local\\Switchcraft.SetupLaya" : "Local\\Switchcraft.Show").Set(); } catch { }
            Shutdown(); return;
        }
        try
        {
            Store = new SettingsStore(); Engine = new NativeEngine(Store); Detection = new LanguageDetection(Engine, Store); Keyboard = new KeyboardService(Engine, Store, detection: Detection);
            string? diagnosticPath = e.Args.SkipWhile(x => x != "--input-diagnostics").Skip(1).FirstOrDefault();
            Keyboard.DiagnosticsEnabled = diagnosticPath != null;
            Shell = new MainWindow(); MainWindow = Shell;
            Keyboard.Corrected += (result, app) => Dispatcher.BeginInvoke(() =>
            {
                SessionCorrections++;
                if (Store.Value.KeepHistory)
                {
                    History.Insert(0, new(DateTime.Now, result.Original, result.Text, result.Target, app));
                    while (History.Count > 50) History.RemoveAt(History.Count - 1);
                }
                Shell.RefreshStatus();
            });
            Keyboard.Status += message => Dispatcher.BeginInvoke(() => Shell.ShowStatus(message));
            Keyboard.Notice += message => Dispatcher.BeginInvoke(() => { Shell.ShowStatus(message); tray?.ShowBalloonTip(8000, "Switchcraft", message, Forms.ToolTipIcon.Info); });
            Keyboard.LearnWord += word => Dispatcher.BeginInvoke(() =>
            {
                try { Store.AddNever(word); Engine.Reload(); Shell.RefreshRules(); Shell.ShowStatus("Слово додано до винятків після скасування."); }
                catch (Exception ex) { Shell.ShowStatus("Не вдалося зберегти правило: " + ex.Message); }
            });
            CreateTray();
            Shell.Show();
            if (!setupLaya && (Store.Value.StartMinimized || e.Args.Contains("--minimized"))) Shell.Hide();
            Keyboard.Start();
            if (setupLaya) Shell.BeginLayaSetup(); else _ = Detection.PrepareAsync();
            showSignal = new EventWaitHandle(false, EventResetMode.AutoReset, "Local\\Switchcraft.Show");
            showWait = ThreadPool.RegisterWaitForSingleObject(showSignal, (_, _) => Dispatcher.BeginInvoke(ShowShell), null, -1, false);
            // The installer's optional Laya step reaches an already running instance through this signal.
            setupSignal = new EventWaitHandle(false, EventResetMode.AutoReset, "Local\\Switchcraft.SetupLaya");
            setupWait = ThreadPool.RegisterWaitForSingleObject(setupSignal, (_, _) => Dispatcher.BeginInvoke(Shell.BeginLayaSetup), null, -1, false);
            timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
            timer.Tick += (_, _) =>
            {
                var hwnd = Win32.GetForegroundWindow();
                var app = Win32.AppName(hwnd);
                if (hwnd != IntPtr.Zero && app.Length > 0 && app is not "Switchcraft.exe" and not "explorer.exe") lastExternalWindow = hwnd;
                Shell.RefreshStatus();
                if (tray != null) tray.Text = Keyboard.Running ? "Switchcraft • Активний" : "Switchcraft • На паузі";
                if (diagnosticPath != null)
                    try { File.WriteAllText(diagnosticPath, System.Text.Json.JsonSerializer.Serialize(Keyboard.InputDiagnostics())); } catch (IOException) { }
            };
            timer.Start();
            if (Store.LoadWarning != null) Shell.ShowStatus(Store.LoadWarning);
            DispatcherUnhandledException += (_, args) => { Shell.ShowStatus("Дію не завершено: " + args.Exception.Message); args.Handled = true; };
        }
        catch (Exception ex)
        {
            MessageBox.Show("Switchcraft не вдалося запустити.\n\n" + ex.Message, "Switchcraft", MessageBoxButton.OK, MessageBoxImage.Error);
            Shutdown(1);
        }
    }
    private void CreateTray()
    {
        using var stream = GetResourceStream(new Uri("pack://application:,,,/Assets/Switchcraft.ico")).Stream;
        tray = new Forms.NotifyIcon { Icon = new Icon(stream), Text = "Switchcraft", Visible = true };
        var menu = new Forms.ContextMenuStrip { BackColor = Color.FromArgb(25, 28, 35), ForeColor = Color.FromArgb(235, 240, 245), ShowImageMargin = false };
        void Add(string title, Action action) => menu.Items.Add(title, null, (_, _) => Dispatcher.BeginInvoke(action));
        Add("Відкрити Switchcraft", ShowShell);
        Add("Увімкнути / призупинити", () => { if (Keyboard.Running) Keyboard.Pause(); else Keyboard.Resume(); Store.Save(); Shell.RefreshStatus(); });
        Add("Пауза на 15 хвилин", () => { Keyboard.Pause(15); Shell.RefreshStatus(); });
        menu.Items.Add(new Forms.ToolStripSeparator());
        Add("Перетворити виділення", () => RestoreAndConvert(null));
        Add("Перетворити → українська", () => RestoreAndConvert("uk"));
        Add("Перетворити → англійська", () => RestoreAndConvert("en"));
        Add("Перетворити → російська", () => RestoreAndConvert("ru"));
        Add("Виняток для поточного застосунку", () =>
        {
            var name = Win32.AppName(lastExternalWindow);
            if (string.IsNullOrWhiteSpace(name)) return;
            if (!Store.Value.ExcludedApps.RemoveAll(x => string.Equals(x, name, StringComparison.OrdinalIgnoreCase)).Equals(0)) Shell.ShowStatus("Виправлення ввімкнено для " + name);
            else { Store.Value.ExcludedApps.Add(name); Shell.ShowStatus("Виправлення вимкнено для " + name); }
            Store.Save(); Shell.RefreshExcluded();
        });
        menu.Items.Add(new Forms.ToolStripSeparator()); Add("Вийти", Quit);
        tray.ContextMenuStrip = menu; tray.DoubleClick += (_, _) => Dispatcher.BeginInvoke(ShowShell);
    }
    private async void RestoreAndConvert(string? language)
    {
        if (lastExternalWindow == IntPtr.Zero) return;
        SetForegroundWindow(lastExternalWindow);
        await System.Threading.Tasks.Task.Delay(250);
        await Keyboard.ConvertSelectionAsync(language);
    }
    public void ShowShell() { Shell.Show(); Shell.WindowState = WindowState.Normal; Shell.Activate(); }
    public void Quit() { Store?.Save(); Shutdown(); }
    protected override void OnExit(ExitEventArgs e)
    {
        timer?.Stop(); showWait?.Unregister(null); showSignal?.Dispose(); setupWait?.Unregister(null); setupSignal?.Dispose();
        Shell?.DisposeHotkeys(); Keyboard?.Dispose(); Detection?.Dispose(); Engine?.Dispose();
        if (tray != null) { tray.Visible = false; tray.Dispose(); }
        if (ownsMutex) mutex?.ReleaseMutex(); mutex?.Dispose();
        base.OnExit(e);
    }
    [System.Runtime.InteropServices.DllImport("user32.dll")] private static extern bool SetForegroundWindow(IntPtr window);
}
