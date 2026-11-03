using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace MicMix.Audio;

/// <summary>
/// Settings the audio thread reads live. The UI binds straight to these; every change bumps
/// <see cref="Version"/> so DSP code knows to recompute filter coefficients.
/// </summary>
public abstract class LiveSettings : INotifyPropertyChanged
{
    private int _version;

    public event PropertyChangedEventHandler? PropertyChanged;

    [System.Text.Json.Serialization.JsonIgnore]
    public int Version => _version;

    protected void Set<T>(ref T field, T value, [CallerMemberName] string? name = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return;
        field = value;
        Interlocked.Increment(ref _version);
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
        OnChanged(name);
    }

    protected void Raise(string name) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));

    protected virtual void OnChanged(string? name) { }
}

public enum VoicePreset { Universal, SoloCast, Headset, Custom }

/// <summary>Processing applied only to my voice. Defaults are the "Универсальный" preset.</summary>
public sealed class VoiceSettings : LiveSettings
{
    private VoicePreset _preset = VoicePreset.Universal;
    private bool _applyingPreset;
    private bool _enabled = true;
    private bool _highPass = true;
    private bool _noiseSuppression = true;
    private float _noiseStrength = 0.85f;
    private bool _gate = true;
    private float _gateSensitivity = 0.5f;
    private bool _eq = true;
    private float _eq100, _eq250 = -2f, _eq1k, _eq4k = 2f, _eq10k = 1f;
    private bool _compressor = true;
    private float _compressorAmount = 0.45f;
    private bool _limiter = true;

    /// <summary>Master switch for the whole chain.</summary>
    public bool Enabled { get => _enabled; set => Set(ref _enabled, value); }

    /// <summary>80 Hz high-pass: desk bumps, rumble, stand vibration.</summary>
    public bool HighPass { get => _highPass; set => Set(ref _highPass, value); }

    public bool NoiseSuppression { get => _noiseSuppression; set => Set(ref _noiseSuppression, value); }

    /// <summary>0..1: how much of the denoised signal replaces the original.</summary>
    public float NoiseStrength { get => _noiseStrength; set => Set(ref _noiseStrength, Math.Clamp(value, 0f, 1f)); }

    /// <summary>Closes the mic while I'm not speaking (speech detection from RNNoise).</summary>
    public bool Gate { get => _gate; set => Set(ref _gate, value); }

    /// <summary>0..1: higher opens on quieter / less certain speech.</summary>
    public float GateSensitivity { get => _gateSensitivity; set => Set(ref _gateSensitivity, Math.Clamp(value, 0f, 1f)); }

    public bool Eq { get => _eq; set => Set(ref _eq, value); }
    public float Eq100 { get => _eq100; set => Set(ref _eq100, value); }
    public float Eq250 { get => _eq250; set => Set(ref _eq250, value); }
    public float Eq1k { get => _eq1k; set => Set(ref _eq1k, value); }
    public float Eq4k { get => _eq4k; set => Set(ref _eq4k, value); }
    public float Eq10k { get => _eq10k; set => Set(ref _eq10k, value); }

    public bool Compressor { get => _compressor; set => Set(ref _compressor, value); }

    /// <summary>0..1: gentle → strong levelling of my voice.</summary>
    public float CompressorAmount { get => _compressorAmount; set => Set(ref _compressorAmount, Math.Clamp(value, 0f, 1f)); }

    public bool Limiter { get => _limiter; set => Set(ref _limiter, value); }

    /// <summary>Speech probability above which the gate opens.</summary>
    public float VadThreshold => 0.85f - 0.7f * _gateSensitivity;

    /// <summary>Which preset the current values came from; any manual tweak turns it into <see cref="VoicePreset.Custom"/>.</summary>
    /// <remarks>Deserialized last: loading the individual values first would flip it to Custom.</remarks>
    [System.Text.Json.Serialization.JsonPropertyOrder(100)]
    public VoicePreset Preset
    {
        get => _preset;
        set
        {
            if (value == VoicePreset.Custom) { Set(ref _preset, value); return; }
            ApplyPreset(value);
        }
    }

    /// <summary>Guess a starting preset from the microphone's name.</summary>
    public static VoicePreset SuggestFor(string micName)
    {
        if (micName.Contains("SoloCast", StringComparison.OrdinalIgnoreCase)) return VoicePreset.SoloCast;
        string[] headset = ["headset", "hands-free", "гарнитур", "головной", "headphone", "earbud", "airpods", "buds"];
        return headset.Any(h => micName.Contains(h, StringComparison.OrdinalIgnoreCase)) ? VoicePreset.Headset : VoicePreset.Universal;
    }

    public void ApplyPreset(VoicePreset preset)
    {
        // (high-pass, noise strength, gate sensitivity, eq 100/250/1k/4k/10k, compressor amount)
        var p = preset switch
        {
            // Close-miked USB condenser: boxy around 250 Hz, likes some presence and air.
            VoicePreset.SoloCast => (true, 0.9f, 0.5f, 0f, -3f, 0f, 2.5f, 2f, 0.5f),
            // Headset boom mics: thin and close to the mouth — add body, tame harshness.
            VoicePreset.Headset => (true, 0.8f, 0.55f, 2f, -1f, -1f, 1f, 0f, 0.4f),
            _ => (true, 0.85f, 0.5f, 0f, -2f, 0f, 2f, 1f, 0.45f),
        };
        _applyingPreset = true;
        try
        {
            HighPass = p.Item1; NoiseSuppression = true; NoiseStrength = p.Item2;
            Gate = true; GateSensitivity = p.Item3;
            Eq = true; Eq100 = p.Item4; Eq250 = p.Item5; Eq1k = p.Item6; Eq4k = p.Item7; Eq10k = p.Item8;
            Compressor = true; CompressorAmount = p.Item9; Limiter = true;
            Set(ref _preset, preset, nameof(Preset));
        }
        finally
        {
            _applyingPreset = false;
        }
    }

