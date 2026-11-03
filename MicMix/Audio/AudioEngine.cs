using System.Diagnostics;
using MicMix.Audio.Dsp;
using NAudio.CoreAudioApi;
using NAudio.Wave;
using NAudio.Wave.SampleProviders;

namespace MicMix.Audio;

/// <summary>What the engine should be capturing for one app (grouped by exe name).</summary>
public sealed record AppTarget(IReadOnlyCollection<int> RootProcessIds, float Volume);

/// <summary>
/// Mic + selected apps → mixer → virtual cable. One instance per (output device, mic device) pair;
/// when a device changes or dies the owner disposes it and builds a new one.
/// </summary>
public sealed class AudioEngine : IDisposable
{
    private readonly MMDevice _outputDevice;
    private readonly MMDevice? _micDevice;
    private readonly MixingSampleProvider _mixer;
    private readonly MixingSampleProvider _appMixer;
    private readonly AppBusProcessor _appBus;
    private readonly OutputStage _outputStage;
    private readonly VoiceSettings _voiceSettings;
    private readonly VoiceFxSettings _fxSettings;
    private readonly VoiceMonitor _monitor;
    private readonly FeedTap _appsTap;
    private FeedTap? _voiceTap;
    private Action<float[], int>? _voiceSink;
    private readonly WasapiOut _out;
    private readonly Dictionary<string, AppRoute> _apps = new(StringComparer.OrdinalIgnoreCase);
    private readonly object _appsLock = new();
    private MicInput? _mic;
    private SourceChannel? _micChannel;
    private float _micVolume = 1f;
    private bool _micMuted;

    public int SampleRate { get; }

    /// <summary>Set when the output or mic stream died; the owner should rebuild the engine.</summary>
    public string? Fault { get; private set; }

    /// <summary>True while I'm speaking (speech detected and mic not muted).</summary>
    public bool VoiceActive => _mic != null && !_micMuted && _mic.Voice.VoiceActive;

    /// <summary>Confidently talking (stricter than <see cref="VoiceActive"/>); drives ducking.</summary>
    private bool Talking => _mic != null && !_micMuted && _mic.Voice.Talking;

    /// <summary>True while the apps are lowered under my voice.</summary>
    public bool AppsDucked => _appBus.Ducking;

    public AudioEngine(MMDevice outputDevice, MMDevice? micDevice, VoiceSettings voiceSettings, VoiceFxSettings fxSettings, AppBusSettings appBusSettings)
    {
        _outputDevice = outputDevice;
        _micDevice = micDevice;
        _voiceSettings = voiceSettings;
        _fxSettings = fxSettings;
        SampleRate = outputDevice.AudioClient.MixFormat.SampleRate;

        var format = WaveFormat.CreateIeeeFloatWaveFormat(SampleRate, 2);
        _mixer = new MixingSampleProvider(format) { ReadFully = true };
        _appMixer = new MixingSampleProvider(format) { ReadFully = true };
        _appBus = new AppBusProcessor(_appMixer, appBusSettings, () => Talking);
        _outputStage = new OutputStage(_mixer);
        _monitor = new VoiceMonitor(SampleRate);
        _appsTap = new FeedTap(_appBus);
        _out = new WasapiOut(outputDevice, AudioClientShareMode.Shared, useEventSync: true, latency: 20);
        _out.PlaybackStopped += (_, e) => { if (e.Exception != null) Fault ??= "Выход: " + e.Exception.Message; };
    }

    public void Start()
    {
        if (_micDevice != null)
        {
            _mic = new MicInput(_micDevice, SampleRate, _voiceSettings, _fxSettings);
            _mic.Failed += ex => Fault ??= "Микрофон: " + ex.Message;
            _micChannel = new SourceChannel(_mic.Output) { Volume = _micVolume, Muted = _micMuted };
            _voiceTap = new FeedTap(_monitor.Tap(_micChannel)) { Sink = _voiceSink };
            _mixer.AddMixerInput(_voiceTap); // mic first: ducking reads its speech flag
            _mic.Start();
        }
        _mixer.AddMixerInput(_appsTap);
        _out.Init(_outputStage);
        _out.Play();
    }

    /// <summary>Copies of the processed voice and the processed apps, e.g. for separate OBS tracks. Null = off.</summary>
    public void SetFeedSinks(Action<float[], int>? voice, Action<float[], int>? apps)
    {
        _voiceSink = voice;
        if (_voiceTap != null) _voiceTap.Sink = voice;
        _appsTap.Sink = apps;
    }

    /// <summary>Plays my processed voice into my own headphones, to tune the chain by ear.</summary>
    public void SetMonitor(bool enabled) => _monitor.SetEnabled(enabled);

    public void SetMic(float volume, bool muted)
    {
        _micVolume = volume;
        _micMuted = muted;
        if (_micChannel == null) return;
        _micChannel.Volume = volume;
        _micChannel.Muted = muted;
    }

    /// <summary>Cheap, immediate volume change for slider drags (SyncApps would also apply it, but later).</summary>
    public void SetAppVolume(string key, float volume)
    {
        lock (_appsLock)
        {
            if (_apps.TryGetValue(key, out var route)) route.Volume = volume;
        }
    }

