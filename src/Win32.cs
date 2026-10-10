using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;

namespace Switchcraft;

internal static class Win32
{
    public const int WH_KEYBOARD_LL = 13, WH_MOUSE_LL = 14;
    public static readonly UIntPtr InjectionTag = new(0x53574346);
    public delegate IntPtr HookProc(int code, IntPtr message, IntPtr data);
    public delegate void WinEventProc(IntPtr hook, uint evt, IntPtr window, int obj, int child, uint thread, uint time);
    [DllImport("user32.dll")] public static extern IntPtr SetWinEventHook(uint min, uint max, IntPtr module, WinEventProc proc, uint process, uint thread, uint flags);
    [DllImport("user32.dll")] public static extern bool UnhookWinEvent(IntPtr hook);
    [DllImport("kernel32.dll")] private static extern IntPtr OpenProcess(uint access, bool inherit, uint id);
    [DllImport("kernel32.dll")] private static extern bool ReadProcessMemory(IntPtr process, IntPtr address, byte[] bytes, UIntPtr size, out UIntPtr read);
    [DllImport("kernel32.dll")] private static extern bool CloseHandle(IntPtr handle);
    [DllImport("kernel32.dll")] public static extern uint GetACP();
    [StructLayout(LayoutKind.Sequential)] public struct KeyData { public uint Vk, Scan, Flags, Time; public UIntPtr Extra; }
    [StructLayout(LayoutKind.Sequential)] public struct Point { public int X, Y; }
    [StructLayout(LayoutKind.Sequential)] public struct Rect { public int Left, Top, Right, Bottom; }
    [StructLayout(LayoutKind.Sequential)] public struct GuiInfo { public int Size; public uint Flags; public IntPtr Active, Focus, Capture, MenuOwner, MoveSize, Caret; public Rect CaretRect; }
    [StructLayout(LayoutKind.Sequential)] public struct Input { public uint Type; public InputUnion Data; }
    [StructLayout(LayoutKind.Explicit)] public struct InputUnion { [FieldOffset(0)] public KeyboardInput Keyboard; [FieldOffset(0)] public MouseInput Mouse; }
    [StructLayout(LayoutKind.Sequential)] public struct KeyboardInput { public ushort Vk, Scan; public uint Flags, Time; public UIntPtr Extra; }
    [StructLayout(LayoutKind.Sequential)] public struct MouseInput { public int X, Y; public uint MouseData, Flags, Time; public UIntPtr Extra; }
    [DllImport("user32.dll", SetLastError = true)] public static extern IntPtr SetWindowsHookEx(int id, HookProc proc, IntPtr module, uint thread);
    [DllImport("user32.dll")] public static extern bool UnhookWindowsHookEx(IntPtr hook);
    [DllImport("user32.dll")] public static extern IntPtr CallNextHookEx(IntPtr hook, int code, IntPtr message, IntPtr data);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] public static extern IntPtr GetModuleHandle(string? name);
    [DllImport("kernel32.dll")] public static extern uint GetCurrentThreadId();
    [DllImport("user32.dll")] public static extern bool PostThreadMessage(uint thread, uint message, IntPtr wParam, IntPtr lParam);
    [DllImport("user32.dll")] public static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] public static extern uint GetWindowThreadProcessId(IntPtr window, out uint process);
    [DllImport("user32.dll")] public static extern IntPtr GetKeyboardLayout(uint thread);
    [DllImport("user32.dll")] public static extern int GetKeyboardLayoutList(int count, [Out] IntPtr[]? list);
    [DllImport("user32.dll")] public static extern short GetAsyncKeyState(int key);
    [DllImport("user32.dll")] public static extern short GetKeyState(int key);
    [DllImport("user32.dll")] public static extern bool GetKeyboardState(byte[] state);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] public static extern int ToUnicodeEx(uint key, uint scan, byte[] state, StringBuilder text, int capacity, uint flags, IntPtr layout);
    [DllImport("user32.dll")] public static extern bool GetGUIThreadInfo(uint thread, ref GuiInfo info);
    [DllImport("user32.dll", EntryPoint = "GetWindowLongW")] public static extern int GetWindowLong(IntPtr window, int index);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] public static extern int GetClassName(IntPtr window, StringBuilder name, int size);
    [DllImport("user32.dll")] public static extern bool IsWindowEnabled(IntPtr window);
    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern IntPtr SendMessageTimeout(IntPtr window, uint message, UIntPtr wParam, IntPtr lParam, uint flags, uint timeout, out UIntPtr result);
    [DllImport("user32.dll")] public static extern IntPtr GetParent(IntPtr window);
    [DllImport("user32.dll", SetLastError = true)] private static extern uint SendInput(uint count, Input[] inputs, int size);
    [DllImport("user32.dll")] public static extern bool PostMessage(IntPtr window, uint message, IntPtr wParam, IntPtr lParam);
    [DllImport("user32.dll", SetLastError = true)] public static extern bool RegisterHotKey(IntPtr window, int id, uint modifiers, uint key);
    [DllImport("user32.dll")] public static extern bool UnregisterHotKey(IntPtr window, int id);
    [DllImport("user32.dll")] public static extern uint GetClipboardSequenceNumber();
    public static bool Down(int vk) => (GetAsyncKeyState(vk) & 0x8000) != 0;
    public static string ClassName(IntPtr window) { var name = new StringBuilder(128); GetClassName(window, name, name.Capacity); return name.ToString(); }
    public static long Message(IntPtr window, uint message, long w = 0, long l = 0)
    {
        if (SendMessageTimeout(window, message, new UIntPtr(unchecked((ulong)w)), new IntPtr(l), 3, 40, out var result) == IntPtr.Zero)
            throw new TimeoutException("Редактор не відповідає.");
        return unchecked((long)result.ToUInt64());
    }
    public static byte[] ReadScintillaRange(IntPtr window, long start, int count)
    {
        if (count < 0 || count > 400000) throw new InvalidOperationException("Виділення надто велике.");
        var result = new byte[count]; if (count == 0) return result;
        GetWindowThreadProcessId(window, out var pid);
        var process = OpenProcess(0x10, false, pid);
        if (process == IntPtr.Zero) throw new InvalidOperationException("Редактор не дозволив прочитати виділення.");
        try
        {
            var address = new IntPtr(Message(window, 2643, start, count)); // SCI_GETRANGEPOINTER; read-only and immediately consumed.
            if (address == IntPtr.Zero || !ReadProcessMemory(process, address, result, (UIntPtr)(uint)count, out var read) || read.ToUInt64() != (ulong)count)
                throw new InvalidOperationException("Текст редактора змінився.");
            return result;
        }
        finally { CloseHandle(process); }
    }
    public static bool ModifiersDown => Down(0x10) || Down(0x11) || Down(0x12) || Down(0x5B) || Down(0x5C);
    public static string AppName(IntPtr window)
    {
        GetWindowThreadProcessId(window, out var pid);
        try { using var process = Process.GetProcessById((int)pid); return process.ProcessName + ".exe"; } catch { return ""; }
    }
    public static (IntPtr Window, IntPtr Focus) Focus()
    {
        var window = GetForegroundWindow(); var thread = GetWindowThreadProcessId(window, out _);
        var gui = new GuiInfo { Size = Marshal.SizeOf<GuiInfo>() };
        return (window, GetGUIThreadInfo(thread, ref gui) ? gui.Focus : IntPtr.Zero);
    }
    public static bool IsWritableScintilla((IntPtr Window, IntPtr Focus) focus, string className)
    {
        // Notepad++ can expose no UIA text provider. Its actual focused Scintilla
        // HWND is an editor; never extend this fallback to arbitrary custom controls.
        if (focus.Focus == IntPtr.Zero || className != "Scintilla" || !IsWindowEnabled(focus.Focus)) return false;
        GetWindowThreadProcessId(focus.Window, out var owner);
        GetWindowThreadProcessId(focus.Focus, out var editorOwner);
        if (owner == 0 || owner != editorOwner) return false;
        const uint SCI_GETREADONLY = 2140, SMTO_BLOCK = 1, SMTO_ABORTIFHUNG = 2;
        return SendMessageTimeout(focus.Focus, SCI_GETREADONLY, UIntPtr.Zero, IntPtr.Zero,
            SMTO_BLOCK | SMTO_ABORTIFHUNG, 100, out var readOnly) != IntPtr.Zero && readOnly == UIntPtr.Zero;
    }
    public static string LayoutLanguage(IntPtr window)
    {
        var thread = GetWindowThreadProcessId(window, out _);
        var id = (long)GetKeyboardLayout(thread) & 0xffff;
        return id switch { 0x0422 => "uk", 0x0419 => "ru", _ => (id & 0x3ff) == 9 ? "en" : "" };
    }
    public static IntPtr LayoutFor(string language)
    {
        int count = GetKeyboardLayoutList(0, null); var layouts = new IntPtr[count]; GetKeyboardLayoutList(count, layouts);
        return layouts.FirstOrDefault(x => language switch { "uk" => ((long)x & 0xffff) == 0x0422, "ru" => ((long)x & 0xffff) == 0x0419, "en" => ((long)x & 0x3ff) == 9, _ => false });
    }
    public static void SwitchLayout(IntPtr window, string language)
    {
        var layout = LayoutFor(language); if (layout != IntPtr.Zero) PostMessage(window, 0x0050, IntPtr.Zero, layout);
    }
    public static Input Key(ushort vk, bool up = false) => new() { Type = 1, Data = new() { Keyboard = new() { Vk = vk, Flags = up ? 2u : 0u, Extra = InjectionTag } } };
    public static Input Unicode(char ch, bool up = false) => new() { Type = 1, Data = new() { Keyboard = new() { Scan = ch, Flags = up ? 6u : 4u, Extra = InjectionTag } } };
    public static IEnumerable<Input> TextInput(string text)
    {
        for (int i = 0; i < text.Length; i++)
        {
            if (text[i] is '\r' or '\n')
            {
                if (text[i] == '\r' && i + 1 < text.Length && text[i + 1] == '\n') i++;
                yield return Key(13); yield return Key(13, true);
            }
            else { yield return Unicode(text[i]); yield return Unicode(text[i], true); }
        }
    }
    public static bool Send(IEnumerable<Input> inputs) { var array = inputs.ToArray(); return SendInput((uint)array.Length, array, Marshal.SizeOf<Input>()) == array.Length; }
    public static bool Replace(int count, string text, ushort delimiter = 0)
    {
        var inputs = new List<Input>();
        for (int i = 0; i < count; i++) { inputs.Add(Key(8)); inputs.Add(Key(8, true)); }
        foreach (var ch in text) { inputs.Add(Unicode(ch)); inputs.Add(Unicode(ch, true)); }
        if (delimiter != 0) { inputs.Add(Key(delimiter)); inputs.Add(Key(delimiter, true)); }
        return Send(inputs);
    }
    public static bool Chord(ushort key) => Send(new[] { Key(0x11), Key(key), Key(key, true), Key(0x11, true) });
}
