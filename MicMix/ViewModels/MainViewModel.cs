using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using MicMix.Apps;
using MicMix.Audio;
using MicMix.Hotkeys;
using MicMix.Obs;
using NAudio.CoreAudioApi;

namespace MicMix.ViewModels;

public enum EngineState { Starting, Running, NoOutput, Error }

/// <summary>
/// Owns the settings and the audio engine. A background loop (every ~1.5 s, or right after the user
/// changes something) rescans devices and apps, rebuilds the engine if a device changed or died, and
/// syncs which apps are captured. The UI thread only reads levels and edits settings.
/// </summary>
public sealed partial class MainViewModel : Observable, IDisposable
{
    private const string CableOutputName = "CABLE Input";

    private readonly Settings _settings;
    private readonly Dispatcher _ui;
    private readonly object _settingsLock = new();
    private readonly AutoResetEvent _wake = new(false);
    private readonly CancellationTokenSource _cts = new();
    private readonly DispatcherTimer _meterTimer;
    private readonly DispatcherTimer _saveTimer;
    private readonly Dictionary<string, ImageSource?> _iconCache = new(StringComparer.OrdinalIgnoreCase);
    private readonly Thread? _worker;
    /// <summary>False only for the screenshot demo: no audio, no hotkeys, nothing saved.</summary>
    private readonly bool _live;

    // Owned by the worker thread; the UI thread reads _engine only for levels/volume, under _engineLock.
    private readonly object _engineLock = new();
    private AudioEngine? _engine;
    private string? _engineOutputId;
    private string? _engineMicId;

    private DeviceItem? _selectedMic;
    private DeviceItem? _selectedOutput;
    private double _micVolumePercent;
    private bool _micMuted;
    private double _micLevel;
    private double _outputLevel;
    private EngineState _state = EngineState.Starting;
    private string _statusText = "Запуск…";
    private bool _vbCableMissing;
    private string? _loopWarning;
    private bool _suppressDeviceSelection;
    private volatile bool _monitorEnabled;
    private bool _voiceActive;
    private bool _appsDucked;
    private bool _showVoiceDetails;

    public ObservableCollection<DeviceItem> Mics { get; } = new();
    public ObservableCollection<DeviceItem> Outputs { get; } = new();
    public ObservableCollection<AppItemViewModel> Apps { get; } = new();
    public ObservableCollection<HotkeyItemViewModel> Hotkeys { get; } = new();

    private readonly GlobalHotkeys _hotkeys = new();
    private readonly ObsFeed _obsVoice = new("voice", "MicMix → OBS: голос");
    private readonly ObsFeed _obsApps = new("apps", "MicMix → OBS: программы");
    private DeviceItem? _selectedObsDevice;
    private string _obsStatus = "";
    private string? _obsWarning;

    public ObservableCollection<DeviceItem> ObsDevices { get; } = new();

    public MainViewModel() : this(Settings.Load(), live: true) { }

    private MainViewModel(Settings settings, bool live)
    {
        _ui = Dispatcher.CurrentDispatcher;
        _live = live;
        _settings = settings;
        _micVolumePercent = _settings.MicVolume * 100;
        _micMuted = _settings.MicMuted;
        _settings.Voice.PropertyChanged += (_, _) => SettingsChanged(wakeWorker: false);
        _settings.VoiceFx.PropertyChanged += (_, _) => SettingsChanged(wakeWorker: false);
        _settings.AppBus.PropertyChanged += (_, _) => SettingsChanged(wakeWorker: false);

        // Keep saved bindings, add defaults for any action a newer version introduced.
        foreach (var d in HotkeyBinding.Defaults())
            if (_settings.Hotkeys.All(h => h.Action != d.Action)) _settings.Hotkeys.Add(d);
        foreach (var h in _settings.Hotkeys.OrderBy(h => h.Action)) Hotkeys.Add(new HotkeyItemViewModel(h));
        _hotkeys.Pressed += OnHotkey;
        if (_live) RegisterHotkeys();

        foreach (var app in _settings.Apps.Where(a => a.Enabled))
            InsertSorted(new AppItemViewModel(app.Key, app.DisplayName, true, app.Volume, OnAppChanged) { Status = "не запущено" });

        _meterTimer = new DispatcherTimer(TimeSpan.FromMilliseconds(33), DispatcherPriority.Background, (_, _) => UpdateMeters(), _ui);
        _saveTimer = new DispatcherTimer(TimeSpan.FromMilliseconds(600), DispatcherPriority.Background, (_, _) =>
        {
            _saveTimer!.Stop();
            if (_live) lock (_settingsLock) _settings.Save();
        }, _ui);
        _saveTimer.Stop();

        InitUi();
        if (!_live) return;

        _worker = new Thread(WorkerLoop) { IsBackground = true, Name = "MicMix worker" };
        _worker.SetApartmentState(ApartmentState.MTA);
        _worker.Start();
        _meterTimer.Start();
    }

