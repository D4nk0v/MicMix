using NAudio.Dsp;

namespace MicMix.Audio.Dsp;

/// <summary>
/// Voice changer for the mono 48 kHz voice chain: pitch → robot → radio → choppy.
/// Every stage is skipped when it is at its neutral value, so "off" costs nothing and changes nothing.
/// </summary>
public sealed class VoiceFx
{
    private readonly VoiceFxSettings _s;
    private PitchShifter _pitch;
    private bool _pitchWasOn;
    private readonly int _sampleRate;
    private readonly BiQuadFilter _radioLow, _radioHigh, _radioLow2, _radioHigh2;
    private double _robotPhase, _chopPhase;
    private float _chopGain = 1f;
    private readonly float _chopSmooth;

    public VoiceFx(int sampleRate, VoiceFxSettings settings)
    {
        _sampleRate = sampleRate;
        _s = settings;
        _pitch = new PitchShifter(sampleRate);
        // Telephone / walkie-talkie band: 4th-order 400 Hz – 3 kHz.
        _radioHigh = BiQuadFilter.HighPassFilter(sampleRate, 400f, 0.707f);
        _radioHigh2 = BiQuadFilter.HighPassFilter(sampleRate, 400f, 0.707f);
        _radioLow = BiQuadFilter.LowPassFilter(sampleRate, 3000f, 0.707f);
        _radioLow2 = BiQuadFilter.LowPassFilter(sampleRate, 3000f, 0.707f);
        _chopSmooth = Db.Coef(4f, sampleRate); // 4 ms edges: choppy, but no clicks
    }

    public void Process(float[] buffer, int count)
    {
        if (!_s.Enabled) return;

        bool pitchOn = Math.Abs(_s.Pitch) >= 0.05f;
        if (pitchOn)
        {
            // Coming back from bypass: drop the 32 ms of old voice still sitting in its buffers.
            if (!_pitchWasOn) _pitch = new PitchShifter(_sampleRate);
            _pitch.Ratio = MathF.Pow(2f, _s.Pitch / 12f);
            _pitch.Process(buffer, count);
        }
        _pitchWasOn = pitchOn;

        float robot = _s.Robot;
        if (robot > 0.001f)
        {
            // Ring modulation at a low, fixed carrier: the classic metallic "robot" voice.
            double step = 2 * Math.PI * 70.0 / _sampleRate;
            for (int i = 0; i < count; i++)
            {
                float carrier = (float)Math.Sin(_robotPhase);
                _robotPhase += step;
                if (_robotPhase > 2 * Math.PI) _robotPhase -= 2 * Math.PI;
                // Ring modulation halves perceived loudness; compensate so the slider doesn't just turn it down.
                buffer[i] *= (1f - robot) + robot * carrier * 1.6f;
            }
        }

        if (_s.Radio)
        {
            for (int i = 0; i < count; i++)
            {
                float x = _radioLow2.Transform(_radioLow.Transform(_radioHigh2.Transform(_radioHigh.Transform(buffer[i]))));
                // Soft saturation: the crunchy edge of a cheap speaker. tanh(3) ≈ 0.995 keeps peaks below 1.
                buffer[i] = MathF.Tanh(x * 3f) * 0.9f;
            }
        }

        float depth = _s.Choppy;
        if (depth > 0.001f)
        {
            double step = _s.ChoppyRate / _sampleRate;
            float closed = 1f - depth;
            for (int i = 0; i < count; i++)
            {
                _chopPhase += step;
                if (_chopPhase >= 1) _chopPhase -= 1;
                float target = _chopPhase < 0.55 ? 1f : closed; // slightly more "on" than "off" keeps words readable
                _chopGain += (target - _chopGain) * _chopSmooth;
                buffer[i] *= _chopGain;
            }
        }
    }

    /// <summary>
    /// Phase-vocoder pitch shifter (2048-point FFT, 4× overlap): every bin's true frequency is
    /// measured from its phase advance, scaled by the ratio and resynthesised, so the pitch lands
    /// exactly on the requested interval without changing tempo. Latency is N − hop = 1536 samples
    /// (32 ms at 48 kHz). Formants move with the pitch, which is the fun part of a voice changer.
    /// </summary>
    private sealed class PitchShifter
    {
        private const int N = 2048, Osamp = 4, Hop = N / Osamp, Latency = N - Hop, Half = N / 2;
        private readonly float[] _inFifo = new float[N], _outFifo = new float[N], _accum = new float[2 * N];
        private readonly float[] _re = new float[N], _im = new float[N];
        private readonly float[] _lastPhase = new float[Half + 1], _sumPhase = new float[Half + 1];
        private readonly float[] _anaMagn = new float[Half + 1], _anaFreq = new float[Half + 1];
        private readonly float[] _synMagn = new float[Half + 1], _synFreq = new float[Half + 1];
        private readonly float[] _window = new float[N];
        private readonly float _binHz;
        private int _rover = Latency;

