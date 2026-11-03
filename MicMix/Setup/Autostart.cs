using Microsoft.Win32;

namespace MicMix.Setup;

/// <summary>"Запускать вместе с Windows": a per-user Run entry that starts MicMix in the tray.</summary>
public static class Autostart
{
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ValueName = "MicMix";

    public static bool IsEnabled
    {
        get
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKey);
            return key?.GetValue(ValueName) is string;
        }
    }

    public static void Set(bool enabled)
    {
        using var key = Registry.CurrentUser.CreateSubKey(RunKey);
        if (enabled)
            key.SetValue(ValueName, $"\"{Environment.ProcessPath}\" --minimized");
        else
            key.DeleteValue(ValueName, throwOnMissingValue: false);
    }

    /// <summary>If the exe was moved since autostart was turned on, point the entry at the new location.</summary>
    public static void RefreshPath()
    {
        if (IsEnabled) Set(true);
    }
}