    #region Bindable state

    public DeviceItem? SelectedMic
    {
        get => _selectedMic;
        set
        {
            if (!Set(ref _selectedMic, value) || _suppressDeviceSelection || value == null) return;
            lock (_settingsLock) _settings.MicDeviceId = value.Id;
            SettingsChanged();
        }
    }

    public DeviceItem? SelectedOutput
    {
        get => _selectedOutput;
        set
        {
            if (!Set(ref _selectedOutput, value) || _suppressDeviceSelection || value == null) return;
            lock (_settingsLock) _settings.OutputDeviceId = value.Id;
            SettingsChanged();
        }
    }

    public double MicVolumePercent
    {
        get => _micVolumePercent;
        set
        {
            if (!Set(ref _micVolumePercent, Math.Round(value))) return;
            lock (_settingsLock) _settings.MicVolume = (float)(value / 100);
            ApplyMicToEngine();
            SettingsChanged(wakeWorker: false);
        }
    }

    public bool MicMuted
    {
        get => _micMuted;
        set
        {
            if (!Set(ref _micMuted, value)) return;
            lock (_settingsLock) _settings.MicMuted = value;
            ApplyMicToEngine();
            SettingsChanged(wakeWorker: false);
        }
    }

    /// <summary>Voice chain settings; the UI binds to them directly and the audio thread reads them live.</summary>
    public VoiceSettings Voice => _settings.Voice;

    /// <summary>Voice changer (pitch, robot, radio, choppy).</summary>
    public VoiceFxSettings VoiceFx => _settings.VoiceFx;

    public AppBusSettings AppBus => _settings.AppBus;

    /// <summary>"Послушать себя": processed voice into my own headphones.</summary>
    public bool MonitorEnabled
    {
        get => _monitorEnabled;
        set
        {
            if (_monitorEnabled == value) return;
            _monitorEnabled = value;
            Raise(nameof(MonitorEnabled));
            AudioEngine? engine;
            lock (_engineLock) engine = _engine;
            if (engine != null) Task.Run(() => engine.SetMonitor(value));
        }
    }

    public bool ShowVoiceDetails { get => _showVoiceDetails; set => Set(ref _showVoiceDetails, value); }

    /// <summary>Speech detected right now (lights the "голос" lamp).</summary>
    public bool VoiceActive { get => _voiceActive; private set => Set(ref _voiceActive, value); }

    public bool AppsDucked { get => _appsDucked; private set => Set(ref _appsDucked, value); }

    public double MicLevel { get => _micLevel; private set => Set(ref _micLevel, value); }
    public double OutputLevel { get => _outputLevel; private set => Set(ref _outputLevel, value); }

    public EngineState State
    {
        get => _state;
        private set { if (Set(ref _state, value)) Raise(nameof(IsRunning)); }
    }

    public bool IsRunning => _state == EngineState.Running;
    public string StatusText { get => _statusText; private set => Set(ref _statusText, value); }
    public bool VbCableMissing { get => _vbCableMissing; private set => Set(ref _vbCableMissing, value); }

    /// <summary>Set when some other program plays straight into the virtual cable (it would be heard by everyone).</summary>
    public string? LoopWarning
    {
        get => _loopWarning;
        private set { if (Set(ref _loopWarning, value)) Raise(nameof(HasLoopWarning)); }
    }

    public bool HasLoopWarning => _loopWarning != null;

    #endregion

