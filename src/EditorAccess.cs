using System;
using System.Collections.Concurrent;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Automation;
using System.Windows.Automation.Text;

namespace Switchcraft;

// All UIA calls live on one MTA thread. A hung provider can only disable UIA
// corrections: bounded queues and expiring requests never block the keyboard hook.
internal sealed class AutomationWorker : IDisposable
{
    private readonly BlockingCollection<Action> queue = new(8);
    private readonly Thread thread;
    public AutomationWorker(ApartmentState apartment = ApartmentState.MTA)
    {
        thread = new Thread(() => { foreach (var action in queue.GetConsumingEnumerable()) action(); }) { IsBackground = true, Name = "Switchcraft accessibility" };
        thread.SetApartmentState(apartment); thread.Start();
    }
    public async Task<T?> Invoke<T>(Func<Func<bool>, T?> action, int timeout = 300) where T : class
    {
        var completion = new TaskCompletionSource<T?>(TaskCreationOptions.RunContinuationsAsynchronously);
        long deadline = Environment.TickCount64 + timeout;
        bool Active() => Environment.TickCount64 < deadline && !queue.IsAddingCompleted;
        try
        {
            if (!queue.TryAdd(() => { try { completion.TrySetResult(Active() ? action(Active) : null); } catch { completion.TrySetResult(null); } })) return null;
        }
        catch (InvalidOperationException) { return null; }
        if (await Task.WhenAny(completion.Task, Task.Delay(timeout)).ConfigureAwait(false) != completion.Task) return null;
        return await completion.Task.ConfigureAwait(false);
    }
    public void Dispose() { queue.CompleteAdding(); }
}

internal sealed record EditorSnapshot((IntPtr Window, IntPtr Focus) Focus, string Text,
    long Document, long Start, long End, long Caret, long Anchor, AutomationElement? Element = null, TextPatternRange? Range = null)
{
    public bool Scintilla => Element == null;
}

