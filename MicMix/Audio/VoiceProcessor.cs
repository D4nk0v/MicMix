using MicMix.Audio.Dsp;
using NAudio.Dsp;
using NAudio.Wave;

namespace MicMix.Audio;

/// <summary>
/// The voice chain, mono 48 kHz, in 10 ms frames:
/// high-pass → RNNoise → speech gate → 5-band EQ → compressor → voice changer → limiter.
/// RNNoise always runs (even when the chain is off) because its speech detection drives ducking.
/// </summary>
public sealed class VoiceProcessor : ISampleProvider, IDisposable
{
    public const int SampleRate = 48000;
    private const int Frame = RnNoise.FrameSize;

    /// <summary>RNNoise output lags its input by this many samples; the dry path is delayed to match.</summary>
    public const int RnNoiseDelay = 480;

    private readonly ISampleProvider _source;
    private readonly VoiceSettings _s;
    private readonly RnNoise _rnnoise = new();
    private readonly float[] _frame = new float[Frame];
    private readonly float[] _wet = new float[Frame];
    private readonly float[] _dryDelay = new float[RnNoiseDelay + Frame];
    private readonly float[] _out = new float[Frame];
    private int _outPos = Frame; // nothing buffered yet

    private readonly BiQuadFilter _highPass = BiQuadFilter.HighPassFilter(SampleRate, 80f, 0.707f);
    private readonly BiQuadFilter[] _eq = new BiQuadFilter[5];
    private readonly SmoothGate _gate = new(SampleRate, initialGain: 0f) { AttackMs = 3f, HoldMs = 300f, ReleaseMs = 120f };
    private readonly Compressor _compressor = new(SampleRate, 1) { AttackMs = 5f, ReleaseMs = 150f };
    private readonly Limiter _limiter = new(SampleRate, 1);
    private readonly VoiceFxSettings _fxSettings;
    private readonly VoiceFx _fx;
    private int _voiceHoldFrames;
    private int _talkRun, _talkHoldFrames;
    private (float, float, float, float, float)? _eqGains;

    public WaveFormat WaveFormat { get; } = WaveFormat.CreateIeeeFloatWaveFormat(SampleRate, 1);

    /// <summary>True while I'm speaking (with a short hold), by the gate's threshold. Lights the UI lamp.</summary>
    public bool VoiceActive { get; private set; }

    /// <summary>
    /// Stricter "I'm talking" for ducking: confident speech for 60 ms in a row, then held through pauses.
    /// Clicks, breaths and keyboard score a frame or two of "speech" and must not pump the music.
    /// </summary>
    public bool Talking { get; private set; }

    /// <summary>Last frame's speech probability, 0..1.</summary>
    public float SpeechProbability { get; private set; }

    public VoiceProcessor(ISampleProvider monoSource48k, VoiceSettings settings, VoiceFxSettings fxSettings)
    {
        if (monoSource48k.WaveFormat.SampleRate != SampleRate || monoSource48k.WaveFormat.Channels != 1)
            throw new ArgumentException("Voice chain needs mono 48 kHz input.");
        _source = monoSource48k;
        _s = settings;
        _fxSettings = fxSettings;
        _fx = new VoiceFx(SampleRate, fxSettings);
    }

    public int Read(float[] buffer, int offset, int count)
    {
        int written = 0;
        while (written < count)
        {
            if (_outPos == Frame)
            {
                ProcessFrame();
                _outPos = 0;
            }
            int n = Math.Min(count - written, Frame - _outPos);
            Array.Copy(_out, _outPos, buffer, offset + written, n);
            _outPos += n;
            written += n;
        }
        return count;
    }

    private void ProcessFrame()
    {
        int read = _source.Read(_frame, 0, Frame);
        if (read < Frame) Array.Clear(_frame, read, Frame - read);

        bool on = _s.Enabled;
        if (on && _s.HighPass)
            for (int i = 0; i < Frame; i++) _frame[i] = _highPass.Transform(_frame[i]);

        // Dry path delayed so it lines up with RNNoise's output when blending.
        Array.Copy(_dryDelay, Frame, _dryDelay, 0, RnNoiseDelay);
        Array.Copy(_frame, 0, _dryDelay, RnNoiseDelay, Frame);

        float speech = _rnnoise.Process(_frame, _wet);
        SpeechProbability = speech;
        bool speaking = speech >= _s.VadThreshold;
        _voiceHoldFrames = speaking ? 30 : Math.Max(0, _voiceHoldFrames - 1); // 300 ms hold
        VoiceActive = _voiceHoldFrames > 0;
        _talkRun = speech >= 0.7f ? _talkRun + 1 : 0;
        _talkHoldFrames = _talkRun >= 6 ? 50 : Math.Max(0, _talkHoldFrames - 1); // 500 ms hold
        Talking = _talkHoldFrames > 0;

        if (on && _s.NoiseSuppression)
        {
            float wet = _s.NoiseStrength, dry = 1f - wet;
            for (int i = 0; i < Frame; i++) _out[i] = _wet[i] * wet + _dryDelay[i] * dry;
        }
        else
        {
            Array.Copy(on ? _dryDelay : _frame, 0, _out, 0, Frame);
        }
        if (on) ProcessChain(speaking);

        // The voice changer works with or without the clean-up chain; when it's active the limiter
        // always runs, since pitch and saturation can push peaks up.
        _fx.Process(_out, Frame);
        if ((on && _s.Limiter) || _fxSettings.Enabled)
            _limiter.Process(_out, 0, Frame);
    }

    private void ProcessChain(bool speaking)
    {
        if (_s.Gate)
            _gate.Process(_out, 0, Frame, 1, speaking, 1f, Db.ToLinear(-60f));

        if (_s.Eq)
        {
            UpdateEq();
            foreach (var band in _eq)
                for (int i = 0; i < Frame; i++) _out[i] = band.Transform(_out[i]);
        }

        if (_s.Compressor)
        {
            float a = _s.CompressorAmount;
            _compressor.ThresholdDb = -12f - 18f * a;
            _compressor.Ratio = 2f + 4f * a;
            // Bring the levelled voice back up; half the theoretical reduction at 0 dBFS sounds natural.
            _compressor.MakeupDb = -_compressor.ThresholdDb * (1f - 1f / _compressor.Ratio) * 0.5f;
            _compressor.Process(_out, 0, Frame);
        }
    }

    private void UpdateEq()
    {
        var gains = (_s.Eq100, _s.Eq250, _s.Eq1k, _s.Eq4k, _s.Eq10k);
        if (_eqGains == gains) return;
        _eqGains = gains;
        // Recreating filters resets their state; only happens while a slider moves, inaudible.
        _eq[0] = BiQuadFilter.LowShelf(SampleRate, 100f, 0.8f, _s.Eq100);
        _eq[1] = BiQuadFilter.PeakingEQ(SampleRate, 250f, 1.0f, _s.Eq250);
        _eq[2] = BiQuadFilter.PeakingEQ(SampleRate, 1000f, 1.0f, _s.Eq1k);
        _eq[3] = BiQuadFilter.PeakingEQ(SampleRate, 4000f, 1.0f, _s.Eq4k);
        _eq[4] = BiQuadFilter.HighShelf(SampleRate, 10000f, 0.8f, _s.Eq10k);
    }

    public void Dispose() => _rnnoise.Dispose();
}
