using NAudio.Wave;

namespace MicMix.Audio;

/// <summary>Applies volume/mute to one mixer input and measures its peak level for the UI meter.</summary>
public sealed class SourceChannel : ISampleProvider
{
    private readonly ISampleProvider _source;
    private float _peak;

    public SourceChannel(ISampleProvider source) => _source = source;

    public WaveFormat WaveFormat => _source.WaveFormat;

    /// <summary>Linear gain, 1 = unchanged.</summary>
    public float Volume { get; set; } = 1f;

    public bool Muted { get; set; }

    /// <summary>Peak since the last call, 0..1+. Reset on read so the UI sees fresh values.</summary>
    public float TakePeak()
    {
        float p = _peak;
        _peak = 0;
        return p;
    }

    public int Read(float[] buffer, int offset, int count)
    {
        int read = _source.Read(buffer, offset, count);
        float gain = Muted ? 0f : Volume;
        float peak = 0;
        for (int i = offset; i < offset + read; i++)
        {
            float s = buffer[i] * gain;
            buffer[i] = s;
            float a = Math.Abs(s);
            if (a > peak) peak = a;
        }
        if (peak > _peak) _peak = peak;
        return read;
    }
}
