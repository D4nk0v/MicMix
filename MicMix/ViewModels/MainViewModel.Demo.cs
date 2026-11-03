using System.IO;
using MicMix.Audio;

namespace MicMix.ViewModels;

/// <summary>
/// A frozen, made-up state for README screenshots: no audio engine, no hotkeys, nothing read from
/// or written to the user's settings, and no real device names.
/// </summary>
public sealed partial class MainViewModel
{
    public static MainViewModel CreateDemo(Page page)
    {
        var settings = new Settings { FirstRunDone = true, ObsEnabled = true };
        settings.Voice.ApplyPreset(VoicePreset.SoloCast);
        settings.VoiceFx.ApplyPreset(VoiceFxPreset.Robot);
        settings.VoiceFx.Enabled = true;

        var vm = new MainViewModel(settings, live: false) { Page = page };
        vm.FillDemo();
        return vm;
    }

    private void FillDemo()
    {
        var mic = new DeviceItem("demo-mic", "Микрофон (USB Microphone)");
        var cable = new DeviceItem("demo-cable", "CABLE Input (VB-Audio Virtual Cable)");
        var phones = new DeviceItem("demo-phones", "Наушники (Realtek USB Audio)");
        var spdif = new DeviceItem("demo-spdif", "Цифровой выход S/PDIF (Realtek)");
        _suppressDeviceSelection = true;
        Mics.Add(mic);
        Outputs.Add(cable);
        Outputs.Add(phones);
        ObsDevices.Add(spdif);
        SelectedMic = mic;
        SelectedOutput = cable;
        SelectedObsDevice = spdif;
        _suppressDeviceSelection = false;

        State = EngineState.Running;
        StatusText = "Работает → CABLE Input";
        ObsStatus = "голос: работает · программы: работает";
        VoiceActive = true;
        MicLevel = 0.62;
        OutputLevel = 0.74;
        AppsLevel = 0.48;

        string local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        string? discord = Directory.Exists(Path.Combine(local, "Discord"))
            ? Directory.GetDirectories(Path.Combine(local, "Discord"), "app-*").Select(d => Path.Combine(d, "Discord.exe")).FirstOrDefault(File.Exists)
            : null;
        (string key, string name, string? exe, bool enabled, bool playing, double volume, double level)[] apps =
        [
            ("chrome", "Google Chrome", @"C:\Program Files\Google\Chrome\Application\chrome.exe", true, true, 100, 0.48),
            ("spotify", "Spotify", Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), @"Spotify\Spotify.exe"), true, false, 80, 0),
            ("discord", "Discord", discord, false, true, 100, 0),
            ("steam", "Steam", @"C:\Program Files (x86)\Steam\steam.exe", false, false, 100, 0),
        ];
        foreach (var a in apps)
        {
            var item = new AppItemViewModel(a.key, a.name, a.enabled, (float)(a.volume / 100), _ => { })
            {
                IsRunning = true,
                IsPlaying = a.playing,
                Level = a.level,
                Icon = a.exe != null && File.Exists(a.exe) ? GetIcon(a.exe) : null,
                Status = a.enabled ? (a.playing ? "в микрофоне · играет" : "в микрофоне") : a.playing ? "играет" : "",
            };
            InsertSorted(item);
        }
    }
}
