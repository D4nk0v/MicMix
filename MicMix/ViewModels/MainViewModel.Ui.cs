using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Windows.Data;
using MicMix.Audio;
using MicMix.Setup;

namespace MicMix.ViewModels;

public enum Page { Mixer, Voice, Effects, Stream, Settings }

/// <summary>Navigation, first-run wizard and other purely presentational state.</summary>
public sealed partial class MainViewModel
{
    private Page _page = Page.Mixer;
    private bool _showWizard;
    private int _wizardStep = 1;
    private bool _showAllApps;
    private bool _isInstallingCable;
    private string _installStatus = "";
    private ListCollectionView _visibleApps = null!;

    private void InitUi()
    {
        _showWizard = !_settings.FirstRunDone;
        _visibleApps = new ListCollectionView(Apps)
        {
            Filter = o => _showAllApps || o is AppItemViewModel { Enabled: true } or AppItemViewModel { IsPlaying: true },
            IsLiveFiltering = true,
        };
        _visibleApps.LiveFilteringProperties.Add(nameof(AppItemViewModel.Enabled));
        _visibleApps.LiveFilteringProperties.Add(nameof(AppItemViewModel.IsPlaying));
        Apps.CollectionChanged += (_, _) => Raise(nameof(HiddenAppsCount));
        ((System.Collections.Specialized.INotifyCollectionChanged)_visibleApps).CollectionChanged += (_, _) => Raise(nameof(HiddenAppsCount));

        PropertyChanged += (_, e) =>
        {
            switch (e.PropertyName)
            {
                case nameof(SelectedMic): Raise(nameof(MicName)); break;
                case nameof(VbCableMissing): Raise(nameof(CableInstalled)); break;
            }
        };
        VoiceFx.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName is nameof(VoiceFxSettings.Preset) or nameof(VoiceFxSettings.Enabled)) Raise(nameof(VoiceFxSummary));
        };
        Autostart.RefreshPath();
    }

    #region Navigation

    public Page Page { get => _page; set => Set(ref _page, value); }

    #endregion

    #region Voice changer

    /// <summary>One line for the mixer: which preset is on, e.g. "сейчас: Робот".</summary>
    public string VoiceFxSummary
    {
        get
        {
            string name = VoiceFx.Preset switch
            {
                VoiceFxPreset.Deep => "Низкий",
                VoiceFxPreset.Demon => "Демон",
                VoiceFxPreset.High => "Высокий",
                VoiceFxPreset.Chipmunk => "Бурундук",
                VoiceFxPreset.Robot => "Робот",
                VoiceFxPreset.Alien => "Пришелец",
                VoiceFxPreset.Radio => "Рация",
                VoiceFxPreset.BadSignal => "Плохая связь",
                VoiceFxPreset.Choppy => "Прерывистый",
                _ => "свои настройки",
            };
            return VoiceFx.Enabled ? "сейчас: " + name : "выключен · пресет «" + name + "»";
        }
    }

    #endregion

    #region Mixer

    /// <summary>Apps worth showing by default: ticked or currently playing. "Показать все" lifts the filter.</summary>
    public ICollectionView VisibleApps => _visibleApps;

    public bool ShowAllApps
    {
        get => _showAllApps;
        set
        {
            if (!Set(ref _showAllApps, value)) return;
            _visibleApps.Refresh();
            Raise(nameof(HiddenAppsCount));
        }
    }

    public int HiddenAppsCount => Apps.Count - _visibleApps.Count;

    public string MicName => SelectedMic?.Name ?? "микрофон не выбран";

    private double _appsLevel;

    /// <summary>Loudest selected app right now (before the app-bus processing), for the "Программы" meters.</summary>
    public double AppsLevel { get => _appsLevel; private set => Set(ref _appsLevel, value); }

    #endregion

    #region Settings page

    public bool AutostartEnabled
    {
        get => Autostart.IsEnabled;
        set
        {
            try { Autostart.Set(value); } catch { /* registry locked down: leave as is */ }
            Raise(nameof(AutostartEnabled));
        }
    }

    public string VersionText =>
        "MicMix " + (Assembly.GetExecutingAssembly().GetName().Version?.ToString(3) ?? "");

    public static string DataFolder => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "MicMix");

    public void OpenDataFolder()
    {
        Directory.CreateDirectory(DataFolder);
        Process.Start(new ProcessStartInfo(DataFolder) { UseShellExecute = true });
    }

    #endregion

    #region First-run wizard

    public bool ShowWizard { get => _showWizard; set => Set(ref _showWizard, value); }

    /// <summary>1: VB-Cable, 2: microphone, 3: what to pick in Discord/games.</summary>
    public int WizardStep
    {
        get => _wizardStep;
        set { if (Set(ref _wizardStep, Math.Clamp(value, 1, 3))) InstallStatus = ""; }
    }

    public bool CableInstalled => !VbCableMissing;

    public bool IsInstallingCable { get => _isInstallingCable; private set => Set(ref _isInstallingCable, value); }

    public string InstallStatus { get => _installStatus; private set => Set(ref _installStatus, value); }

    public async void InstallVbCable()
    {
        if (IsInstallingCable) return;
        IsInstallingCable = true;
        try
        {
            var progress = new Progress<string>(s => InstallStatus = s);
            var (_, message) = await VbCableInstaller.InstallAsync(progress);
            InstallStatus = message;
            _wake.Set(); // rescan devices right away
        }
        finally
        {
            IsInstallingCable = false;
        }
    }

    public void OpenVbCablePage() =>
        Process.Start(new ProcessStartInfo(VbCableInstaller.PageUrl) { UseShellExecute = true });

    public void WizardNext()
    {
        // Leaving the mic step: start from a preset that suits this mic, unless the user already tuned one.
        if (WizardStep == 2 && SelectedMic != null && Voice.Preset != VoicePreset.Custom)
            Voice.ApplyPreset(VoiceSettings.SuggestFor(SelectedMic.Name));
        if (WizardStep < 3) WizardStep++;
        else FinishWizard();
    }

    public void WizardBack() => WizardStep--;

    public void FinishWizard()
    {
        lock (_settingsLock) _settings.FirstRunDone = true;
        SettingsChanged(wakeWorker: false);
        ShowWizard = false;
        WizardStep = 1;
    }

    /// <summary>Re-run the setup from the settings page.</summary>
    public void StartWizard()
    {
        WizardStep = 1;
        ShowWizard = true;
    }

    #endregion
}
