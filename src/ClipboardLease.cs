using System;
using System.IO;
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
    public static async Task<ClipboardLease?> Acquire(string text, Func<bool> valid)
    {
        for (int i = 0; i < 3 && valid(); i++)
        {
            var lease = await AcquireOnce(text, valid).ConfigureAwait(false);
            if (lease != null) return lease;
            await Task.Delay(25).ConfigureAwait(false);
        }
        return null;
    }
    private static async Task<ClipboardLease?> AcquireOnce(string text, Func<bool> valid)
    {
        long deadline = Environment.TickCount64 + 500;
        return await worker.Invoke<ClipboardLease>(active =>
        {
            if (!valid() || Environment.TickCount64 >= deadline) return null;
            uint sequence = Win32.GetClipboardSequenceNumber();
            var original = Clipboard.GetDataObject(); var copy = new DataObject();
            if (original != null) foreach (var format in original.GetFormats(false))
            {
                var value = original.GetData(format, false); if (value != null) copy.SetData(format, value, false);
            }
            if (!active() || !valid() || Environment.TickCount64 >= deadline || sequence != Win32.GetClipboardSequenceNumber()) return null;
            var data = new DataObject(); data.SetText(text);
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
                    || !string.Equals(persisted, text, StringComparison.Ordinal)) return null;
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