    /// <summary>
    /// Brings the running captures in line with <paramref name="targets"/>. Blocking (activating a
    /// process capture takes a moment), so call it off the UI thread.
    /// </summary>
    public void SyncApps(IReadOnlyDictionary<string, AppTarget> targets)
    {
        lock (_appsLock)
        {
            foreach (var key in _apps.Keys.Where(k => !targets.ContainsKey(k)).ToList())
            {
                _apps[key].Dispose(_appMixer);
                _apps.Remove(key);
            }

            foreach (var (key, target) in targets)
            {
                if (!_apps.TryGetValue(key, out var route))
                    _apps[key] = route = new AppRoute();
                route.Volume = target.Volume;
                route.Sync(target.RootProcessIds, SampleRate, _appMixer);
            }
        }
    }

    /// <summary>Capture health per app, without consuming the meter peaks.</summary>
    public IReadOnlyDictionary<string, AppLevel> GetAppStatus()
    {
        lock (_appsLock)
            return _apps.ToDictionary(kv => kv.Key, kv => new AppLevel(0, kv.Value.ActiveCaptures, kv.Value.LastError),
                StringComparer.OrdinalIgnoreCase);
    }

    public EngineLevels TakeLevels()
    {
        var apps = new Dictionary<string, AppLevel>(StringComparer.OrdinalIgnoreCase);
        lock (_appsLock)
        {
            foreach (var (key, route) in _apps)
                apps[key] = new AppLevel(route.TakePeak(), route.ActiveCaptures, route.LastError);
        }
        return new EngineLevels(_micChannel?.TakePeak() ?? 0, _outputStage.TakePeak(), apps);
    }

    public void Dispose()
    {
        try { _out.Stop(); } catch { /* device may be gone */ }
        _out.Dispose();
        _monitor.Dispose();
        _mic?.Dispose();
        lock (_appsLock)
        {
            foreach (var route in _apps.Values) route.Dispose(_appMixer);
            _apps.Clear();
        }
    }

    /// <summary>All captures belonging to one app; usually one root process, sometimes several.</summary>
    private sealed class AppRoute
    {
        private readonly Dictionary<int, (ProcessLoopbackCapture Capture, SourceChannel Channel)> _captures = new();
        private readonly Dictionary<int, DateTime> _retryAfter = new();
        private float _volume = 1f;

        public string? LastError { get; private set; }
        public int ActiveCaptures => _captures.Count;

        public float Volume
        {
            get => _volume;
            set
            {
                _volume = value;
                foreach (var (_, channel) in _captures.Values) channel.Volume = value;
            }
        }

        public void Sync(IReadOnlyCollection<int> pids, int sampleRate, MixingSampleProvider mixer)
        {
            foreach (var pid in _captures.Keys.Where(p => !pids.Contains(p)).ToList())
                Remove(pid, mixer);

            foreach (var pid in pids)
            {
                if (_captures.ContainsKey(pid)) continue;
                if (_retryAfter.TryGetValue(pid, out var when) && DateTime.UtcNow < when) continue;

                var buffer = new JitterBuffer(sampleRate);
                var capture = new ProcessLoopbackCapture(pid, sampleRate, buffer.Write);
                try
                {
                    capture.Start();
                }
                catch (Exception ex)
                {
                    capture.Dispose();
                    LastError = ex.Message;
                    _retryAfter[pid] = DateTime.UtcNow.AddSeconds(10);
                    Debug.WriteLine($"Capture of {pid} failed: {ex}");
                    continue;
                }
                capture.Failed += ex => LastError = ex.Message;
                var channel = new SourceChannel(buffer) { Volume = _volume };
                _captures[pid] = (capture, channel);
                mixer.AddMixerInput(channel);
                LastError = null;
            }

            foreach (var pid in _retryAfter.Keys.Where(p => !pids.Contains(p)).ToList())
                _retryAfter.Remove(pid);
        }

        public float TakePeak()
        {
            float peak = 0;
            foreach (var (_, channel) in _captures.Values) peak = Math.Max(peak, channel.TakePeak());
            return peak;
        }

        private void Remove(int pid, MixingSampleProvider mixer)
        {
            var (capture, channel) = _captures[pid];
            mixer.RemoveMixerInput(channel);
            capture.Dispose();
            _captures.Remove(pid);
        }

        public void Dispose(MixingSampleProvider mixer)
        {
            foreach (var pid in _captures.Keys.ToList()) Remove(pid, mixer);
        }
    }

    /// <summary>
    /// Final stage: a limiter just under full scale so loud music + voice never clips, and the meter.
    /// Anything below the ceiling passes untouched, so music alone comes out exactly as it went in.
    /// </summary>
    private sealed class OutputStage(ISampleProvider source) : ISampleProvider
    {
        private readonly Limiter _limiter = new(source.WaveFormat.SampleRate, source.WaveFormat.Channels, releaseMs: 80f) { CeilingDb = -0.3f };
        private float _peak;
        public WaveFormat WaveFormat => source.WaveFormat;

        public float TakePeak()
        {
            float p = _peak;
            _peak = 0;
            return p;
        }

        public int Read(float[] buffer, int offset, int count)
        {
            int read = source.Read(buffer, offset, count);
            _limiter.Process(buffer, offset, read);
            float peak = 0;
            for (int i = offset; i < offset + read; i++)
            {
                float a = Math.Abs(buffer[i]);
                if (a > peak) peak = a;
            }
            if (peak > _peak) _peak = peak;
            return read;
        }
    }
}

public sealed record AppLevel(float Peak, int ActiveCaptures, string? Error);

public sealed record EngineLevels(float Mic, float Output, IReadOnlyDictionary<string, AppLevel> Apps);