        public float Ratio { get; set; } = 1f;

        public PitchShifter(int sampleRate)
        {
            _binHz = (float)sampleRate / N;
            for (int k = 0; k < N; k++) _window[k] = 0.5f - 0.5f * MathF.Cos(2 * MathF.PI * k / N);
        }

        public void Process(float[] buffer, int count)
        {
            for (int i = 0; i < count; i++)
            {
                _inFifo[_rover] = buffer[i];
                buffer[i] = _outFifo[_rover - Latency];
                if (++_rover < N) continue;
                _rover = Latency;
                ProcessFrame();
            }
        }

        private void ProcessFrame()
        {
            const float expected = 2 * MathF.PI * Hop / N;
            for (int k = 0; k < N; k++) { _re[k] = _inFifo[k] * _window[k]; _im[k] = 0; }
            Fft(_re, _im, inverse: false);

            // Analysis: magnitude and true frequency of each bin.
            for (int k = 0; k <= Half; k++)
            {
                float magn = 2 * MathF.Sqrt(_re[k] * _re[k] + _im[k] * _im[k]);
                float phase = MathF.Atan2(_im[k], _re[k]);
                float d = phase - _lastPhase[k] - k * expected;
                _lastPhase[k] = phase;
                d -= 2 * MathF.PI * MathF.Round(d / (2 * MathF.PI)); // wrap to ±π
                _anaMagn[k] = magn;
                _anaFreq[k] = (k + d * Osamp / (2 * MathF.PI)) * _binHz;
            }

            // Shift: move each bin's energy and frequency up or down by the ratio.
            Array.Clear(_synMagn); Array.Clear(_synFreq);
            float ratio = Ratio;
            for (int k = 0; k <= Half; k++)
            {
                int j = (int)(k * ratio);
                if (j > Half) break;
                _synMagn[j] += _anaMagn[k];
                _synFreq[j] = _anaFreq[k] * ratio;
            }

            // Synthesis: accumulate phase from the new frequencies, then inverse FFT and overlap-add.
            for (int k = 0; k <= Half; k++)
            {
                float d = (_synFreq[k] / _binHz - k) * 2 * MathF.PI / Osamp + k * expected;
                _sumPhase[k] += d;
                _sumPhase[k] -= 2 * MathF.PI * MathF.Round(_sumPhase[k] / (2 * MathF.PI)); // keep float precision
                _re[k] = _synMagn[k] * MathF.Cos(_sumPhase[k]);
                _im[k] = _synMagn[k] * MathF.Sin(_sumPhase[k]);
            }
            for (int k = Half + 1; k < N; k++) _re[k] = _im[k] = 0;
            Fft(_re, _im, inverse: true);

            float scale = 2f / (Half * Osamp);
            for (int k = 0; k < N; k++) _accum[k] += _window[k] * _re[k] * scale;
            Array.Copy(_accum, _outFifo, Hop);
            Array.Copy(_accum, Hop, _accum, 0, N);
            Array.Clear(_accum, N, Hop);
            Array.Copy(_inFifo, Hop, _inFifo, 0, Latency);
        }

        /// <summary>In-place radix-2 complex FFT, unnormalised in both directions.</summary>
        private static void Fft(float[] re, float[] im, bool inverse)
        {
            int n = re.Length;
            for (int i = 1, j = 0; i < n; i++)
            {
                int bit = n >> 1;
                for (; (j & bit) != 0; bit >>= 1) j ^= bit;
                j ^= bit;
                if (i < j) { (re[i], re[j]) = (re[j], re[i]); (im[i], im[j]) = (im[j], im[i]); }
            }
            for (int len = 2; len <= n; len <<= 1)
            {
                double ang = 2 * Math.PI / len * (inverse ? 1 : -1);
                float wr = (float)Math.Cos(ang), wi = (float)Math.Sin(ang);
                for (int i = 0; i < n; i += len)
                {
                    float cr = 1, ci = 0;
                    for (int k = 0; k < len / 2; k++)
                    {
                        int a = i + k, b = a + len / 2;
                        float tr = re[b] * cr - im[b] * ci, ti = re[b] * ci + im[b] * cr;
                        re[b] = re[a] - tr; im[b] = im[a] - ti;
                        re[a] += tr; im[a] += ti;
                        float ncr = cr * wr - ci * wi;
                        ci = cr * wi + ci * wr;
                        cr = ncr;
                    }
                }
            }
        }
    }
}
