using NAudio.Wave;

namespace MicMix.Audio;

/// <summary>Passes audio through unchanged and hands a copy to <see cref="Sink"/> (e.g. an OBS track).</summary>
public sealed class FeedTap(ISampleProvider source) : ISampleProvider
{
    /// <summary>Receives interleaved samples on the audio thread; must not block. Null = off.</summary>
    public volatile Action<float[], int>? Sink;

    public WaveFormat WaveFormat => source.WaveFormat;

    public int Read(float[] buffer, int offset, int count)
    {
        int read = source.Read(buffer, offset, count);
        var sink = Sink;
        if (sink != null)
        {
            if (offset == 0) sink(buffer, read);
            else
            {
                var copy = new float[read];
                Array.Copy(buffer, offset, copy, 0, read);
                sink(copy, read);
            }
        }
        return read;
    }
}
