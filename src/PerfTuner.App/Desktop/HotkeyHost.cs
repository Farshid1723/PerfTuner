using System.Runtime.InteropServices;
using System.Windows.Interop;

namespace PerfTuner.App.Desktop;

/// <summary>System-wide keyboard shortcuts (RegisterHotKey) on a hidden message-only window.</summary>
public sealed partial class HotkeyHost : IDisposable
{
    public const uint Alt = 0x1, Control = 0x2, Shift = 0x4, Win = 0x8;
    private const uint NoRepeat = 0x4000;
    private const int WmHotkey = 0x0312;

    private readonly HwndSource _window;
    private readonly Dictionary<int, Action> _actions = [];

    public HotkeyHost()
    {
        _window = new HwndSource(new HwndSourceParameters("PerfTunerHotkeys") { Width = 0, Height = 0, WindowStyle = 0, ParentWindow = -3 /* HWND_MESSAGE */ });
        _window.AddHook(WndProc);
    }

    /// <summary>Registers one shortcut; false when another program already owns it.</summary>
    public bool Register(int id, uint modifiers, uint key, Action action)
    {
        Unregister(id);
        if (!RegisterHotKey(_window.Handle, id, modifiers | NoRepeat, key)) { return false; }
        _actions[id] = action;
        return true;
    }

    public void Unregister(int id)
    {
        if (_actions.Remove(id)) { UnregisterHotKey(_window.Handle, id); }
    }

    public void UnregisterRange(int first, int last)
    {
        foreach (var id in _actions.Keys.Where(k => k >= first && k <= last).ToList()) { Unregister(id); }
    }

    private nint WndProc(nint hwnd, int msg, nint wParam, nint lParam, ref bool handled)
    {
        if (msg == WmHotkey && _actions.TryGetValue((int)wParam, out var action))
        {
            handled = true;
            action();
        }
        return 0;
    }

    public void Dispose()
    {
        foreach (var id in _actions.Keys.ToList()) { Unregister(id); }
        _window.RemoveHook(WndProc);
        _window.Dispose();
    }

    [LibraryImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool RegisterHotKey(nint hwnd, int id, uint modifiers, uint key);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool UnregisterHotKey(nint hwnd, int id);
}

/// <summary>Sends Windows' own Win+Ctrl+←/→ desktop shortcut (SendInput), after the user has let go of the hotkey's modifiers.</summary>
public static partial class DesktopKeys
{
    private const ushort VkControl = 0x11, VkMenu = 0x12, VkShift = 0x10, VkLWin = 0x5B, VkRWin = 0x5C, VkLeft = 0x25, VkRight = 0x27;
    private const uint KeyUp = 0x2, Extended = 0x1;

    /// <summary>Moves |steps| desktops left (negative) or right (positive).</summary>
    public static async Task StepAsync(int steps)
    {
        if (steps == 0) { return; }
        await WaitForModifiersReleasedAsync().ConfigureAwait(false);
        var arrow = steps > 0 ? VkRight : VkLeft;
        for (var i = 0; i < Math.Abs(steps); i++)
        {
            Send([(VkLWin, 0), (VkControl, 0), (arrow, Extended), (arrow, Extended | KeyUp), (VkControl, KeyUp), (VkLWin, KeyUp)]);
            await Task.Delay(120).ConfigureAwait(false);
        }
    }

    private static async Task WaitForModifiersReleasedAsync()
    {
        for (var i = 0; i < 40; i++)
        {
            if (!Down(VkMenu) && !Down(VkShift) && !Down(VkControl) && !Down(VkLWin) && !Down(VkRWin)) { return; }
            await Task.Delay(50).ConfigureAwait(false);
        }
    }

    private static bool Down(ushort vk) => (GetAsyncKeyState(vk) & 0x8000) != 0;

    private static unsafe void Send(ReadOnlySpan<(ushort Key, uint Flags)> keys)
    {
        var inputs = stackalloc Input[keys.Length];
        for (var i = 0; i < keys.Length; i++)
        {
            inputs[i] = new Input { Type = 1, Key = new KeyboardInput { Vk = keys[i].Key, Flags = keys[i].Flags } };
        }
        SendInput((uint)keys.Length, inputs, sizeof(Input));
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct KeyboardInput
    {
        public ushort Vk;
        public ushort Scan;
        public uint Flags;
        public uint Time;
        public nint ExtraInfo;
    }

    // INPUT is a union sized for MOUSEINPUT (the largest member): pad so sizeof matches Windows'.
    [StructLayout(LayoutKind.Explicit, Size = 40)]
    private struct Input
    {
        [FieldOffset(0)] public uint Type;
        [FieldOffset(8)] public KeyboardInput Key;
    }

    [LibraryImport("user32.dll")]
    private static unsafe partial uint SendInput(uint count, Input* inputs, int size);

    [LibraryImport("user32.dll")]
    private static partial short GetAsyncKeyState(int key);
}
