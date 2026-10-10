using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows.Input;
using System.Windows.Interop;

namespace Switchcraft;

public sealed class HotkeyRegistry : IDisposable
{
    private readonly HwndSource source;
    private readonly Dictionary<int, string> actions = new();
    public event Action<string>? Pressed;
    public HotkeyRegistry(HwndSource source) { this.source = source; source.AddHook(WndProc); }
    public List<string> Apply(Dictionary<string, string> bindings)
    {
        foreach (var id in actions.Keys) Win32.UnregisterHotKey(source.Handle, id);
        actions.Clear(); var failures = new List<string>(); int next = 500;
        foreach (var item in bindings)
        {
            if (string.IsNullOrWhiteSpace(item.Value)) continue;
            if (!TryParse(item.Value, out uint modifiers, out uint vk) || !Win32.RegisterHotKey(source.Handle, next, modifiers | 0x4000, vk)) failures.Add(item.Value);
            else actions[next] = item.Key;
            next++;
        }
        return failures;
    }
    public static bool TryParse(string value, out uint modifiers, out uint vk)
    {
        modifiers = 0; vk = 0;
        try
        {
            var parts = value.Split('+', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
            foreach (var part in parts.Take(parts.Length - 1))
                modifiers |= part.ToLowerInvariant() switch { "ctrl" => 2u, "alt" => 1u, "shift" => 4u, _ => 0u };
            var last = parts.LastOrDefault() ?? "";
            var key = last.ToLowerInvariant() switch { "backspace" => Key.Back, "space" => Key.Space, "enter" => Key.Return, _ => (Key)new KeyConverter().ConvertFromString(last)! };
            vk = (uint)KeyInterop.VirtualKeyFromKey(key);
            return modifiers != 0 && vk != 0;
        }
        catch { return false; }
    }
    public static bool Matches(string value, uint key)
    {
        if (!TryParse(value, out var mods, out var vk) || key != vk) return false;
        return ((mods & 1) != 0) == Win32.Down(0x12) && ((mods & 2) != 0) == Win32.Down(0x11) && ((mods & 4) != 0) == Win32.Down(0x10);
    }
    private IntPtr WndProc(IntPtr hwnd, int message, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (message == 0x0312 && actions.TryGetValue(wParam.ToInt32(), out var action)) { handled = true; Pressed?.Invoke(action); }
        return IntPtr.Zero;
    }
    public void Dispose() { foreach (var id in actions.Keys) Win32.UnregisterHotKey(source.Handle, id); source.RemoveHook(WndProc); }
}
