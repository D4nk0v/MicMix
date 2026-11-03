using System.IO;
using System.Text.Json;
using MicMix.Audio;
using MicMix.Hotkeys;

namespace MicMix;

public sealed class AppSetting
{
    public string Key { get; set; } = "";
    public string DisplayName { get; set; } = "";
    public bool Enabled { get; set; }
    /// <summary>0..2, 1 = original level.</summary>
    public float Volume { get; set; } = 1f;
}

public sealed class Settings
{
    public string? MicDeviceId { get; set; }
    public string? OutputDeviceId { get; set; }
    /// <summary>0..2, 1 = original level.</summary>
    public float MicVolume { get; set; } = 1f;
    public bool MicMuted { get; set; }
    public List<AppSetting> Apps { get; set; } = new();
    public VoiceSettings Voice { get; set; } = new();
    public VoiceFxSettings VoiceFx { get; set; } = new();
    public AppBusSettings AppBus { get; set; } = new();
    public List<HotkeyBinding> Hotkeys { get; set; } = HotkeyBinding.Defaults();
    /// <summary>Blip in the headphones when a hotkey toggles something.</summary>
    public bool HotkeySounds { get; set; } = true;
    /// <summary>Separate voice / apps tracks for OBS via helper processes.</summary>
    public bool ObsEnabled { get; set; }
    /// <summary>The first-run setup (VB-Cable, mic, what to pick in Discord) was completed or skipped.</summary>
    public bool FirstRunDone { get; set; }
    /// <summary>Output nobody listens to that carries the OBS tracks (auto: S/PDIF).</summary>
    public string? ObsDeviceId { get; set; }
    /// <summary>Format of the saved file; older files are upgraded in <see cref="Load"/>.</summary>
    public int Version { get; set; }

    private const int CurrentVersion = 2;

    private static readonly string Dir = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "MicMix");
    private static readonly string FilePath = Path.Combine(Dir, "settings.json");

    public static Settings Load()
    {
        var settings = new Settings();
        try
        {
            if (File.Exists(FilePath))
                settings = JsonSerializer.Deserialize<Settings>(File.ReadAllText(FilePath)) ?? settings;
        }
        catch { /* corrupt file: start fresh rather than refuse to launch */ }
        settings.Upgrade();
        return settings;
    }

    private void Upgrade()
    {
        if (Version < 2)
        {
            // 1.0 processed the apps by default (leveler, low cut, ducking) and added them at 60%:
            // music came out flat, thin and pumping. Now they pass through untouched unless asked.
            AppBus.Leveler = AppBus.LowCut = AppBus.Ducking = false;
            foreach (var app in Apps.Where(a => a.Volume == 0.6f)) app.Volume = 1f;
        }
        Version = CurrentVersion;
    }

    public void Save()
    {
        try
        {
            Directory.CreateDirectory(Dir);
            string tmp = FilePath + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true }));
            File.Move(tmp, FilePath, overwrite: true);
        }
        catch { /* settings are a convenience; never crash audio over them */ }
    }
}