    private void OnAppChanged(AppItemViewModel app)
    {
        lock (_settingsLock)
        {
            var s = _settings.Apps.FirstOrDefault(a => a.Key.Equals(app.Key, StringComparison.OrdinalIgnoreCase));
            if (s == null) _settings.Apps.Add(s = new AppSetting { Key = app.Key });
            s.DisplayName = app.DisplayName;
            s.Enabled = app.Enabled;
            s.Volume = app.Volume;
        }
        lock (_engineLock) _engine?.SetAppVolume(app.Key, app.Volume);
        SettingsChanged();
    }

    private void ApplyMicToEngine()
    {
        lock (_engineLock) _engine?.SetMic((float)(_micVolumePercent / 100), _micMuted);
    }

    private void SettingsChanged(bool wakeWorker = true)
    {
        _saveTimer.Stop();
        _saveTimer.Start();
        if (wakeWorker) _wake.Set();
    }

    private void UpdateMeters()
    {
        EngineLevels? levels;
        lock (_engineLock)
        {
            levels = _engine?.TakeLevels();
            VoiceActive = _engine?.VoiceActive ?? false;
            AppsDucked = _engine?.AppsDucked ?? false;
        }
        if (levels == null)
        {
            MicLevel = OutputLevel = AppsLevel = 0;
            foreach (var app in Apps) app.Level = 0;
            return;
        }

        MicLevel = Decay(MicLevel, levels.Mic);
        OutputLevel = Decay(OutputLevel, levels.Output);
        AppsLevel = Decay(AppsLevel, levels.Apps.Values.Select(a => a.Peak).DefaultIfEmpty(0).Max());
        foreach (var app in Apps)
            app.Level = app.Enabled && levels.Apps.TryGetValue(app.Key, out var l) ? Decay(app.Level, l.Peak) : 0;

        static double Decay(double current, float peak) => Math.Max(PeakToMeter(peak), current * 0.86);
    }

    #region Hotkeys

    #region OBS

    public bool ObsEnabled
    {
        get => _settings.ObsEnabled;
        set
        {
            if (_settings.ObsEnabled == value) return;
            lock (_settingsLock) _settings.ObsEnabled = value;
            Raise(nameof(ObsEnabled));
            SettingsChanged();
        }
    }

    public DeviceItem? SelectedObsDevice
    {
        get => _selectedObsDevice;
        set
        {
            if (!Set(ref _selectedObsDevice, value) || _suppressDeviceSelection || value == null) return;
            lock (_settingsLock) _settings.ObsDeviceId = value.Id;
            SettingsChanged();
        }
    }

    public string VoiceTrackTitle => _obsVoice.Title;
    public string AppsTrackTitle => _obsApps.Title;

    public string ObsStatus { get => _obsStatus; private set => Set(ref _obsStatus, value); }

    /// <summary>Set when the chosen OBS device is one the user actually hears.</summary>
    public string? ObsWarning
    {
        get => _obsWarning;
        private set { if (Set(ref _obsWarning, value)) Raise(nameof(HasObsWarning)); }
    }

    public bool HasObsWarning => _obsWarning != null;

    private static bool LooksUnplugged(string name) =>
        name.Contains("S/PDIF", StringComparison.OrdinalIgnoreCase) || name.Contains("SPDIF", StringComparison.OrdinalIgnoreCase);

    #endregion

    public bool HotkeySounds
    {
        get => _settings.HotkeySounds;
        set
        {
            if (_settings.HotkeySounds == value) return;
            _settings.HotkeySounds = value;
            Raise(nameof(HotkeySounds));
            SettingsChanged(wakeWorker: false);
        }
    }

    private void OnHotkey(HotkeyAction action)
    {
        bool on;
        switch (action)
        {
            case HotkeyAction.ToggleMicMute: MicMuted = !MicMuted; on = !MicMuted; break;
            case HotkeyAction.ToggleApps: AppBus.Muted = !AppBus.Muted; on = !AppBus.Muted; break;
            case HotkeyAction.ToggleVoiceChain: Voice.Enabled = !Voice.Enabled; on = Voice.Enabled; break;
            case HotkeyAction.ToggleMonitor: MonitorEnabled = !MonitorEnabled; on = MonitorEnabled; break;
            case HotkeyAction.ToggleVoiceFx: VoiceFx.Enabled = !VoiceFx.Enabled; on = VoiceFx.Enabled; break;
            default: return;
        }
        if (HotkeySounds) SoundCue.Play(on);
    }

