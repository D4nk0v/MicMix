using System.Runtime.InteropServices;

namespace MicMix.Audio.Dsp;

/// <summary>
/// RNNoise neural noise suppressor (48 kHz mono, 480-sample frames). Also returns the probability
/// that the frame contains speech, which drives the voice gate and music ducking.
/// </summary>
public sealed unsafe class RnNoise : IDisposable
{
    public const int FrameSize = 480;
    private const float Scale = 32768f; // RNNoise expects 16-bit range floats

    private IntPtr _state;
    private readonly float[] _in = new float[FrameSize];

    public RnNoise() => _state = rnnoise_create(IntPtr.Zero);

    /// <summary>Denoises one frame from <paramref name="input"/> into <paramref name="output"/>; returns speech probability 0..1.</summary>
    public float Process(float[] input, float[] output)
    {
        for (int i = 0; i < FrameSize; i++) _in[i] = input[i] * Scale;
        float vad;
        fixed (float* pin = _in)
        fixed (float* pout = output)
            vad = rnnoise_process_frame(_state, pout, pin);
        for (int i = 0; i < FrameSize; i++) output[i] /= Scale;
        return vad;
    }

    public void Dispose()
    {
        if (_state == IntPtr.Zero) return;
        rnnoise_destroy(_state);
        _state = IntPtr.Zero;
    }

    [DllImport("rnnoise", CallingConvention = CallingConvention.Cdecl)]
    private static extern IntPtr rnnoise_create(IntPtr model);

    [DllImport("rnnoise", CallingConvention = CallingConvention.Cdecl)]
    private static extern void rnnoise_destroy(IntPtr state);

    [DllImport("rnnoise", CallingConvention = CallingConvention.Cdecl)]
    private static extern float rnnoise_process_frame(IntPtr state, float* output, float* input);
}
