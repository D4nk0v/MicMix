using MicMix.Audio.Dsp;
using NAudio.Dsp;
using NAudio.Wave;

namespace MicMix.Audio;

/// <summary>
/// Processing for the sum of all selected apps (stereo): low cut → leveler → ducking under my voice.
/// </summary>
public sealed class AppBusProcessor : ISampleProvider
{
    private readonly ISampleProvider _source;
    private readonly AppBusSettings _s;
    private readonly Func<bool> _voiceActive;
    private readonly BiQuadFilter _lowCutL, _lowCutR;
    private readonly Compressor _leveler;
    private readonly SmoothGate _ducker;
    private readonly SmoothGate _mute;

    public WaveFormat WaveFormat => _source.WaveFormat;

    /// <summary>True while the apps are being lowered because I'm speaking.</summary>
    public bool Ducking { get; private set; }

    public AppBusProcessor(ISampleProvider stereoSource, AppBusSettings settings, Func<bool> voiceActive)
    {
        _source = stereoSource;
        _s = settings;
        _voiceActive = voiceActive;
        int rate = stereoSource.WaveFormat.SampleRate;
        _lowCutL = BiQuadFilter.HighPassFilter(rate, 60f, 0.707f);
        _lowCutR = BiQuadFilter.HighPassFilter(rate, 60f, 0.707f);
        // Slow, broadcast-style levelling: evens out tracks and videos without pumping on every beat.
        _leveler = new Compressor(rate, 2) { ThresholdDb = -26f, Ratio = 4f, AttackMs = 30f, ReleaseMs = 600f, MakeupDb = 8f };
        // Duck down fast when I start talking, come back slowly. ("Attack" = rising gain here.)
        _ducker = new SmoothGate(rate) { AttackMs = 450f, HoldMs = 0f, ReleaseMs = 60f };
        // Short fades so the master mute never clicks.
        _mute = new SmoothGate(rate) { AttackMs = 40f, HoldMs = 0f, ReleaseMs = 40f };
    }

    public int Read(float[] buffer, int offset, int count)
    {
        int read = _source.Read(buffer, offset, count);

        if (_s.LowCut)
        {
            for (int i = offset; i + 1 < offset + read; i += 2)
            {
                buffer[i] = _lowCutL.Transform(buffer[i]);
                buffer[i + 1] = _lowCutR.Transform(buffer[i + 1]);
            }
        }

        if (_s.Leveler) _leveler.Process(buffer, offset, read);

        bool duck = _s.Ducking && _voiceActive();
        Ducking = duck;
        // The ducker runs even when disabled so turning it off mid-sentence glides back up.
        _ducker.Process(buffer, offset, read, 2, trigger: !duck, openGain: 1f, closedGain: Db.ToLinear(-_s.DuckDb));
        _mute.Process(buffer, offset, read, 2, trigger: !_s.Muted, openGain: 1f, closedGain: 0f);
        return read;
    }
}