    private void RegisterHotkeys()
    {
        var taken = _hotkeys.Register(_settings.Hotkeys);
        foreach (var item in Hotkeys) item.IsTaken = taken.Contains(item.Binding.Action);
    }

    /// <summary>Starts listening for a new combo. All hotkeys are released meanwhile so pressing an existing one is captured, not triggered.</summary>
    public void BeginCapture(HotkeyItemViewModel item)
    {
        foreach (var h in Hotkeys) h.IsCapturing = false;
        _hotkeys.UnregisterAll();
        item.IsCapturing = true;
    }

    public HotkeyItemViewModel? Capturing => Hotkeys.FirstOrDefault(h => h.IsCapturing);

    /// <summary>Finishes capture. <paramref name="virtualKey"/> 0 clears the binding; null cancels.</summary>
    public void EndCapture(HotkeyModifiers modifiers, int? virtualKey)
    {
        var item = Capturing;
        if (item == null) return;
        if (virtualKey != null)
        {
            // One combo, one action: steal it from whichever action had it.
            foreach (var other in Hotkeys.Where(h => h != item && h.Binding.Modifiers == modifiers && h.Binding.VirtualKey == virtualKey && virtualKey != 0))
            {
                other.Binding.VirtualKey = 0;
                other.Binding.Modifiers = HotkeyModifiers.None;
                other.Changed();
            }
            item.Binding.Modifiers = virtualKey == 0 ? HotkeyModifiers.None : modifiers;
            item.Binding.VirtualKey = virtualKey.Value;
            SettingsChanged(wakeWorker: false);
        }
        item.IsCapturing = false;
        RegisterHotkeys();
    }

    public void ResetHotkeys()
    {
        foreach (var d in HotkeyBinding.Defaults())
        {
            var item = Hotkeys.First(h => h.Binding.Action == d.Action);
            item.Binding.Modifiers = d.Modifiers;
            item.Binding.VirtualKey = d.VirtualKey;
            item.IsCapturing = false;
            item.Changed();
        }
        SettingsChanged(wakeWorker: false);
        RegisterHotkeys();
    }

    #endregion

    #region Worker

    private sealed record Snapshot(
        List<DeviceItem> Mics, List<DeviceItem> Outputs, DeviceItem? Mic, DeviceItem? Output,
        bool CableMissing, List<AudioApp> Apps, EngineState State, string Status, string? LoopWarning,
        IReadOnlyDictionary<string, AppLevel>? AppLevels,
        List<DeviceItem> ObsDevices, DeviceItem? ObsDevice, string ObsStatus, string? ObsWarning);

    private void WorkerLoop()
    {
        while (!_cts.IsCancellationRequested)
        {
            try
            {
                var snapshot = Tick();
                _ui.BeginInvoke(() => Apply(snapshot));
            }
            catch (Exception ex)
            {
                Debug.WriteLine("Worker tick failed: " + ex);
            }
            _wake.WaitOne(1500);
        }
    }

