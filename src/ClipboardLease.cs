using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;

namespace Switchcraft;

// Used only by UIA editors whose Unicode packet handling is unreliable. All
// clipboard access stays on a bounded STA worker and restoration respects ownership.
internal sealed class ClipboardLease
{
    private readonly IDataObject backup;
    private static readonly AutomationWorker worker = new(ApartmentState.STA);
    private readonly uint owned;
    private ClipboardLease(IDataObject backup, uint owned) { this.backup = backup; this.owned = owned; }
    public bool IsOwned => Win32.GetClipboardSequenceNumber() == owned;
    /// <summary>The locale whose 8-bit code page can hold the text: Ukrainian or Russian for Cyrillic, otherwise English.</summary>
    internal static int TextLocale(string text) => text.Any(ch => ch is 'і' or 'ї' or 'є' or 'ґ' or 'І' or 'Ї' or 'Є' or 'Ґ') ? 0x0422
        : text.Any(ch => ch is >= 'А' and <= 'я' or 'ё' or 'Ё') ? 0x0419 : 0x0409;
    /// <summary>Why the last acquisition failed, as a category for diagnostics (never clipboard content).</summary>
    internal static string LastFailure { get; private set; } = "";
    public static async Task<ClipboardLease?> Acquire(string text, Func<bool> valid)
    {
        // Clipboard watchers (messengers, screenshot tools) open the clipboard right after every change, including
        // the restore of the previous correction. Wait for them a little longer, but only while the word is current.
        // Bounded: the user's keys are held meanwhile.
        for (long until = Environment.TickCount64 + 350, i = 0; Environment.TickCount64 < until && valid(); i++)
        {
            var lease = await AcquireOnce(text, valid).ConfigureAwait(false);
            if (lease != null) return lease;
            await Task.Delay((int)Math.Min(25 + 15 * i, 120)).ConfigureAwait(false);
        }
        if (!valid()) LastFailure = "stale";
        return null;
    }
    private static async Task<ClipboardLease?> AcquireOnce(string text, Func<bool> valid)
    {
        long deadline = Environment.TickCount64 + 500;
        LastFailure = "busy";
        return await worker.Invoke<ClipboardLease>(active =>
        {
            if (!valid() || Environment.TickCount64 >= deadline) { LastFailure = "stale"; return null; }
            uint sequence = Win32.GetClipboardSequenceNumber();
            var original = Clipboard.GetDataObject(); var copy = new DataObject();
            if (original != null) foreach (var format in original.GetFormats(false))
            {
                var value = original.GetData(format, false); if (value != null) copy.SetData(format, value, false);
            }
            if (!active() || !valid() || Environment.TickCount64 >= deadline || sequence != Win32.GetClipboardSequenceNumber()) { LastFailure = "changed_while_saving"; return null; }
            var data = new DataObject(); data.SetText(text);
            // An editor falls back to 8-bit text when the Unicode copy is briefly unavailable (clipboard watchers such as
            // messengers open the clipboard right after every change). Windows converts that copy with the clipboard
            // locale, taken from this thread's keyboard layout, which may be English just now: Cyrillic would become "?".
            data.SetData(DataFormats.Locale, new MemoryStream(BitConverter.GetBytes(TextLocale(text))));
            data.SetData("CanIncludeInClipboardHistory", new MemoryStream(BitConverter.GetBytes(0)));
            data.SetData("CanUploadToCloudClipboard", new MemoryStream(BitConverter.GetBytes(0)));
            uint? published = null;
            bool accepted = false;
            try
            {
                Clipboard.SetDataObject(data, true);
                published = Win32.GetClipboardSequenceNumber();
                if (!active() || !valid() || Environment.TickCount64 >= deadline) return null;
                // Verify the persisted Unicode format itself. Automatic format
                // conversion can hide a failed Unicode read behind lossy ANSI text.
                // A lease is usable only before the editor selection is changed.
                var persisted = Clipboard.GetDataObject()?.GetData(DataFormats.UnicodeText, false) as string;
                if (!active() || !valid() || Environment.TickCount64 >= deadline
                    || published.Value != Win32.GetClipboardSequenceNumber()
                    || !string.Equals(persisted, text, StringComparison.Ordinal)) { LastFailure = "not_persisted"; return null; }
                accepted = true;
                return new ClipboardLease(copy, published.Value);
            }
            finally
            {
                // Failed verification (including a read that outlived its deadline)
                // must not leave our temporary payload behind or overwrite a newer copy.
                if (!accepted && published.HasValue && published.Value == Win32.GetClipboardSequenceNumber())
                    Clipboard.SetDataObject(copy, true);
            }
        }, 500).ConfigureAwait(false);
    }
    public async Task<bool> Restore()
    {
        for (int i = 0; i < 3; i++)
        {
            if (!IsOwned) return true;
            if (await worker.Invoke<object>(active => { if (active() && IsOwned) Clipboard.SetDataObject(backup, true); return new object(); }, 500).ConfigureAwait(false) != null) return true;
            await Task.Delay(25).ConfigureAwait(false);
        }
        return false;
    }
}
