using System.Runtime.InteropServices;
using System.Windows.Input;
using System.Windows.Interop;

namespace MicMix.Hotkeys;

public enum HotkeyAction
{
    ToggleMicMute,
    ToggleApps,
    ToggleVoiceChain,
    ToggleMonitor,
    ToggleVoiceFx,
}

[Flags]
public enum HotkeyModifiers : uint
{
    None = 0,
    Alt = 0x1,
    Control = 0x2,
    Shift = 0x4,
    Win = 0x8,
}

public sealed class HotkeyBinding
{
    public HotkeyAction Action { get; set; }
    public HotkeyModifiers Modifiers { get; set; }
    /// <summary>Win32 virtual-key code; 0 = not assigned.</summary>
    public int VirtualKey { get; set; }

    public bool IsAssigned => VirtualKey != 0;

    public override string ToString()
    {
        if (!IsAssigned) return "не назначено";
        var parts = new List<string>();
        if (Modifiers.HasFlag(HotkeyModifiers.Control)) parts.Add("Ctrl");
        if (Modifiers.HasFlag(HotkeyModifiers.Alt)) parts.Add("Alt");
        if (Modifiers.HasFlag(HotkeyModifiers.Shift)) parts.Add("Shift");
        if (Modifiers.HasFlag(HotkeyModifiers.Win)) parts.Add("Win");
        var key = KeyInterop.KeyFromVirtualKey(VirtualKey);
        parts.Add(key switch
        {
            >= Key.D0 and <= Key.D9 => ((int)(key - Key.D0)).ToString(),
            >= Key.NumPad0 and <= Key.NumPad9 => "Num " + (int)(key - Key.NumPad0),
            Key.OemPlus => "+",
            Key.OemMinus => "-",
            Key.Scroll => "Scroll Lock",
            _ => key.ToString(),
        });
        return string.Join(" + ", parts);
    }

    /// <summary>
    /// Keys that may be bound without a modifier: they're never used for typing, so grabbing them
    /// globally is harmless (F13–F24 are what Stream Deck–style pads usually send).
    /// </summary>
    public static bool AllowedWithoutModifier(Key key) =>
        key is >= Key.F1 and <= Key.F24 or Key.Pause or Key.Scroll;

    // Ctrl+Alt+M is commonly grabbed by other software and Ctrl+Shift+M is Discord's own mute, hence K.
    public static List<HotkeyBinding> Defaults() =>
    [
        new() { Action = HotkeyAction.ToggleMicMute, Modifiers = HotkeyModifiers.Control | HotkeyModifiers.Alt, VirtualKey = KeyInterop.VirtualKeyFromKey(Key.K) },
        new() { Action = HotkeyAction.ToggleApps, Modifiers = HotkeyModifiers.Control | HotkeyModifiers.Alt, VirtualKey = KeyInterop.VirtualKeyFromKey(Key.N) },
        new() { Action = HotkeyAction.ToggleVoiceChain, Modifiers = HotkeyModifiers.Control | HotkeyModifiers.Alt, VirtualKey = KeyInterop.VirtualKeyFromKey(Key.V) },
        new() { Action = HotkeyAction.ToggleMonitor, Modifiers = HotkeyModifiers.Control | HotkeyModifiers.Alt, VirtualKey = KeyInterop.VirtualKeyFromKey(Key.L) },
        new() { Action = HotkeyAction.ToggleVoiceFx, Modifiers = HotkeyModifiers.Control | HotkeyModifiers.Alt, VirtualKey = KeyInterop.VirtualKeyFromKey(Key.F) },
    ];
}

/// <summary>
/// System-wide hotkeys via RegisterHotKey on a hidden message-only window, so they work while the
/// main window is closed to the tray and while a game has focus. Must be used on the UI thread.
/// </summary>
public sealed class GlobalHotkeys : IDisposable
{
    private const int WmHotkey = 0x0312;
    private const uint ModNoRepeat = 0x4000;

    private readonly HwndSource _window;
    private readonly Dictionary<int, HotkeyAction> _registered = new();
    private int _nextId = 1;

    public event Action<HotkeyAction>? Pressed;

    public GlobalHotkeys()
    {
        _window = new HwndSource(new HwndSourceParameters("MicMix hotkeys")
        {
            ParentWindow = new IntPtr(-3), // HWND_MESSAGE
            WindowStyle = 0,
        });
        _window.AddHook(WndProc);
    }

    /// <summary>Replaces all registrations. Returns the actions whose combo is taken by another program.</summary>
    public List<HotkeyAction> Register(IEnumerable<HotkeyBinding> bindings)
    {
        UnregisterAll();
        var failed = new List<HotkeyAction>();
        foreach (var b in bindings.Where(b => b.IsAssigned))
        {
            int id = _nextId++;
            if (RegisterHotKey(_window.Handle, id, (uint)b.Modifiers | ModNoRepeat, (uint)b.VirtualKey))
                _registered[id] = b.Action;
            else
                failed.Add(b.Action);
        }
        return failed;
    }

    public void UnregisterAll()
    {
        foreach (var id in _registered.Keys) UnregisterHotKey(_window.Handle, id);
        _registered.Clear();
    }

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == WmHotkey && _registered.TryGetValue(wParam.ToInt32(), out var action))
        {
            handled = true;
            Pressed?.Invoke(action);
        }
        return IntPtr.Zero;
    }

    public void Dispose()
    {
        UnregisterAll();
        _window.Dispose();
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool RegisterHotKey(IntPtr hWnd, int id, uint fsModifiers, uint vk);

    [DllImport("user32.dll")]
    private static extern bool UnregisterHotKey(IntPtr hWnd, int id);
}
