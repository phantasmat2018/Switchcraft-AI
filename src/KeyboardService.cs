using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using Forms = System.Windows.Forms;

namespace Switchcraft;

/// <summary>Transient word buffer. Hooks never call the engine, UIA, clipboard or editor messages.</summary>
public sealed class KeyboardService : IDisposable
{
    private readonly NativeEngine engine;
    private readonly SettingsStore settings;
    private readonly LanguageDetection? detection;
    private readonly Queue<string> recentWords = new();
    private CancellationTokenSource? activeAnalysis;
    private readonly object gate = new();
    private readonly Thread thread;
    private readonly Win32.HookProc keyboardProc, mouseProc;
    private readonly Win32.WinEventProc focusProc;
    private IntPtr keyboardHook, mouseHook, focusHook, foregroundHook;
    private uint threadId;
    private readonly StringBuilder word = new();
    private readonly TypingContext languageContext = new();
    private readonly EditorAccess editor = new();
    private readonly SemaphoreSlim operation = new(1, 1);
    private readonly Channel<WordJob> jobs = Channel.CreateBounded<WordJob>(new BoundedChannelOptions(32) { SingleReader = true, FullMode = BoundedChannelFullMode.DropOldest });
    private (IntPtr Window, IntPtr Focus) tracked, permitted;
    private bool allowed, incomplete, disposed;
    // A click or focus change starts a new check of the field. Until it decides, typed keys are kept (never
    // processed), so the first word typed right after a click is not lost; a denial discards them.
    private bool awaitingPermission;
    private int probeKick;
    private readonly HashSet<IntPtr> reportedLayouts = new();
    private volatile bool hookActive;
    private long sequence, contextEpoch, permissionTime, analysisStarted, wordOrdinal, lastInputTime, lastIdleSequence;
    private int probeBusy;
    private long diagnosticKeys, diagnosticAccepted, diagnosticJobs, diagnosticCaptures, diagnosticReplacements;
    private readonly Queue<string> diagnosticStages = new();
    public bool DiagnosticsEnabled { get; set; }
    private void Trace(string stage) { if (DiagnosticsEnabled) lock (gate) { diagnosticStages.Enqueue(stage); while (diagnosticStages.Count > 96) diagnosticStages.Dequeue(); } }
    private void TraceDecision(string stage, string reason)
    {
        if (!DiagnosticsEnabled) return;
        string code = reason is "known_original" or "cyrillic_dictionary" or "cyrillic_context" or "layout_dictionary"
            or "ambiguous_dictionary_candidates" or "ai_keep" or "typesafe_ai" or "ai_uncertain" or "ai_fallback"
            or "laya_ai" or "local_ai_keep" or "local_ai_uncertain" or "local_ai_fallback"
            or "user_pair" or "user_exception" or "protected_pattern" or "web_address" or "length_limit" or "engine_stopped" or "short_context" or "context_layout"
            ? reason : "native_result";
        Trace(stage + ":" + code);
    }
    public object InputDiagnostics() { lock (gate) return new { running = Running, allowed, source = Win32.LayoutLanguage(tracked.Window), buffered = word.Length, incomplete, keys = diagnosticKeys, accepted = diagnosticAccepted, jobs = diagnosticJobs, captures = diagnosticCaptures, replacements = diagnosticReplacements, stages = diagnosticStages.ToArray() }; }
    // From selecting a word until its paste is confirmed, user keys are held in order and replayed
    // afterwards: otherwise a fast next keystroke lands between Select() and Ctrl+V and overwrites the
    // selected word (Chromium applies UIA selections asynchronously, which widens that window).
    private readonly object holdGate = new();
    private readonly List<(Win32.KeyData Key, int Message)> heldKeys = new();
    private bool holdingKeys;
    private const int MaxHoldMs = 400;
    // Replayed keys carry their own tag: processed as ordinary typing, never held again.
    private static readonly UIntPtr ReplayTag = new(0x53574352);
    /// <summary>Starts holding user keys; returns false when a hold is already active (its owner releases it).</summary>
    private bool HoldKeys() { lock (holdGate) { if (holdingKeys) return false; heldKeys.Clear(); holdingKeys = true; return true; } }
    internal bool HoldingKeys => Volatile.Read(ref holdingKeys);
    /// <summary>Replays held keys in order and returns their count. Holding ends only when nothing is left:
    /// a key typed while replaying is held too and sent after the earlier ones, so order is preserved.</summary>
    private int ReleaseKeys()
    {
        int total = 0;
        while (true)
        {
            (Win32.KeyData Key, int Message)[] keys;
            lock (holdGate)
            {
                if (!holdingKeys) return total;
                if (heldKeys.Count == 0) { holdingKeys = false; return total; }
                keys = heldKeys.ToArray(); heldKeys.Clear();
            }
            Win32.Send(keys.Select(x =>
            {
                bool unicode = x.Key.Vk == 0xE7;
                uint flags = (x.Message is 0x0101 or 0x0105 ? 2u : 0u) | ((x.Key.Flags & 1) != 0 ? 1u : 0u) | (unicode ? 4u : 0u);
                return new Win32.Input { Type = 1, Data = new() { Keyboard = new() { Vk = unicode ? (ushort)0 : (ushort)x.Key.Vk, Scan = (ushort)x.Key.Scan, Flags = flags, Extra = ReplayTag } } };
            }));
            total += keys.Length;
        }
    }
    private System.Threading.Timer? privacyTimer;
    private UndoState? undo;
    // Some editors (Windows 11 Notepad) move focus to another element of their window right after a paste. That hop
    // must not lose the undo: it is kept here and still verified against the text and caret when used.
    private (UndoState State, long At)? undoAcrossFocusHop;
    // Words whose check was interrupted by further typing wait here for the next pause. Their context is still only the
    // next three words (the detector takes no more), but a fast typist may type several more before pausing: a word stays
    // until MaxPendingFollowing words (and at most 160 characters) follow it, instead of being dropped after three.
    private const int MaxPendingFollowing = 8;
    private readonly List<PendingWord> pendingWords = new(MaxPendingFollowing);
    private readonly bool allowOwnProcess;
    private string currentApp = "";
    private readonly HashSet<string> passwordApps = new(StringComparer.OrdinalIgnoreCase) { "KeePass.exe", "KeePassXC.exe", "1Password.exe", "Bitwarden.exe", "CredentialUIBroker.exe", "LogonUI.exe", "consent.exe" };
    public DateTime? PausedUntil { get; private set; }
    public bool SessionPaused { get; private set; }
    public bool Running => hookActive && settings.Value.Enabled && !SessionPaused && !(PausedUntil > DateTime.Now) && (Interlocked.Read(ref analysisStarted) == 0 || Environment.TickCount64 - Interlocked.Read(ref analysisStarted) < 2000);
    public string Health => !hookActive ? "Обробка клавіатури недоступна" : Interlocked.Read(ref analysisStarted) > 0 && Environment.TickCount64 - Interlocked.Read(ref analysisStarted) >= 2000 ? "Мовний аналіз не відповідає" : "";
    public event Action<Correction, string>? Corrected;
    public event Action<string>? Status;
    /// <summary>A one-time hint that deserves a tray notification, not just the status line.</summary>
    public event Action<string>? Notice;
    private int hiddenTextNoticeShown;
    public event Action<string>? LearnWord;
    // SubmitAfter: the user's Enter was held for a wrong-layout web address and is sent after its correction.
    private sealed record WordJob(long Sequence, long Epoch, long Ordinal, (IntPtr Window, IntPtr Focus) Focus, string Original, string Source, string Boundary, string App, long CreatedAt, bool SubmitAfter = false, bool AwaitPermission = false);
    private sealed record PendingWord(WordJob Job, string Context, string Following = "", int Count = 0);
    private readonly record struct BatchFix(long Ordinal, string Original, string Text, string Target);
    /// <summary>The attempt's preceding words with the corrections already confirmed in this batch. A word is replaced only
    /// at its own position, counted back from the attempt (the context holds the words typed right before it).</summary>
    private static string BatchContext(PendingWord attempt, List<BatchFix> fixes)
    {
        if (fixes.Count == 0 || attempt.Context.Length == 0) return attempt.Context;
        var tokens = attempt.Context.Split(' ');
        foreach (var fix in fixes)
        {
            long index = tokens.Length - (attempt.Job.Ordinal - fix.Ordinal);
            if (index >= 0 && index < tokens.Length && tokens[index] == fix.Original) tokens[index] = fix.Text;
        }
        return string.Join(" ", tokens);
    }
    private sealed record UndoState(EditorSnapshot Snapshot, string Original, string SourceLanguage, string LearningToken, string Boundary);
    public KeyboardService(NativeEngine engine, SettingsStore settings, bool allowOwnProcess = false, LanguageDetection? detection = null)
    {
        this.engine = engine; this.settings = settings; this.allowOwnProcess = allowOwnProcess; this.detection = detection;
        keyboardProc = KeyboardHook; mouseProc = MouseHook;
        focusProc = OnFocusEvent;
        thread = new Thread(Run) { Name = "Switchcraft input", IsBackground = true };
        thread.SetApartmentState(ApartmentState.STA);
    }
    public void Start()
    {
        thread.Start(); _ = Task.Run(ProcessWords);
        privacyTimer = new System.Threading.Timer(_ => { _ = ProbeFocus(); QueueIdleRetry(); EnterWatchdog(); }, null, 0, 150);
    }
    internal readonly record struct FocusNotice(uint Event, int Object, int Child, bool SameFocus,
        bool Permitted, bool FreshPermission, bool Scintilla, bool EventIsEditor, bool EventIsListBox,
        bool SameProcess, bool OwnedByWindow);
    internal static bool IgnoreScintillaFocus(FocusNotice notice) => notice.Event == 0x8005 && notice.Object == -4
        && notice.Child >= 0 && notice.SameFocus && notice.Permitted && notice.FreshPermission && notice.Scintilla
        && (notice.EventIsEditor && notice.Child == 0
            || notice.EventIsListBox && notice.SameProcess && notice.OwnedByWindow);
    private void OnFocusEvent(IntPtr hook, uint evt, IntPtr window, int obj, int child, uint eventThread, uint time)
    {
        lock (gate)
        {
            var current = Win32.Focus();
            string eventClass = Win32.ClassName(window);
            bool currentScintilla = Win32.ClassName(current.Focus) == "Scintilla";
            bool sameProcess = false, eventAncestorIsWindow = window == tracked.Window;
            bool eventAncestorIsEditor = window == tracked.Focus;
            if (eventClass == "ListBox" || DiagnosticsEnabled)
            {
                Win32.GetWindowThreadProcessId(window, out var eventProcess);
                Win32.GetWindowThreadProcessId(current.Window, out var foregroundProcess);
                sameProcess = eventProcess != 0 && eventProcess == foregroundProcess;
                var ancestor = window;
                for (int i = 0; i < 16 && ancestor != IntPtr.Zero && !eventAncestorIsWindow; i++)
                {
                    ancestor = Win32.GetParent(ancestor);
                    eventAncestorIsEditor |= ancestor == tracked.Focus;
                    eventAncestorIsWindow = ancestor == tracked.Window;
                }
            }
            bool fresh = permissionTime != 0 && Environment.TickCount64 - permissionTime <= 650;
            if (DiagnosticsEnabled)
            {
                string permissionAge = permissionTime == 0 ? "missing" : fresh ? "fresh" : "expired";
                string eventCategory = eventClass is "Scintilla" or "ListBox" or "ComboLBox" ? eventClass : "other";
                // Categorical state only: never record HWNDs, text or object names.
                Trace($"focus_event:evt={evt:X}:obj={obj}:child={child}:current_tracked={current == tracked}"
                    + $":event_tracked_focus={window == tracked.Focus}:permitted_tracked={permitted == tracked}:allowed={allowed}"
                    + $":event_class={eventCategory}:current_scintilla={currentScintilla}"
                    + $":tracked_scintilla={Win32.ClassName(tracked.Focus) == "Scintilla"}:permission={permissionAge}"
                    + $":event_window={window == tracked.Window}:event_foreground_process={sameProcess}"
                    + $":event_ancestor_window={eventAncestorIsWindow}:event_ancestor_editor={eventAncestorIsEditor}");
            }
            // Scintilla's autocomplete list exposes virtual item focus while the
            // real input HWND remains the editor. Its own ListBox is the only
            // popup exception; generic UIA fields may share a HWND and still reset.
            var notice = new FocusNotice(evt, obj, child, current == tracked, permitted == tracked && allowed,
                fresh, currentScintilla, window == tracked.Focus, eventClass == "ListBox", sameProcess, eventAncestorIsWindow);
            if (IgnoreScintillaFocus(notice))
            { Trace(notice.EventIsListBox ? "scintilla_autocomplete_focus" : "repeated_scintilla_focus"); return; }
            if (undo != null && current.Window == tracked.Window) undoAcrossFocusHop = (undo, Environment.TickCount64);
            Trace("focus_reset"); Reset(); allowed = false; awaitingPermission = true;
        }
    }
    private void Run()
    {
        threadId = Win32.GetCurrentThreadId();
        keyboardHook = Win32.SetWindowsHookEx(Win32.WH_KEYBOARD_LL, keyboardProc, Win32.GetModuleHandle(null), 0);
        mouseHook = Win32.SetWindowsHookEx(Win32.WH_MOUSE_LL, mouseProc, Win32.GetModuleHandle(null), 0);
        focusHook = Win32.SetWinEventHook(0x8005, 0x8005, IntPtr.Zero, focusProc, 0, 0, 0);
        foregroundHook = Win32.SetWinEventHook(3, 3, IntPtr.Zero, focusProc, 0, 0, 0);
        hookActive = keyboardHook != IntPtr.Zero && mouseHook != IntPtr.Zero && focusHook != IntPtr.Zero && foregroundHook != IntPtr.Zero;
        try
        {
            if (!hookActive) { Status?.Invoke("Не вдалося підключити обробку вводу: " + Marshal.GetLastWin32Error()); return; }
            Forms.Application.Run();
        }
        finally
        {
            hookActive = false;
            if (keyboardHook != IntPtr.Zero) Win32.UnhookWindowsHookEx(keyboardHook);
            if (mouseHook != IntPtr.Zero) Win32.UnhookWindowsHookEx(mouseHook);
            if (focusHook != IntPtr.Zero) Win32.UnhookWinEvent(focusHook);
            if (foregroundHook != IntPtr.Zero) Win32.UnhookWinEvent(foregroundHook);
        }
    }
    private async Task ProbeFocus()
    {
        if (disposed || Interlocked.Exchange(ref probeBusy, 1) != 0) return;
        try
        {
            var focus = Win32.Focus(); var app = Win32.AppName(focus.Window);
            long epoch; lock (gate) epoch = contextEpoch;
            bool? writable = app.Length > 0 && !passwordApps.Contains(app) && !IsExcluded(app) ? await editor.IsWritable(focus) : false;
            lock (gate)
            {
                if (!disposed && epoch == contextEpoch && focus == Win32.Focus() && writable == null)
                {
                    // No answer is not a denial: keys typed meanwhile wait (and are discarded only if the field is then denied).
                    // An allowed field keeps its permission until it expires, then its keys wait the same way.
                    if (permitted != focus || !allowed) { if (!awaitingPermission) Trace("probe_unknown"); awaitingPermission = true; }
                }
                else if (!disposed && epoch == contextEpoch && focus == Win32.Focus())
                {
                    bool safe = writable == true;
                    bool undecided = awaitingPermission || permitted != focus;
                    // Keys typed into this field while it was being checked belong to it; anything else is stale.
                    if (permitted != focus && tracked != focus || !safe && (allowed || undecided)) { Trace(safe ? "probe_new_field" : "probe_denied"); Reset(); }
                    permitted = focus; allowed = safe; awaitingPermission = false; currentApp = app; permissionTime = Environment.TickCount64;
                }
            }
        }
        catch { lock (gate) { allowed = false; Reset(); } }
        finally { Volatile.Write(ref probeBusy, 0); }
    }
    public void Reset([System.Runtime.CompilerServices.CallerMemberName] string caller = "") { lock (gate) { Trace("reset:" + caller); sequence++; contextEpoch++; word.Clear(); undo = null; pendingWords.Clear(); languageContext.Reset(); recentWords.Clear(); CancelAnalysis(); incomplete = false; } }
    private void QueueIdleRetry()
    {
        lock (gate)
        {
            if (disposed || detection?.UsesAi != true || pendingWords.Count == 0 || word.Length == 0 || incomplete
                || sequence == lastIdleSequence || Environment.TickCount64 - lastInputTime < 450
                || !Current(sequence, tracked)) return;
            // Reconsider only an earlier completed word. The unfinished word remains
            // context, never a correction target, and stays in the keyboard buffer.
            lastIdleSequence = sequence;
            jobs.Writer.TryWrite(new(sequence, contextEpoch, wordOrdinal + 1, tracked, word.ToString(),
                Win32.LayoutLanguage(tracked.Window), "", currentApp, Environment.TickCount64));
        }
    }
    private void CancelAnalysis() { if (activeAnalysis != null) _ = activeAnalysis.CancelAsync(); }
    public void Pause(int minutes = 0) { SessionPaused = minutes == 0; PausedUntil = minutes > 0 ? DateTime.Now.AddMinutes(minutes) : null; Reset(); }
    public void Resume() { SessionPaused = false; PausedUntil = null; settings.Value.Enabled = true; Reset(); }
    public bool IsExcluded(string app) => (!allowOwnProcess && app.Equals("Switchcraft.exe", StringComparison.OrdinalIgnoreCase)) || settings.Value.ExcludedApps.ToArray().Contains(app, StringComparer.OrdinalIgnoreCase);
    private IntPtr MouseHook(int code, IntPtr message, IntPtr data)
    {
        if (code >= 0 && message.ToInt32() is 0x0201 or 0x0204 or 0x0207 or 0x020A) { lock (gate) { Reset(); allowed = false; awaitingPermission = true; } }
        return Win32.CallNextHookEx(mouseHook, code, message, data);
    }
    private IntPtr KeyboardHook(int code, IntPtr message, IntPtr data)
    {
        if (code < 0) return Win32.CallNextHookEx(keyboardHook, code, message, data);
        try
        {
            var key = Marshal.PtrToStructure<Win32.KeyData>(data);
            if (key.Extra != Win32.InjectionTag && key.Extra != ReplayTag && Volatile.Read(ref holdingKeys))
                lock (holdGate) if (holdingKeys) { heldKeys.Add((key, message.ToInt32())); return (IntPtr)1; }
            if (key.Extra == Win32.InjectionTag || message.ToInt32() is not (0x0100 or 0x0104)) return Win32.CallNextHookEx(keyboardHook, code, message, data);
            if (DiagnosticsEnabled) Interlocked.Increment(ref diagnosticKeys);
            var focus = Win32.Focus();
            lock (gate)
            {
                sequence++;
                lastInputTime = Environment.TickCount64;
                CancelAnalysis();
                if (key.Vk is 0x10 or 0x11 or 0x12 or 0xA0 or 0xA1 or 0xA2 or 0xA3 or 0xA4 or 0xA5) return Win32.CallNextHookEx(keyboardHook, code, message, data);
                if (tracked != focus) { Reset(); tracked = focus; }
                uint boundary = key.Vk == 0xE7 ? key.Scan : key.Vk;
                if (boundary == 0x0A) boundary = 0x0D;
                bool fresh = Environment.TickCount64 - permissionTime <= 650;
                bool permittedNow = Running && allowed && permitted == focus && fresh && !IsExcluded(currentApp);
                bool denied = permitted == focus && fresh && !allowed && !awaitingPermission;
                bool pendingPermission = !permittedNow && Running && !denied && (awaitingPermission || permitted != focus || !fresh);
                if (!permittedNow && !pendingPermission)
                {
                    word.Clear(); undo = null; undoAcrossFocusHop = null; pendingWords.Clear(); recentWords.Clear(); incomplete = boundary is not (0x20 or 0x0D or 0x09); return Win32.CallNextHookEx(keyboardHook, code, message, data);
                }
                if (pendingPermission && Interlocked.CompareExchange(ref probeKick, 1, 0) == 0)
                    ThreadPool.QueueUserWorkItem(_ => { Volatile.Write(ref probeKick, 0); _ = ProbeFocus(); });
                var source = Win32.LayoutLanguage(focus.Window);
                if (DiagnosticsEnabled) Interlocked.Increment(ref diagnosticAccepted);
                if (!settings.Value.Languages.Contains(source))
                {
                    // An unreadable layout must not look like Switchcraft silently failing: say so once per layout.
                    var layout = Win32.GetKeyboardLayout(Win32.GetWindowThreadProcessId(focus.Window, out _));
                    if (source.Length == 0 && reportedLayouts.Add(layout))
                        ThreadPool.QueueUserWorkItem(_ => Notice?.Invoke($"Switchcraft не розпізнає розкладку {Win32.LayoutDescription(layout)}, тому набір у ній не виправляється. Підтримуються англійська, українська та російська розкладки."));
                    Reset(); return Win32.CallNextHookEx(keyboardHook, code, message, data);
                }
                if (languageContext.ObserveLayout(source)) { word.Clear(); undo = null; undoAcrossFocusHop = null; pendingWords.Clear(); contextEpoch++; recentWords.Clear(); }
                if (Win32.Down(0x11) || Win32.Down(0x12) || Win32.Down(0x5B) || Win32.Down(0x5C))
                {
                    word.Clear(); contextEpoch++; pendingWords.Clear(); languageContext.Reset(); recentWords.Clear();
                    if (!HotkeyRegistry.Matches(settings.Value.Hotkeys["undo"], key.Vk)) { undo = null; undoAcrossFocusHop = null; }
                    return Win32.CallNextHookEx(keyboardHook, code, message, data);
                }
                undo = null; undoAcrossFocusHop = null;
                if (key.Vk == 8) { pendingWords.Clear(); contextEpoch++; if (word.Length > 0) word.Length--; else { incomplete = true; recentWords.Clear(); } return Win32.CallNextHookEx(keyboardHook, code, message, data); }
                if (boundary is 0x20 or 0x0D or 0x09)
                {
                    var original = word.ToString(); word.Clear();
                    wordOrdinal++;
                    if (!incomplete && original.Length > 0 && boundary != 0x09)
                    {
                        // A web address typed with a Cyrillic layout (пщщпдуюсщь): hold this Enter and later keys
                        // until the address is corrected, so it is not submitted in the wrong layout.
                        if (boundary == 0x0D && settings.Value.Languages.Contains("en") && engine.AddressReading(original, source) != null
                            && Interlocked.CompareExchange(ref enterHeld, 1, 0) == 0)
                        {
                            if (HoldKeys())
                            {
                                if (jobs.Writer.TryWrite(new(sequence, contextEpoch, wordOrdinal, focus, original, source, "", currentApp, lastInputTime, SubmitAfter: true, AwaitPermission: pendingPermission)))
                                { Interlocked.Exchange(ref enterHeldAt, Environment.TickCount64); incomplete = false; return (IntPtr)1; }
                                ReleaseKeys();
                            }
                            Volatile.Write(ref enterHeld, 0);
                        }
                        jobs.Writer.TryWrite(new(sequence, contextEpoch, wordOrdinal, focus, original, source, boundary == 0x20 ? " " : "\n", currentApp, lastInputTime, AwaitPermission: pendingPermission));
                    }
                    incomplete = false;
                    if (boundary == 0x09) { Reset(); allowed = false; awaitingPermission = true; }
                    return Win32.CallNextHookEx(keyboardHook, code, message, data);
                }
                if (key.Vk is 0x1B or 0x21 or 0x22 or 0x23 or 0x24 or 0x25 or 0x26 or 0x27 or 0x28 or 0x2D or 0x2E || key.Vk >= 0x70 && key.Vk <= 0x87)
                { Reset(); incomplete = true; return Win32.CallNextHookEx(keyboardHook, code, message, data); }
                if (incomplete) return Win32.CallNextHookEx(keyboardHook, code, message, data);
                if (key.Vk == 0xE7)
                {
                    var ch = (char)key.Scan;
                    if (char.IsControl(ch) || char.IsSurrogate(ch) || word.Length >= 256) { word.Clear(); incomplete = true; }
                    else word.Append(ch);
                    return Win32.CallNextHookEx(keyboardHook, code, message, data);
                }
                if (key.Vk >= 256) { word.Clear(); incomplete = true; return Win32.CallNextHookEx(keyboardHook, code, message, data); }
                var state = new byte[256]; Win32.GetKeyboardState(state);
                for (int k = 0; k < state.Length; k++) state[k] = (byte)((state[k] & 1) | (Win32.Down(k) ? 0x80 : 0));
                state[key.Vk] |= 0x80;
                var text = new StringBuilder(8);
                int count = Win32.ToUnicodeEx(key.Vk, key.Scan, state, text, text.Capacity, 4, Win32.GetKeyboardLayout(Win32.GetWindowThreadProcessId(focus.Window, out _)));
                if (count > 0 && !text.ToString().Any(char.IsControl) && word.Length + count <= 256) word.Append(text);
                else if (count != 0) { word.Clear(); incomplete = true; }
                if (DiagnosticsEnabled) Trace("translated:" + count + ":buffer=" + word.Length);
            }
        }
        catch (Exception ex) { Reset(); Status?.Invoke("Виправлення пропущено: " + ex.GetType().Name); }
        return Win32.CallNextHookEx(keyboardHook, code, message, data);
    }
    private async Task<bool> PermissionFor(WordJob job)
    {
        for (long deadline = Environment.TickCount64 + 600; Environment.TickCount64 < deadline; await Task.Delay(15))
        {
            lock (gate)
            {
                if (disposed || job.Epoch != contextEpoch) return false;
                if (permitted == job.Focus && !awaitingPermission && Environment.TickCount64 - permissionTime <= 650) return allowed;
            }
        }
        return false;
    }
    // Diagnostics only: which condition of Current() failed, as categories (never text or handles).
    private string StaleReason(long id, (IntPtr Window, IntPtr Focus) focus, bool manual)
    {
        lock (gate)
            return string.Join(",", new[] { disposed ? "disposed" : "", !(manual ? hookActive : Running) ? "stopped" : "", id != sequence ? "keys" : "",
                focus != Win32.Focus() ? "focus" : "", !allowed ? (awaitingPermission ? "pending" : "denied") : "", permitted != focus ? "unprobed" : "",
                Environment.TickCount64 - permissionTime > 650 ? "expired" : "", Win32.ModifiersDown ? "modifiers" : "" }.Where(x => x.Length > 0));
    }
    private bool Current(long id, (IntPtr Window, IntPtr Focus) focus, bool manual = false)
    {
        lock (gate) return !disposed && (manual ? hookActive : Running) && id == sequence && focus == Win32.Focus() && allowed && permitted == focus && Environment.TickCount64 - permissionTime <= 650;
    }
    private static bool ContextBreakBefore(string token) => token.IndexOfAny(new[] { '«', '»', '“', '”', '"', '(', ')', '[', ']' }) >= 0;
    private static bool ContextBreakAfter(string token) => token.IndexOfAny(new[] { '.', '!', '?', ':', ';', '…', '»', '”', '"' }) >= 0;
    // Called under gate after a confirmed replacement. An older unresolved word
    // still reaches the same caret, but its suffix now contains the new spelling.
    private void RebasePendingFollowing(PendingWord corrected, string replacement)
    {
        for (int i = pendingWords.Count - 1; i >= 0; i--)
        {
            var pending = pendingWords[i];
            if (pending.Job.Sequence == corrected.Job.Sequence) { pendingWords.RemoveAt(i); continue; }
            if (pending.Job.Ordinal >= corrected.Job.Ordinal) continue;
            long offset = corrected.Job.Ordinal - pending.Job.Ordinal - 1;
            var tokens = pending.Following.Split(' ');
            if (replacement.Any(char.IsWhiteSpace) || tokens.Length != pending.Count || offset < 0 || offset >= tokens.Length
                || tokens[(int)offset] != corrected.Job.Original)
            { pendingWords.RemoveAt(i); continue; }
            tokens[(int)offset] = replacement;
            string following = string.Join(" ", tokens);
            if (following.Length > 160) pendingWords.RemoveAt(i);
            else pendingWords[i] = pending with { Following = following };
        }
    }
    private async Task ProcessWords()
    {
        await foreach (var job in jobs.Reader.ReadAllAsync())
        {
            if (job.SubmitAfter) { await SubmitAddress(job); continue; }
            // A word typed while its field was still being checked is not even evaluated until the field is allowed.
            if (job.AwaitPermission && !await PermissionFor(job)) { Trace("permission_not_granted"); continue; }
            if (DiagnosticsEnabled) Interlocked.Increment(ref diagnosticJobs);
            try
            {
                string context, recent;
                var attempts = new List<PendingWord>(4);
                bool idle = job.Boundary.Length == 0;
                lock (gate)
                {
                    if (disposed || job.Epoch != contextEpoch) continue;
                    context = ContextBreakBefore(job.Original) ? "" : languageContext.Language;
                }
                Interlocked.Exchange(ref analysisStarted, Environment.TickCount64);
                var local = engine.Evaluate(job.Original, job.Source, context);
                bool needsContext = detection?.UsesAi == true && (local.Changed || detection.NeedsContext(job.Original, job.Source))
                    // In every mode: a reading the dictionary lacks may still be settled by the rest of the sentence.
                    || !local.Changed && !idle && engine.ContextLayoutReadings(job.Original, job.Source).Count > 0;
                string identified = local.Changed ? "" : engine.IdentifyLanguage(job.Original);
                // Wrong-layout punctuation keys can represent letters, e.g.
                // cke;bk -> служил. Only the verified spelling defines its scope.
                string boundaryToken = local.Changed ? local.Text : job.Original;
                TraceDecision("local", local.Reason);
                Interlocked.Exchange(ref analysisStarted, 0);
                lock (gate)
                {
                    if (disposed || job.Epoch != contextEpoch) continue;
                    // Keep the text sent to AI within the same scope as the local
                    // language evidence. A quotation can deliberately change language.
                    if (ContextBreakBefore(boundaryToken)) { recentWords.Clear(); pendingWords.Clear(); }
                    if (!idle) languageContext.Before(boundaryToken);
                    context = languageContext.Language;
                    recent = string.Join(" ", recentWords);
                    for (int i = pendingWords.Count - 1; i >= 0; i--)
                    {
                        var retry = pendingWords[i];
                        string following = retry.Following.Length == 0 ? job.Original : retry.Following + " " + job.Original;
                        // A different layout here is Switchcraft's own switch after a correction: a manual
                        // switch already cleared every pending word in the keyboard hook.
                        if (retry.Job.Epoch != job.Epoch || retry.Job.Ordinal + retry.Count + 1 != job.Ordinal
                            || retry.Job.Focus != job.Focus || job.Boundary == "\n"
                            || retry.Count >= MaxPendingFollowing || following.Length > 160 || job.CreatedAt - retry.Job.CreatedAt > 15_000
                            || !LanguageDetection.Sendable(job.Original)) pendingWords.RemoveAt(i);
                        else
                        {
                            retry = retry with { Following = following, Count = retry.Count + 1 };
                            // Idle context is provisional; the actual delimiter will commit it.
                            if (!idle) pendingWords[i] = retry;
                            attempts.Insert(0, retry);
                        }
                    }
                    if (detection?.UsesAi == true && !idle)
                    {
                        // Preserve what was actually typed before awaiting a cancellable request.
                        if (LanguageDetection.Sendable(job.Original)) recentWords.Enqueue(job.Original);
                        while (recentWords.Count > 6 || string.Join(" ", recentWords).Length > 180) recentWords.Dequeue();
                        if (job.Boundary != " " || ContextBreakAfter(boundaryToken)) recentWords.Clear();
                    }
                    // The last word may still settle a retry in this sentence, but
                    // later words must never carry that retry across its boundary.
                    if (!idle && ContextBreakAfter(boundaryToken)) pendingWords.Clear();
                }
                if (idle && attempts.Count == 0) continue;
                lock (gate)
                {
                    if (disposed || job.Epoch != contextEpoch) continue;
                    if (needsContext && !idle && job.Boundary == " "
                        && !ContextBreakBefore(boundaryToken) && !ContextBreakAfter(boundaryToken)
                        && LanguageDetection.Sendable(job.Original) && !LanguageDetection.LetterMention(job.Original, recent))
                    {
                        pendingWords.Add(new(job, recent));
                        if (pendingWords.Count > MaxPendingFollowing) pendingWords.RemoveAt(0);
                    }
                    // Record independently known words before the cancellable HTTP
                    // wait, otherwise continuous typing silently loses this evidence.
                    if (!idle)
                    {
                        languageContext.Observe(identified);
                        languageContext.After(boundaryToken);
                        if (job.Boundary == "\n") { languageContext.Reset(); recentWords.Clear(); }
                    }
                }
                if (!idle) attempts.Add(new(job, recent));
                if (!Current(job.Sequence, job.Focus)) { Trace("job_stale"); continue; }
                // An implicit UIA paragraph terminator cannot prove an Enter was
                // consumed. A second Enter could submit a form.
                if (job.Boundary == "\n" && Win32.ClassName(job.Focus.Focus) != "Scintilla") continue;
                using var cancellation = new CancellationTokenSource();
                lock (gate) activeAnalysis = cancellation;
                try
                {
                    if (detection?.UsesAi == true) await Task.Delay(120, cancellation.Token);
                    Trace("attempts:" + attempts.Count);
                    // A phrase typed without a pause is corrected here in one batch: later words are judged with the
                    // corrected spelling (and language) of the earlier ones, not with the wrong-layout text typed.
                    var batchFixes = new List<BatchFix>(attempts.Count);
                    foreach (var attempt in attempts)
                    {
                        if (!Current(job.Sequence, job.Focus)) { Trace("batch_stale"); break; }
                        bool deferred = attempt.Job.Sequence != job.Sequence;
                        string preceding = BatchContext(attempt, batchFixes);
                        string batchLanguage = batchFixes.Count >= 2 && batchFixes.All(x => x.Target == batchFixes[0].Target)
                            && batchFixes[0].Target is "uk" or "ru" ? batchFixes[0].Target : context;
                        var result = deferred || batchLanguage != context ? engine.Evaluate(attempt.Job.Original, attempt.Job.Source, batchLanguage) : local;
                        if (detection?.UsesAi == true)
                            result = await detection.EvaluateAsync(attempt.Job.Original, attempt.Job.Source, batchLanguage,
                                preceding, cancellation.Token, attempt.Following);
                        if (deferred && !result.Changed && engine.ContextLayoutRepair(attempt.Job.Original, attempt.Job.Source,
                            attempt.Following.Split(' ').Concat(preceding.Split(' ').TakeLast(3))) is { } repaired)
                            result = repaired;
                        cancellation.Token.ThrowIfCancellationRequested();
                        TraceDecision("decision", result.Reason);
                        // A known word gets a final veto. An unknown word can be
                        // reconsidered when genuinely new following context arrives.
                        if ((result.Reason is "ai_keep" or "local_ai_keep") && engine.KnownLanguages(attempt.Job.Original).Length > 0)
                        {
                            lock (gate) pendingWords.RemoveAll(x => x.Job.Sequence == attempt.Job.Sequence);
                        }
                        if (deferred && !result.Changed) { Trace("deferred_kept"); continue; }
                        string originalTail = attempt.Job.Original + (attempt.Following.Length == 0 ? "" : " " + attempt.Following);
                        string replacementTail = result.Text + (attempt.Following.Length == 0 ? "" : " " + attempt.Following);
                        if (!Current(job.Sequence, job.Focus) || !await operation.WaitAsync(0)) break;
                        try
                        {
                            EditorSnapshot? before = null;
                            for (int i = 0; i < 5 && Current(job.Sequence, job.Focus); i++)
                            {
                                await Task.Delay(8);
                                before = await editor.Capture(job.Focus, originalTail + job.Boundary);
                                if (before != null) break;
                            }
                            if (before == null || !Current(job.Sequence, job.Focus))
                            {
                                Trace("capture_skipped");
                                if (before == null && result.Changed && Interlocked.CompareExchange(ref hiddenTextNoticeShown, 1, 0) == 0)
                                {
                                    if (await editor.IsHiddenEditorText(job.Focus))
                                        Notice?.Invoke("VS Code не показує текст редактора, тому Switchcraft пропускає там виправлення. Увімкніть у VS Code режим для екранного диктора: Shift+Alt+F1 або параметр \"editor.accessibilitySupport\": \"on\".");
                                    else Volatile.Write(ref hiddenTextNoticeShown, 0);
                                }
                                continue;
                            }
                            if (DiagnosticsEnabled) Interlocked.Increment(ref diagnosticCaptures);
                            if (result.Changed)
                            {
                                var delimiter = before.Text.EndsWith("\r\n") ? "\r\n" : job.Boundary;
                                // The layout follows the correction, also when the user kept typing: the switch happens while
                                // those keys are held, so they are replayed in the corrected layout. An earlier word switches
                                // only then (the rest of this batch is skipped once the user types; otherwise its last word
                                // decides), never while a word is still being typed (idle), and never over a valid word after it.
                                bool switchLayout = !idle && (!deferred || local.Changed || !engine.KnownLanguages(job.Original).Contains(job.Source));
                                var after = await ReplaceVerified(before, replacementTail + delimiter, job.Sequence,
                                    layout: switchLayout ? result.Target : null, layoutOnlyWhenTyping: deferred);
                                if (after == null) { Trace("replacement_skipped"); continue; }
                                if (DiagnosticsEnabled) Interlocked.Increment(ref diagnosticReplacements);
                                Trace("replacement_confirmed");
                                batchFixes.Add(new(attempt.Job.Ordinal, attempt.Job.Original, result.Text, result.Target));
                                lock (gate)
                                {
                                    // The editor now holds the corrected spelling even when the user kept typing
                                    // (those keys were held and replayed after it), so earlier pending words must see it.
                                    if (job.Epoch == contextEpoch) RebasePendingFollowing(attempt, result.Text);
                                    if (Current(job.Sequence, job.Focus))
                                    {
                                        recentWords.Clear();
                                        if (job.Boundary != "\n") { undo = new(after, originalTail, job.Source, result.Original, job.Boundary); undoAcrossFocusHop = null; }
                                    }
                                }
                                Corrected?.Invoke(result, job.App);
                            }
                            else if (!deferred)
                            {
                                string language;
                                lock (gate) language = languageContext.Language;
                                if (job.Source is "uk" or "ru" && identified == language && identified.Length > 0 && identified != job.Source)
                                    await SwitchLayout(job.Focus.Window, identified, job.Sequence);
                            }
                        }
                        finally { operation.Release(); }
                    }
                }
                finally { lock (gate) { if (activeAnalysis == cancellation) activeAnalysis = null; } }
            }
            catch (OperationCanceledException) { Interlocked.Exchange(ref analysisStarted, 0); Trace("analysis_cancelled"); }
            catch (Exception ex) { Interlocked.Exchange(ref analysisStarted, 0); Status?.Invoke("Виправлення пропущено: " + ex.GetType().Name); }
        }
    }
    /// <summary>Replaces the captured text and verifies it. With <paramref name="layout"/>, a confirmed replacement also
    /// switches the keyboard layout before the held keys are replayed, so they are typed in that layout;
    /// with <paramref name="layoutOnlyWhenTyping"/>, only if the user typed meanwhile (keys are held).</summary>
    private async Task<EditorSnapshot?> ReplaceVerified(EditorSnapshot before, string replacement, long id, bool manual = false,
        string? layout = null, bool layoutOnlyWhenTyping = false)
    {
        bool Valid() => Current(id, before.Focus, manual) && !Win32.ModifiersDown;
        ClipboardLease? clipboard = null; bool submitted = false, confirmed = false, selecting = false, ownsHold = false;
        try
        {
            if (!Valid()) { Trace("replace_abort:stale:" + StaleReason(id, before.Focus, manual)); return null; }
            // Hold the user's next keys from the start: a busy clipboard must not let them make this word stale.
            ownsHold = HoldKeys();
            if (!before.Scintilla)
            {
                clipboard = await ClipboardLease.Acquire(replacement, Valid);
                if (clipboard == null) { Trace("replace_abort:clipboard:" + ClipboardLease.LastFailure); Status?.Invoke("Буфер обміну зайнятий — заміну пропущено."); return null; }
            }
            // The early-release budget starts with the selection: a clipboard wait must not use it up, or the held keys
            // would be released before the paste is confirmed and the confirmation would fail.
            long holdStart = Environment.TickCount64;
            selecting = true;
            if (!Valid() || !await editor.SelectIfCurrent(before, Valid)) { Trace(Valid() ? "replace_abort:select" : "replace_abort:stale_select"); return null; }
            // Never hold the hook's gate during SendInput: external input can
            // otherwise wait for this lock while SendInput waits for the hook.
            if (!Valid() || clipboard != null && !clipboard.IsOwned) { Trace("replace_abort:before_paste"); return null; }
            submitted = true;
            bool sent = clipboard == null ? Win32.Send(Win32.TextInput(replacement)) : Win32.Chord(0x56);
            if (!sent) { Pause(); Status?.Invoke("Windows не завершила заміну. Автовиправлення призупинено; перевірте текст."); return null; }
            // Chromium's address bar trims whitespace from pasted text, which would drop the user's space.
            string trimmed = replacement.TrimEnd(' ');
            bool boundaryTyped = clipboard == null || trimmed.Length == 0 || trimmed.Length == replacement.Length;
            EditorSnapshot? pastedWithoutBoundary = null; int typedAt = 0; bool resent = false;
            for (int round = 0; round < 3; round++)
            {
                for (int i = 0; i < 20 && Current(id, before.Focus, manual); i++)
                {
                    if (pastedWithoutBoundary != null && i - typedAt > 4) break;
                    await Task.Delay(15);
                    // Keys typed after the paste are safe; never delay typing for long. Not while our selection is still
                    // there (the editor has not applied the paste yet): a released key would overwrite the selected words.
                    if (ownsHold && Environment.TickCount64 - holdStart > MaxHoldMs && !await editor.StillSelected(before)) ReleaseKeys();
                    var after = await editor.Capture(before.Focus, replacement, afterPaste: true, pasted: before.Element);
                    if (after != null && (!before.Scintilla || after.Document == before.Document && after.Start == before.Start)) { confirmed = true; return after; }
                    // Only exactly our replacement without its trailing space qualifies; then type the space itself.
                    if (!boundaryTyped)
                    {
                        var trimmedAfter = await editor.Capture(before.Focus, trimmed, afterPaste: true, pasted: before.Element);
                        if (trimmedAfter != null && Valid())
                        {
                            boundaryTyped = true;
                            if (!Win32.Send(Win32.TextInput(replacement[trimmed.Length..]))) break;
                            pastedWithoutBoundary = trimmedAfter; typedAt = i;
                        }
                    }
                }
                // The paste itself was verified and the space was typed by us. Chrome's address bar then replaces
                // its accessible text with a suggestion description, which is not a failed replacement.
                if (pastedWithoutBoundary != null && Current(id, before.Focus, manual)) { confirmed = true; return pastedWithoutBoundary; }
                // Windows 11 Notepad occasionally ignores one of several quick pastes: our selection still holds exactly the
                // original text. After a settle time (a late paste would show by then) it is pasted once more; if that is
                // ignored too, the word is skipped (selection collapsed, clipboard restored) instead of pausing all corrections.
                if (!Current(id, before.Focus, manual) || !await editor.StillSelected(before)) break;
                await Task.Delay(300);
                if (!Current(id, before.Focus, manual)) break;
                if (!await editor.StillSelected(before)) continue; // changed meanwhile: confirm what is there now
                if (!resent && (clipboard == null || clipboard.IsOwned) && Valid())
                {
                    resent = true; Trace("paste_retry");
                    if (clipboard == null ? Win32.Send(Win32.TextInput(replacement)) : Win32.Chord(0x56)) continue;
                }
                submitted = false; Trace("replace_abort:not_pasted"); return null;
            }
            if (Current(id, before.Focus, manual)) { Pause(); Status?.Invoke("Редактор не підтвердив заміну. Автовиправлення призупинено; перевірте текст."); }
            return null;
        }
        finally
        {
            // An aborted replacement must not leave a (late) selection for the next key to overwrite.
            if (selecting && !submitted) await editor.CollapseIfSelected(before);
            if (clipboard != null)
            {
                if (confirmed || !submitted) { if (!await clipboard.Restore()) Status?.Invoke("Буфер зайнятий — попередній вміст не вдалося відновити."); }
                else Status?.Invoke("Вставлення не підтверджене; виправлений текст залишено в буфері обміну.");
            }
            // After the clipboard is restored, so a held Ctrl+V pastes the user's own content. Keys released early
            // (MaxHoldMs) were already typed in the old layout: SwitchLayout then sees a newer sequence and keeps it.
            bool typing; lock (holdGate) typing = heldKeys.Any(x => x.Message is 0x0100 or 0x0104); // a key press, not a late key-up
            try { if (confirmed && layout != null && (typing || !layoutOnlyWhenTyping)) await SwitchLayout(before.Focus.Window, layout, id); }
            finally { if (ownsHold) ReleaseKeys(); }
        }
    }
    private int enterHeld;
    private long enterHeldAt;
    /// <summary>Delivers the held Enter exactly once, before every key typed after it.</summary>
    private void SendHeldEnter()
    {
        if (Interlocked.Exchange(ref enterHeld, 0) == 0) return;
        Win32.Send(new[] { new Win32.Input { Type = 1, Data = new() { Keyboard = new() { Vk = 0x0D, Scan = 0x1C, Extra = ReplayTag } } } });
        ReleaseKeys();
    }
    // A lost or stuck correction must never swallow the user's Enter.
    private void EnterWatchdog()
    {
        if (Volatile.Read(ref enterHeld) == 1 && Environment.TickCount64 - Interlocked.Read(ref enterHeldAt) > 1500) SendHeldEnter();
    }
    private async Task SubmitAddress(WordJob job)
    {
        try
        {
            if (job.AwaitPermission && !await PermissionFor(job)) return;
            if (!await operation.WaitAsync(300)) return;
            try
            {
                var result = engine.Evaluate(job.Original, job.Source);
                TraceDecision("decision", result.Reason);
                if (result.Reason != "web_address" || !Current(job.Sequence, job.Focus)) return;
                var before = await editor.Capture(job.Focus, job.Original);
                if (before == null) { Trace("capture_skipped"); return; }
                if (await ReplaceVerified(before, result.Text, job.Sequence) == null) { Trace("replacement_skipped"); return; }
                Trace("replacement_confirmed");
                Corrected?.Invoke(result, job.App);
            }
            finally { operation.Release(); }
        }
        catch (Exception ex) { Status?.Invoke("Виправлення пропущено: " + ex.GetType().Name); }
        finally { SendHeldEnter(); }
    }
    private async Task SwitchLayout(IntPtr window, string language, long id)
    {
        if (!settings.Value.SwitchLayout || !settings.Value.Languages.Contains(language) || Win32.LayoutFor(language) == IntPtr.Zero) return;
        lock (gate) { if (id != sequence) { Trace("layout_kept:keys"); return; } languageContext.ExpectLayout(language); }
        Win32.SwitchLayout(window, language);
        for (int i = 0; i < 8; i++) { if (Win32.LayoutLanguage(window) == language) { Trace("layout_switched"); return; } await Task.Delay(15); }
        Trace("layout_unconfirmed");
        lock (gate) languageContext.Reset();
        Status?.Invoke("Заміна виконана, але редактор не підтвердив перемикання розкладки.");
    }
    public async Task UndoAsync()
    {
        if (!await operation.WaitAsync(0)) return;
        try
        {
            UndoState? saved;
            lock (gate)
            {
                saved = undo ?? (undoAcrossFocusHop is { } hop && Environment.TickCount64 - hop.At < 10_000 && hop.State.Snapshot.Focus.Window == Win32.Focus().Window ? hop.State : null);
                undo = null; undoAcrossFocusHop = null; word.Clear();
            }
            if (saved == null) { Status?.Invoke("Немає виправлення для скасування."); return; }
            if (!await WaitModifiersAsync()) return;
            if (!await FieldReadyAsync(saved.Snapshot.Focus) && !(await editor.RefocusField(saved.Snapshot) && await FieldReadyAsync(saved.Snapshot.Focus)))
            { Status?.Invoke("Текст або позиція курсора змінилися — скасування пропущено."); return; }
            long id; lock (gate) id = sequence;
            var after = await ReplaceVerified(saved.Snapshot, saved.Original + saved.Boundary, id, manual: true, layout: saved.SourceLanguage);
            if (after == null) { Status?.Invoke("Текст або позиція курсора змінилися — скасування пропущено."); return; }
            lock (gate) { languageContext.Reset(); recentWords.Clear(); pendingWords.Clear(); }
            if (settings.Value.LearningMode) LearnWord?.Invoke(saved.LearningToken);
            Status?.Invoke("Останнє виправлення скасовано.");
        }
        finally { operation.Release(); }
    }
    /// <summary>After a hotkey, Alt can briefly move focus to the window menu (Windows 11 Notepad), and the field is then
    /// checked again. Waits until that same field has focus and is allowed, never longer than 0.8 s.</summary>
    private async Task<bool> FieldReadyAsync((IntPtr Window, IntPtr Focus) focus)
    {
        for (long deadline = Environment.TickCount64 + 800; Environment.TickCount64 < deadline; await Task.Delay(20))
        {
            lock (gate) if (!disposed && focus == Win32.Focus() && allowed && permitted == focus && Environment.TickCount64 - permissionTime <= 650) return true;
            _ = ProbeFocus();
        }
        return false;
    }
    public static async Task<bool> WaitModifiersAsync()
    {
        for (int i = 0; i < 50; i++) { if (!Win32.ModifiersDown) return true; await Task.Delay(20); }
        return false;
    }
    public async Task ConvertSelectionAsync(string? target)
    {
        if (!await operation.WaitAsync(0)) { Status?.Invoke("Попереднє перетворення ще триває."); return; }
        try
        {
            var focus = Win32.Focus();
            if (!await WaitModifiersAsync() || !await FieldReadyAsync(focus)) return;
            Reset(); long id; lock (gate) id = sequence;
            if (!Current(id, focus, manual: true)) return;
            var before = await editor.Capture(focus);
            if (before == null) { Status?.Invoke("Виділіть текст у підтримуваному редакторі."); return; }
            var result = await Task.Run(() => engine.Convert(before.Text, target, Win32.LayoutLanguage(focus.Window)));
            if (result.Text == before.Text) return;
            if (await ReplaceVerified(before, result.Text, id, manual: true, layout: target ?? result.Target) == null) return;
            Status?.Invoke("Виділений текст перетворено.");
        }
        catch (Exception ex) { Status?.Invoke("Перетворення пропущено: " + ex.Message); }
        finally { operation.Release(); }
    }
    public void Dispose()
    {
        disposed = true; privacyTimer?.Dispose(); Reset(); jobs.Writer.TryComplete(); editor.Dispose();
        if (threadId != 0) Win32.PostThreadMessage(threadId, 0x0012, IntPtr.Zero, IntPtr.Zero);
        if (thread.IsAlive) thread.Join(1500);
    }
}
