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
    public async Task<bool> IsWritable((IntPtr Window, IntPtr Focus) focus)
    {
        if (Win32.ClassName(focus.Focus) == "Scintilla") return Win32.IsWritableScintilla(focus, "Scintilla");
        return await automation.Invoke<object>(_ => focus == Win32.Focus() && Safe(AutomationElement.FocusedElement) ? new object() : null) != null;
    }
    public Task<EditorSnapshot?> Capture((IntPtr Window, IntPtr Focus) focus, string? tail = null)
    {
        if (Win32.ClassName(focus.Focus) == "Scintilla")
        {
            try { return Task.FromResult(CaptureScintilla(focus, tail)); } catch { return Task.FromResult<EditorSnapshot?>(null); }
        }
        return automation.Invoke<EditorSnapshot>(_ =>
        {
            if (focus != Win32.Focus()) return null;
            var element = AutomationElement.FocusedElement; if (!Safe(element)) return null;
            var pattern = (TextPattern)element.GetCurrentPattern(TextPattern.Pattern);
            var ranges = pattern.GetSelection(); if (ranges.Length != 1) return null;
            var range = ranges[0].Clone();
            if (tail != null)
            {
                if (range.CompareEndpoints(TextPatternRangeEndpoint.Start, range, TextPatternRangeEndpoint.End) != 0) return null;
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
    public void Dispose() => automation.Dispose();
}