    private Snapshot Tick()
    {
        // 1. Devices.
        var mics = new List<DeviceItem>();
        var outputs = new List<DeviceItem>();
        string? defaultMicId = null, defaultOutputId = null;
        using (var enumerator = new MMDeviceEnumerator())
        {
            foreach (var d in enumerator.EnumerateAudioEndPoints(DataFlow.Capture, DeviceState.Active))
            {
                using (d)
                {
                    string name = NameOf(d);
                    // Our own cable's output must never be picked as the source mic: instant feedback loop.
                    if (!name.Contains("CABLE", StringComparison.OrdinalIgnoreCase))
                        mics.Add(new DeviceItem(d.ID, name));
                }
            }
            foreach (var d in enumerator.EnumerateAudioEndPoints(DataFlow.Render, DeviceState.Active))
                using (d) outputs.Add(new DeviceItem(d.ID, NameOf(d)));
            try
            {
                if (enumerator.HasDefaultAudioEndpoint(DataFlow.Capture, Role.Communications))
                    using (var def = enumerator.GetDefaultAudioEndpoint(DataFlow.Capture, Role.Communications))
                        defaultMicId = def.ID;
            }
            catch { /* no default mic */ }
            try
            {
                if (enumerator.HasDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia))
                    using (var def = enumerator.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia))
                        defaultOutputId = def.ID;
            }
            catch { /* no default output */ }
        }

        string? micId, outputId;
        List<AppSetting> appSettings;
        float micVolume;
        bool micMuted;
        lock (_settingsLock)
        {
            micId = _settings.MicDeviceId;
            outputId = _settings.OutputDeviceId;
            appSettings = _settings.Apps.Select(a => new AppSetting { Key = a.Key, Enabled = a.Enabled, Volume = a.Volume }).ToList();
            micVolume = _settings.MicVolume;
            micMuted = _settings.MicMuted;
        }

        var cable = outputs.FirstOrDefault(o => o.Name.Contains(CableOutputName, StringComparison.OrdinalIgnoreCase));
        var output = outputs.FirstOrDefault(o => string.Equals(o.Id, outputId, StringComparison.OrdinalIgnoreCase)) ?? cable;
        // First run: prefer a real microphone over virtual ones (Sonar, Voicemod…), even if Windows defaults to them.
        var mic = mics.FirstOrDefault(m => string.Equals(m.Id, micId, StringComparison.OrdinalIgnoreCase))
                  ?? mics.FirstOrDefault(m => m.Id == defaultMicId && !LooksVirtual(m.Name))
                  ?? mics.FirstOrDefault(m => !LooksVirtual(m.Name))
                  ?? mics.FirstOrDefault();

        // 2. Engine: (re)build when devices changed or a stream died.
        EngineState state;
        string status;
        lock (_engineLock)
        {
            bool needsRebuild = _engine == null || _engine.Fault != null ||
                                _engineOutputId != output?.Id || _engineMicId != mic?.Id;
            if (needsRebuild)
            {
                string? previousFault = _engine?.Fault;
                _engine?.Dispose();
                _engine = null;
                _engineOutputId = _engineMicId = null;

                if (output != null)
                {
                    try
                    {
                        using var enumerator = new MMDeviceEnumerator();
                        var outDev = enumerator.GetDevice(output.Id);
                        var micDev = mic != null ? enumerator.GetDevice(mic.Id) : null;
                        var engine = new AudioEngine(outDev, micDev, _settings.Voice, _settings.VoiceFx, _settings.AppBus);
                        engine.SetMic(micVolume, micMuted);
                        engine.Start();
                        engine.SetMonitor(_monitorEnabled);
                        _engine = engine;
                        _engineOutputId = output.Id;
                        _engineMicId = mic?.Id;
                    }
                    catch (Exception ex)
                    {
                        Debug.WriteLine("Engine start failed: " + ex);
                        _lastStartError = ex.Message;
                    }
                }
                if (previousFault != null) Debug.WriteLine("Engine rebuilt after fault: " + previousFault);
            }

            if (output == null)
            {
                state = EngineState.NoOutput;
                status = cable == null ? "VB-Cable не установлен" : "Выбери устройство выхода";
            }
            else if (_engine == null)
            {
                state = EngineState.Error;
                status = "Не удалось запустить звук: " + _lastStartError + " Пробую снова…";
            }
            else
            {
                state = EngineState.Running;
                // "CABLE Input (VB-Audio Virtual Cable)" → "CABLE Input": fits the sidebar.
                string shortName = output.Name.Split(" (")[0];
                status = mic == null ? "Работает без микрофона → " + shortName : "Работает → " + shortName;
            }
        }

        // 3. Apps.
        var enabledKeys = appSettings.Where(a => a.Enabled).Select(a => a.Key).ToList();
        var apps = AppScanner.Scan(enabledKeys);
        IReadOnlyDictionary<string, AppLevel>? appLevels = null;

        AudioEngine? current;
        lock (_engineLock) current = _engine;
        if (current != null)
        {
            var targets = new Dictionary<string, AppTarget>(StringComparer.OrdinalIgnoreCase);
            foreach (var s in appSettings.Where(a => a.Enabled && !AppScanner.NeverCapture.Contains(a.Key)))
            {
                var app = apps.FirstOrDefault(a => a.Key.Equals(s.Key, StringComparison.OrdinalIgnoreCase));
                if (app != null && app.RootProcessIds.Count > 0)
                    targets[s.Key] = new AppTarget(app.RootProcessIds, s.Volume);
            }
            current.SyncApps(targets);
            appLevels = current.GetAppStatus();
        }

        // 4. Loop guard: anything else playing directly into the cable is heard by everyone.
        string? loopWarning = output != null ? FindDirectPlayers(output.Id) : null;

        // 5. OBS tracks.
        bool obsEnabled;
        string? obsDeviceId;
        lock (_settingsLock) { obsEnabled = _settings.ObsEnabled; obsDeviceId = _settings.ObsDeviceId; }
        var obsDevices = outputs.Where(o => !o.Name.Contains("CABLE", StringComparison.OrdinalIgnoreCase)).ToList();
        var obsDevice = obsDevices.FirstOrDefault(o => string.Equals(o.Id, obsDeviceId, StringComparison.OrdinalIgnoreCase))
                        ?? obsDevices.FirstOrDefault(o => LooksUnplugged(o.Name));
        string obsStatus;
        string? obsWarning = obsDevice != null && obsDevice.Id == defaultOutputId
            ? "Это устройство ты сейчас слышишь — дорожки для OBS будут звучать у тебя. Выбери выход, который ни к чему не подключён."
            : null;
        if (obsEnabled && current != null && obsDevice != null)
        {
            _obsVoice.Ensure(obsDevice.Id, current.SampleRate);
            _obsApps.Ensure(obsDevice.Id, current.SampleRate);
            current.SetFeedSinks(_obsVoice.Write, _obsApps.Write);
            obsStatus = $"голос: {_obsVoice.Status} · программы: {_obsApps.Status}";
        }
        else
        {
            _obsVoice.Stop();
            _obsApps.Stop();
            current?.SetFeedSinks(null, null);
            obsStatus = !obsEnabled ? "выключено"
                : obsDevice == null ? "нет подходящего выхода — выбери его ниже"
                : "ждёт, пока заработает звук";
        }

        return new Snapshot(mics, outputs, mic, output, cable == null, apps, state, status, loopWarning, appLevels,
            obsDevices, obsDevice, obsStatus, obsWarning);
    }

    private string? _lastStartError;

    // Reading FriendlyName goes through each driver's property store: ~600 ms of CPU per scan on some
    // machines. IDs are cheap, so names are read once per device. Worker thread only.
    private readonly Dictionary<string, string> _deviceNames = new(StringComparer.OrdinalIgnoreCase);

    private string NameOf(MMDevice device)
    {
        if (!_deviceNames.TryGetValue(device.ID, out var name))
            _deviceNames[device.ID] = name = device.FriendlyName;
        return name;
    }

    private static readonly string[] VirtualMicMarkers = ["virtual", "sonar", "voicemod", "ivcam", "voicemeeter", "nvidia broadcast"];

    private static bool LooksVirtual(string name) =>
        VirtualMicMarkers.Any(v => name.Contains(v, StringComparison.OrdinalIgnoreCase));

    private static string? FindDirectPlayers(string outputId)
    {
        try
        {
            using var enumerator = new MMDeviceEnumerator();
            using var device = enumerator.GetDevice(outputId);
            var manager = device.AudioSessionManager;
            manager.RefreshSessions();
            var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var sessions = manager.Sessions;
            for (int i = 0; i < sessions.Count; i++)
            {
                var s = sessions[i];
                if (s.IsSystemSoundsSession || s.State != NAudio.CoreAudioApi.Interfaces.AudioSessionState.AudioSessionStateActive) continue;
                int pid = (int)s.GetProcessID;
                if (pid == 0 || pid == Environment.ProcessId) continue;
                try { names.Add(Process.GetProcessById(pid).ProcessName); } catch { }
            }
            return names.Count == 0
                ? null
                : $"{string.Join(", ", names)} выводит звук прямо в виртуальный кабель — его слышат все. " +
                  "Поменяй в этой программе устройство вывода на наушники.";
        }
        catch
        {
            return null;
        }
    }

    #endregion

    private void Apply(Snapshot s)
    {
        _suppressDeviceSelection = true;
        try
        {
            SyncList(Mics, s.Mics);
            SyncList(Outputs, s.Outputs);
            SelectedMic = s.Mic == null ? null : Mics.FirstOrDefault(m => m.Id == s.Mic.Id);
            SelectedOutput = s.Output == null ? null : Outputs.FirstOrDefault(o => o.Id == s.Output.Id);
            SyncList(ObsDevices, s.ObsDevices);
            SelectedObsDevice = s.ObsDevice == null ? null : ObsDevices.FirstOrDefault(o => o.Id == s.ObsDevice.Id);
        }
        finally
        {
            _suppressDeviceSelection = false;
        }

        State = s.State;
        StatusText = s.Status;
        VbCableMissing = s.CableMissing;
        LoopWarning = s.LoopWarning;
        ObsStatus = s.ObsStatus;
        ObsWarning = s.ObsWarning;

        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var app in s.Apps)
        {
            seen.Add(app.Key);
            var vm = Apps.FirstOrDefault(a => a.Key.Equals(app.Key, StringComparison.OrdinalIgnoreCase));
            if (vm == null)
            {
                AppSetting? saved;
                lock (_settingsLock) saved = _settings.Apps.FirstOrDefault(a => a.Key.Equals(app.Key, StringComparison.OrdinalIgnoreCase));
                vm = new AppItemViewModel(app.Key, app.DisplayName, saved?.Enabled ?? false, saved?.Volume ?? 1f, OnAppChanged);
                InsertSorted(vm);
            }
            vm.DisplayName = app.DisplayName;
            vm.IsRunning = app.RootProcessIds.Count > 0;
            vm.IsPlaying = app.IsPlaying;
            vm.Icon ??= GetIcon(app.ExePath);

            AppLevel? level = null;
            s.AppLevels?.TryGetValue(app.Key, out level);
            vm.Status = !vm.IsRunning ? "не запущено"
                : vm.Enabled && level?.Error != null && level.ActiveCaptures == 0 ? "ошибка: " + level.Error
                : vm.Enabled && s.State == EngineState.Running ? (app.IsPlaying ? "в микрофоне · играет" : "в микрофоне")
                : app.IsPlaying ? "играет" : "";
        }

        // Drop apps that went away, unless the user selected them (they stay as "not running").
        foreach (var vm in Apps.Where(a => !seen.Contains(a.Key)).ToList())
        {
            if (vm.Enabled)
            {
                vm.IsRunning = false;
                vm.IsPlaying = false;
                vm.Status = "не запущено";
            }
            else
            {
                Apps.Remove(vm);
            }
        }
    }

    private void InsertSorted(AppItemViewModel vm)
    {
        int i = 0;
        while (i < Apps.Count && string.CompareOrdinal(Apps[i].SortKey, vm.SortKey) < 0) i++;
        Apps.Insert(i, vm);
    }

    private static void SyncList(ObservableCollection<DeviceItem> target, List<DeviceItem> source)
    {
        if (target.SequenceEqual(source)) return;
        target.Clear();
        foreach (var d in source) target.Add(d);
    }

    private ImageSource? GetIcon(string? exePath)
    {
        if (exePath == null) return null;
        if (_iconCache.TryGetValue(exePath, out var cached)) return cached;
        ImageSource? image = null;
        try
        {
            using var icon = System.Drawing.Icon.ExtractAssociatedIcon(exePath);
            if (icon != null)
            {
                image = Imaging.CreateBitmapSourceFromHIcon(icon.Handle, Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions());
                image.Freeze();
            }
        }
        catch { /* protected process or missing file: no icon */ }
        _iconCache[exePath] = image;
        return image;
    }

    public void Dispose()
    {
        _cts.Cancel();
        _wake.Set();
        _worker?.Join(2000);
        _meterTimer.Stop();
        _hotkeys.Dispose();
        _obsVoice.Dispose();
        _obsApps.Dispose();
        if (_live) lock (_settingsLock) _settings.Save();
        lock (_engineLock)
        {
            _engine?.Dispose();
            _engine = null;
        }
    }
}
