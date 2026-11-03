using NAudio.Wave;

namespace MicMix.Audio;

/// <summary>
/// Bridges a capture device's clock to the output device's clock. Capture threads push samples in,
/// the output pulls them out. Keeps latency bounded: if the writer runs ahead the oldest audio is
/// dropped, if it falls behind we output silence and re-prime instead of crackling.
/// </summary>
public sealed class JitterBuffer : ISampleProvider
{
    private readonly float[] _ring;
    private readonly int _targetSamples;
    private readonly int _maxSamples;
    private readonly object _lock = new();
    private int _readPos;
    private int _count;
    private bool _priming = true;

    public WaveFormat WaveFormat { get; }

    public JitterBuffer(int sampleRate, int channels = 2, int targetMs = 25, int maxMs = 90)
    {
        WaveFormat = WaveFormat.CreateIeeeFloatWaveFormat(sampleRate, channels);
        int samplesPerMs = sampleRate * channels / 1000;
        _targetSamples = targetMs * samplesPerMs;
        _maxSamples = maxMs * samplesPerMs;
        _ring = new float[sampleRate * channels]; // 1 second
    }

    public void Write(float[] samples, int count)
    {
        int channels = WaveFormat.Channels;
        lock (_lock)
        {
            if (count > _ring.Length) count = _ring.Length;
            int writePos = (_readPos + _count) % _ring.Length;
            int first = Math.Min(count, _ring.Length - writePos);
            Array.Copy(samples, 0, _ring, writePos, first);
            Array.Copy(samples, first, _ring, 0, count - first);
            _count = Math.Min(_count + count, _ring.Length);

            if (_count > _maxSamples)
            {
                int drop = _count - _targetSamples;
                drop -= drop % channels;
                _readPos = (_readPos + drop) % _ring.Length;
                _count -= drop;
            }
        }
    }

    public int Read(float[] buffer, int offset, int count)
    {
        lock (_lock)
        {
            if (_priming && _count >= _targetSamples) _priming = false;

            // Element-wise on purpose: when WasapiOut reads us directly, NAudio hands in a byte[]
            // disguised as float[] (WaveBuffer), and Array.Copy/Array.Clear throw or clear the wrong range.
            int available = _priming ? 0 : Math.Min(count, _count);
            for (int i = 0; i < available; i++)
            {
                buffer[offset + i] = _ring[_readPos];
                if (++_readPos == _ring.Length) _readPos = 0;
            }
            _count -= available;

            if (available < count)
            {
                for (int i = available; i < count; i++) buffer[offset + i] = 0f;
                if (!_priming) _priming = true;
            }
        }
        return count;
    }
}