internal sealed class EditorAccess : IDisposable
{
    private readonly AutomationWorker automation = new();
    // Browsers store a typed trailing space in contenteditable fields (Gmail, Telegram Web…) as U+00A0.
    internal static string Canonical(string text) => text.Replace("\r\n", "\n").Replace('\u00A0', ' ');
    private static bool Safe(AutomationElement? element)
    {
        if (element == null || element.Current.IsPassword || !element.Current.IsEnabled) return false;
        if (element.TryGetCurrentPattern(ValuePattern.Pattern, out var value) && ((ValuePattern)value).Current.IsReadOnly) return false;
        return element.TryGetCurrentPattern(TextPattern.Pattern, out var text) && !Equals(((TextPattern)text).DocumentRange.GetAttributeValue(TextPattern.IsReadOnlyAttribute), true);
    }
    private static readonly object Writable = new(), NotWritable = new();
    /// <summary>Null when the answer is unknown: the application did not answer in time (Chrome is busy right after a new
    /// tab opens), focus moved during the check, or the element vanished. Unknown is not "not writable".</summary>
    public async Task<bool?> IsWritable((IntPtr Window, IntPtr Focus) focus)
    {
        if (Win32.ClassName(focus.Focus) == "Scintilla") return Win32.IsWritableScintilla(focus, "Scintilla");
        var answer = await automation.Invoke<object>(_ => focus != Win32.Focus() ? null : Safe(AutomationElement.FocusedElement) ? Writable : NotWritable);
        return answer == Writable ? true : answer == NotWritable ? false : null;
    }
    /// <param name="afterPaste">Verifying our own paste: an inline completion that the editor selected after the
    /// caret (Chrome's address bar) is allowed, and the text up to its start is checked.</param>
    /// <param name="pasted">The field we pasted into. Chrome moves UIA focus to its suggestion list after a pasted
    /// address, so verification reads that same field rather than whatever element reports focus.</param>
    public Task<EditorSnapshot?> Capture((IntPtr Window, IntPtr Focus) focus, string? tail = null, bool afterPaste = false, AutomationElement? pasted = null)
    {
        if (Win32.ClassName(focus.Focus) == "Scintilla")
        {
            try { return Task.FromResult(CaptureScintilla(focus, tail)); } catch { return Task.FromResult<EditorSnapshot?>(null); }
        }
        return automation.Invoke<EditorSnapshot>(_ =>
        {
            if (focus != Win32.Focus()) return null;
            var element = afterPaste && pasted != null ? pasted : AutomationElement.FocusedElement; if (!Safe(element)) return null;
            var pattern = (TextPattern)element.GetCurrentPattern(TextPattern.Pattern);
            var ranges = pattern.GetSelection(); if (ranges.Length != 1) return null;
            var range = ranges[0].Clone();
            if (tail != null)
            {
                if (range.CompareEndpoints(TextPatternRangeEndpoint.Start, range, TextPatternRangeEndpoint.End) != 0)
                {
                    if (!afterPaste) return null;
                    range.MoveEndpointByRange(TextPatternRangeEndpoint.End, range, TextPatternRangeEndpoint.Start);
                }
                range.MoveEndpointByUnit(TextPatternRangeEndpoint.Start, TextUnit.Character, -Canonical(tail).Length);
            }
            var text = range.GetText(tail == null ? 100001 : tail.Length + 2);
            if (text.Length == 0 || text.Length > 100000 || tail != null && Canonical(text) != Canonical(tail)) return null;
            return new(focus, text, 0, 0, 0, 0, 0, element, range);
        });
    }
    private static EditorSnapshot? CaptureScintilla((IntPtr Window, IntPtr Focus) focus, string? tail)
    {
        if (focus != Win32.Focus() || !Win32.IsWritableScintilla(focus, "Scintilla")) return null;
        long Get(uint message) => Win32.Message(focus.Focus, message);
        if (Get(2570) != 1 || Get(2372) != 0 || Get(2581) != 0 || Get(2583) != 0) return null; // Multiple carets/rectangular/virtual space.
        long document = Get(2357), caret = Get(2008), anchor = Get(2009);
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        int cp = (int)Get(2137); var encoding = Encoding.GetEncoding(cp == 0 ? (int)Win32.GetACP() : cp, EncoderFallback.ExceptionFallback, DecoderFallback.ExceptionFallback);
        long start = Math.Min(caret, anchor), end = Math.Max(caret, anchor);
        if (tail != null)
        {
            if (caret != anchor) return null;
            start = end - encoding.GetByteCount(tail);
            // Windows editors may store an Enter as CRLF.
            if (tail.EndsWith('\n') && !tail.EndsWith("\r\n") && end >= 2 && Win32.Message(focus.Focus, 2007, end - 2) == 13) { tail = tail[..^1] + "\r\n"; start--; }
        }
        if (start < 0 || end <= start || end - start > 400000) return null;
        var text = encoding.GetString(Win32.ReadScintillaRange(focus.Focus, start, (int)(end - start)));
        if (tail != null && Canonical(text) != Canonical(tail)) return null;
        if (Get(2357) != document || Get(2008) != caret || Get(2009) != anchor || focus != Win32.Focus()) return null;
        return new(focus, text, document, start, end, caret, anchor);
    }
    public async Task<bool> SelectIfCurrent(EditorSnapshot snapshot, Func<bool> valid)
    {
        if (snapshot.Scintilla)
        {
            try
            {
                var current = CaptureScintilla(snapshot.Focus, snapshot.Caret == snapshot.Anchor ? snapshot.Text : null);
                if (current == null || current.Document != snapshot.Document || current.Start != snapshot.Start || current.End != snapshot.End || current.Text != snapshot.Text || !valid()) return false;
                Win32.Message(snapshot.Focus.Focus, 2160, snapshot.Start, snapshot.End);
                return valid() && Win32.Message(snapshot.Focus.Focus, 2008) == snapshot.End && Win32.Message(snapshot.Focus.Focus, 2009) == snapshot.Start;
            }
            catch { return false; }
        }
        return await automation.Invoke<object>(active =>
        {
            if (!valid() || snapshot.Focus != Win32.Focus()) return null;
            var element = AutomationElement.FocusedElement;
            if (!Safe(element) || !Automation.Compare(element, snapshot.Element)) return null;
            var ranges = ((TextPattern)element.GetCurrentPattern(TextPattern.Pattern)).GetSelection();
            if (ranges.Length != 1 || ranges[0].CompareEndpoints(TextPatternRangeEndpoint.End, snapshot.Range!, TextPatternRangeEndpoint.End) != 0) return null;
            var selected = ranges[0].GetText(100001);
            if (selected.Length > 0 && selected != snapshot.Text || snapshot.Range!.GetText(100001) != snapshot.Text || !active() || !valid()) return null;
            snapshot.Range.Select();
            // Chromium (Chrome, Edge, Electron) applies a UIA selection asynchronously: GetSelection can still
            // report the caret right after Select(). Wait briefly for the exact range; never paste without it.
            for (int attempt = 0; attempt < 12 && active() && valid(); attempt++)
            {
                if (attempt > 0) Thread.Sleep(10);
                var confirmed = ((TextPattern)element.GetCurrentPattern(TextPattern.Pattern)).GetSelection();
                if (confirmed.Length == 1 && confirmed[0].GetText(100001) == snapshot.Text
                    && confirmed[0].CompareEndpoints(TextPatternRangeEndpoint.Start, snapshot.Range, TextPatternRangeEndpoint.Start) == 0
                    && confirmed[0].CompareEndpoints(TextPatternRangeEndpoint.End, snapshot.Range, TextPatternRangeEndpoint.End) == 0)
                    return active() && valid() ? new object() : null;
            }
            return null;
        }) != null;
    }
    /// <summary>Puts focus back on the corrected field when its window moved focus to another element of the same window
    /// (Windows 11 Notepad does so after a paste). Used only for an undo the user asked for; never moves between windows.</summary>
    public async Task<bool> RefocusField(EditorSnapshot snapshot) => snapshot.Element != null && await automation.Invoke<object>(_ =>
    {
        if (Win32.Focus().Window != snapshot.Focus.Window) return null;
        snapshot.Element.SetFocus();
        return new object();
    }) != null;
    /// <summary>VS Code (Monaco) outside screen-reader mode exposes an empty edit context instead of its text, so
    /// corrections there cannot be verified and are skipped.</summary>
    public async Task<bool> IsHiddenEditorText((IntPtr Window, IntPtr Focus) focus) => await automation.Invoke<object>(_ =>
    {
        if (focus != Win32.Focus()) return null;
        var element = AutomationElement.FocusedElement;
        if (element == null || element.Current.ClassName != "native-edit-context") return null;
        return element.TryGetCurrentPattern(ValuePattern.Pattern, out var value) && ((ValuePattern)value).Current.Value.Length == 0 ? new object() : null;
    }) != null;
    /// <summary>After an aborted replacement, puts the caret back at the word's end if our selection is (still or
    /// belatedly, in Chromium) in place, so the next typed key cannot overwrite the word.</summary>
    public async Task CollapseIfSelected(EditorSnapshot snapshot)
    {
        if (snapshot.Scintilla)
        {
            try
            {
                if (snapshot.Focus == Win32.Focus() && Win32.Message(snapshot.Focus.Focus, 2143) == snapshot.Start && Win32.Message(snapshot.Focus.Focus, 2145) == snapshot.End)
                    Win32.Message(snapshot.Focus.Focus, 2160, snapshot.End, snapshot.End);
            }
            catch { }
            return;
        }
        await automation.Invoke<object>(active =>
        {
            if (snapshot.Focus != Win32.Focus()) return null;
            var element = AutomationElement.FocusedElement;
            if (element == null || !Automation.Compare(element, snapshot.Element)) return null;
            var pattern = (TextPattern)element.GetCurrentPattern(TextPattern.Pattern);
            bool Is(TextPatternRange[] ranges, TextPatternRangeEndpoint startOf) => ranges.Length == 1
                && ranges[0].CompareEndpoints(TextPatternRangeEndpoint.Start, snapshot.Range!, startOf) == 0
                && ranges[0].CompareEndpoints(TextPatternRangeEndpoint.End, snapshot.Range!, TextPatternRangeEndpoint.End) == 0;
            for (int attempt = 0; attempt < 15 && active(); attempt++)
            {
                if (attempt > 0) Thread.Sleep(10);
                if (!Is(pattern.GetSelection(), TextPatternRangeEndpoint.Start)) continue;
                var caret = snapshot.Range!.Clone();
                caret.MoveEndpointByRange(TextPatternRangeEndpoint.Start, caret, TextPatternRangeEndpoint.End);
                caret.Select();
                for (int wait = 0; wait < 12 && active(); wait++)
                {
                    if (Is(pattern.GetSelection(), TextPatternRangeEndpoint.End)) return new object();
                    Thread.Sleep(10);
                }
                return null;
            }
            return null;
        }, timeout: 400);
    }
    /// <summary>True when our selection is still in place over exactly the original text: a paste was sent but did not
    /// change anything (Windows 11 Notepad occasionally ignores one of several quick pastes).</summary>
    public async Task<bool> StillSelected(EditorSnapshot snapshot)
    {
        if (snapshot.Scintilla)
        {
            try
            {
                return snapshot.Focus == Win32.Focus() && Win32.Message(snapshot.Focus.Focus, 2143) == snapshot.Start && Win32.Message(snapshot.Focus.Focus, 2145) == snapshot.End
                    && CaptureScintillaRange(snapshot) == snapshot.Text;
            }
            catch { return false; }
        }
        return await automation.Invoke<object>(_ =>
        {
            if (snapshot.Focus != Win32.Focus()) return null;
            var element = AutomationElement.FocusedElement;
            if (element == null || !Automation.Compare(element, snapshot.Element)) return null;
            var ranges = ((TextPattern)element.GetCurrentPattern(TextPattern.Pattern)).GetSelection();
            return ranges.Length == 1 && ranges[0].CompareEndpoints(TextPatternRangeEndpoint.Start, snapshot.Range!, TextPatternRangeEndpoint.Start) == 0
                && ranges[0].CompareEndpoints(TextPatternRangeEndpoint.End, snapshot.Range!, TextPatternRangeEndpoint.End) == 0
                && Canonical(ranges[0].GetText(snapshot.Text.Length + 2)) == Canonical(snapshot.Text) ? new object() : null;
        }) != null;
    }
    private static string CaptureScintillaRange(EditorSnapshot snapshot)
    {
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        int cp = (int)Win32.Message(snapshot.Focus.Focus, 2137);
        var encoding = Encoding.GetEncoding(cp == 0 ? (int)Win32.GetACP() : cp, EncoderFallback.ExceptionFallback, DecoderFallback.ExceptionFallback);
        return encoding.GetString(Win32.ReadScintillaRange(snapshot.Focus.Focus, snapshot.Start, (int)(snapshot.End - snapshot.Start)));
    }
    public void Dispose() => automation.Dispose();
}