    protected override void OnChanged(string? name)
    {
        if (_applyingPreset || name is nameof(Preset) or nameof(Enabled)) return;
        if (_preset != VoicePreset.Custom) Set(ref _preset, VoicePreset.Custom, nameof(Preset));
    }
}

public enum VoiceFxPreset { Custom, Deep, Demon, High, Chipmunk, Robot, Alien, Radio, BadSignal, Choppy }

/// <summary>Voice changer, applied after the voice chain. Off by default; presets fill in the sliders.</summary>
public sealed class VoiceFxSettings : LiveSettings
{
    private bool _enabled;
    private VoiceFxPreset _preset = VoiceFxPreset.Deep;
    private bool _applyingPreset;
    private float _pitch = -4f;
    private float _robot;
    private bool _radio;
    private float _choppy;
    private float _choppyRate = 8f;

    public bool Enabled { get => _enabled; set => Set(ref _enabled, value); }

    /// <summary>Pitch shift in semitones, −12..+12 (an octave either way).</summary>
    public float Pitch { get => _pitch; set => Set(ref _pitch, MathF.Round(Math.Clamp(value, -12f, 12f) * 2f) / 2f); }

    /// <summary>0..1: how metallic / robotic.</summary>
    public float Robot { get => _robot; set => Set(ref _robot, Math.Clamp(value, 0f, 1f)); }

    /// <summary>Walkie-talkie: narrow band plus a little crunch.</summary>
    public bool Radio { get => _radio; set => Set(ref _radio, value); }

    /// <summary>0..1: how deep the voice drops out ("прерывистость").</summary>
    public float Choppy { get => _choppy; set => Set(ref _choppy, Math.Clamp(value, 0f, 1f)); }

    /// <summary>Drop-outs per second, 2..20.</summary>
    public float ChoppyRate { get => _choppyRate; set => Set(ref _choppyRate, MathF.Round(Math.Clamp(value, 2f, 20f))); }

    /// <remarks>Deserialized last, like <see cref="VoiceSettings.Preset"/>.</remarks>
    [System.Text.Json.Serialization.JsonPropertyOrder(100)]
    public VoiceFxPreset Preset
    {
        get => _preset;
        set
        {
            if (value == VoiceFxPreset.Custom) { Set(ref _preset, value); return; }
            ApplyPreset(value);
        }
    }

    public void ApplyPreset(VoiceFxPreset preset)
    {
        // (pitch, robot, radio, choppy, choppy rate)
        var p = preset switch
        {
            VoiceFxPreset.Deep => (-4f, 0f, false, 0f, 8f),
            VoiceFxPreset.Demon => (-9f, 0.25f, false, 0f, 8f),
            VoiceFxPreset.High => (4f, 0f, false, 0f, 8f),
            VoiceFxPreset.Chipmunk => (9f, 0f, false, 0f, 8f),
            VoiceFxPreset.Robot => (0f, 1f, false, 0f, 8f),
            VoiceFxPreset.Alien => (6f, 0.5f, false, 0f, 8f),
            VoiceFxPreset.Radio => (0f, 0f, true, 0f, 8f),
            VoiceFxPreset.BadSignal => (0f, 0f, true, 0.85f, 11f),
            VoiceFxPreset.Choppy => (0f, 0f, false, 0.9f, 7f),
            _ => (_pitch, _robot, _radio, _choppy, _choppyRate),
        };
        _applyingPreset = true;
        try
        {
            Pitch = p.Item1; Robot = p.Item2; Radio = p.Item3; Choppy = p.Item4; ChoppyRate = p.Item5;
            Set(ref _preset, preset, nameof(Preset));
        }
        finally
        {
            _applyingPreset = false;
        }
    }

    protected override void OnChanged(string? name)
    {
        if (_applyingPreset || name is nameof(Preset) or nameof(Enabled)) return;
        if (_preset != VoiceFxPreset.Custom) Set(ref _preset, VoiceFxPreset.Custom, nameof(Preset));
    }
}

/// <summary>
/// Processing applied to the selected apps' sum (browser, music). All off by default: the apps then
/// reach the mic sample-for-sample as the user hears them in headphones.
/// </summary>
public sealed class AppBusSettings : LiveSettings
{
    private bool _leveler;
    private bool _lowCut;
    private bool _ducking;
    private float _duckDb = 10f;
    private bool _muted;

    /// <summary>Master off for all apps in the mic, without unticking them (hotkey "музыку убрать").</summary>
    public bool Muted { get => _muted; set => Set(ref _muted, value); }

    /// <summary>Evens out quiet and loud videos/tracks.</summary>
    public bool Leveler { get => _leveler; set => Set(ref _leveler, value); }

    /// <summary>60 Hz low cut: sub-bass that voice codecs waste bits on.</summary>
    public bool LowCut { get => _lowCut; set => Set(ref _lowCut, value); }

    /// <summary>Lower the apps while I'm speaking.</summary>
    public bool Ducking { get => _ducking; set => Set(ref _ducking, value); }

    /// <summary>How much to lower them, dB (positive).</summary>
    public float DuckDb { get => _duckDb; set => Set(ref _duckDb, Math.Clamp(value, 0f, 30f)); }
}
