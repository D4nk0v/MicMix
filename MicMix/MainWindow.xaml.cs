using System.ComponentModel;
using System.Windows;
using System.Windows.Input;
using MicMix.Hotkeys;
using MicMix.ViewModels;

namespace MicMix;

public partial class MainWindow : Window
{
    private readonly MainViewModel _vm;

    /// <summary>When false, closing the window just hides it to the tray.</summary>
    public bool AllowClose { get; set; }

    public MainWindow(MainViewModel vm)
    {
        InitializeComponent();
        DataContext = _vm = vm;
        PreviewKeyDown += CaptureHotkey;
        Deactivated += (_, _) => _vm.EndCapture(HotkeyModifiers.None, null); // clicked away: cancel
    }

    protected override void OnClosing(CancelEventArgs e)
    {
        if (!AllowClose)
        {
            e.Cancel = true;
            Hide();
        }
        base.OnClosing(e);
    }

    /// <summary>While a hotkey button is waiting for a combo, the next key press becomes the binding.</summary>
    private void CaptureHotkey(object sender, KeyEventArgs e)
    {
        if (_vm.Capturing == null) return;
        e.Handled = true;

        var key = e.Key switch { Key.System => e.SystemKey, Key.ImeProcessed => e.ImeProcessedKey, _ => e.Key };
        if (key is Key.LeftCtrl or Key.RightCtrl or Key.LeftAlt or Key.RightAlt or Key.LeftShift or Key.RightShift
            or Key.LWin or Key.RWin or Key.None)
            return; // wait for the actual key

        var mods = HotkeyModifiers.None;
        if (Keyboard.Modifiers.HasFlag(ModifierKeys.Control)) mods |= HotkeyModifiers.Control;
        if (Keyboard.Modifiers.HasFlag(ModifierKeys.Alt)) mods |= HotkeyModifiers.Alt;
        if (Keyboard.Modifiers.HasFlag(ModifierKeys.Shift)) mods |= HotkeyModifiers.Shift;
        if (Keyboard.Modifiers.HasFlag(ModifierKeys.Windows)) mods |= HotkeyModifiers.Win;

        if (mods == HotkeyModifiers.None)
        {
            if (key == Key.Escape) { _vm.EndCapture(mods, null); return; }
            if (key is Key.Back or Key.Delete) { _vm.EndCapture(mods, 0); return; }
            // A bare letter would be swallowed system-wide and break typing everywhere.
            if (!HotkeyBinding.AllowedWithoutModifier(key)) return;
        }
        _vm.EndCapture(mods, KeyInterop.VirtualKeyFromKey(key));
    }
}
