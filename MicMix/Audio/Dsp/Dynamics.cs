namespace MicMix.Audio.Dsp;

public static class Db
{
    public static float ToLinear(float db) => MathF.Pow(10f, db / 20f);
    public static float FromLinear(float lin) => 20f * MathF.Log10(MathF.Max(lin, 1e-9f));

    /// <summary>One-pole smoothing coefficient for a time constant.</summary>
    public static float Coef(float ms, int sampleRate) => 1f - MathF.Exp(-1f / (ms * 0.001f * sampleRate));
}

/// <summary>
/// Feed-forward compressor, peak envelope, channels linked so stereo images don't wander.
/// Works on interleaved buffers.
/// </summary>
public sealed class Compressor
{
    private readonly int _sampleRate;
    private readonly int _channels;
    private float _env;

    public float ThresholdDb { get; set; } = -20f;
    public float Ratio { get; set; } = 3f;
    public float AttackMs { get; set; } = 5f;
    public float ReleaseMs { get; set; } = 120f;
    public float MakeupDb { get; set; }

    /// <summary>Current gain reduction in dB (positive number), for meters.</summary>
    public float GainReductionDb { get; private set; }

    public Compressor(int sampleRate, int channels)
    {
        _sampleRate = sampleRate;
        _channels = channels;
    }

    public void Process(float[] buffer, int offset, int count)
    {
        float att = Db.Coef(AttackMs, _sampleRate);
        float rel = Db.Coef(ReleaseMs, _sampleRate);
        float slope = 1f - 1f / MathF.Max(Ratio, 1f);
        float makeup = MakeupDb;
        float maxGr = 0;

        for (int i = offset; i < offset + count; i += _channels)
        {
            float peak = 0;
            for (int c = 0; c < _channels; c++) peak = MathF.Max(peak, MathF.Abs(buffer[i + c]));
            _env += (peak - _env) * (peak > _env ? att : rel);

            float over = Db.FromLinear(_env) - ThresholdDb;
            float gr = over > 0 ? over * slope : 0;
            if (gr > maxGr) maxGr = gr;
            float gain = Db.ToLinear(makeup - gr);
            for (int c = 0; c < _channels; c++) buffer[i + c] *= gain;
        }
        GainReductionDb = maxGr;
    }
}

/// <summary>Brick-wall peak limiter: instant attack, smooth release. Guarantees nothing exceeds the ceiling.</summary>
public sealed class Limiter
{
    private readonly int _channels;
    private readonly float _release;
    private float _gain = 1f;

    public float CeilingDb { get; set; } = -1f;

    public Limiter(int sampleRate, int channels, float releaseMs = 60f)
    {
        _channels = channels;
        _release = Db.Coef(releaseMs, sampleRate);
    }

    public void Process(float[] buffer, int offset, int count)
    {
        float ceiling = Db.ToLinear(CeilingDb);
        for (int i = offset; i < offset + count; i += _channels)
        {
            float peak = 0;
            for (int c = 0; c < _channels; c++) peak = MathF.Max(peak, MathF.Abs(buffer[i + c]));
            float target = peak > ceiling ? ceiling / peak : 1f;
            _gain = target < _gain ? target : _gain + (target - _gain) * _release;
            for (int c = 0; c < _channels; c++) buffer[i + c] *= _gain;
        }
    }
}

/// <summary>
/// Gain that follows an on/off decision smoothly: fast attack, hold, slower release.
/// Used both for the voice gate (open while speaking) and for ducking (lower while speaking).
/// </summary>
public sealed class SmoothGate
{
    private readonly int _sampleRate;
    private int _holdLeft;
    private float _gain;

    public float AttackMs { get; set; } = 5f;
    public float HoldMs { get; set; } = 250f;
    public float ReleaseMs { get; set; } = 150f;

    public SmoothGate(int sampleRate, float initialGain = 1f)
    {
        _sampleRate = sampleRate;
        _gain = initialGain;
    }

    /// <summary>Advance by one block: open/closed decision for it, gains to move between.</summary>
    public void Process(float[] buffer, int offset, int count, int channels, bool trigger, float openGain, float closedGain)
    {
        int frames = count / channels;
        if (trigger) _holdLeft = (int)(HoldMs * 0.001f * _sampleRate);
        else _holdLeft = Math.Max(0, _holdLeft - frames);

        float target = trigger || _holdLeft > 0 ? openGain : closedGain;
        float coef = Db.Coef(target > _gain ? AttackMs : ReleaseMs, _sampleRate);
        for (int i = offset; i < offset + count; i += channels)
        {
            _gain += (target - _gain) * coef;
            for (int c = 0; c < channels; c++) buffer[i + c] *= _gain;
        }
    }
}
