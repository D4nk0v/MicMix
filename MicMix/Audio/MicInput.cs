using NAudio.CoreAudioApi;
using NAudio.Wave;
using NAudio.Wave.SampleProviders;

namespace MicMix.Audio;

/// <summary>
/// Captures a microphone, runs it through the voice chain and exposes it as stereo float at the
/// mixer's sample rate. This is the only signal voice effects are ever applied to.
/// </summary>
public sealed class MicInput : IDisposable
{
    private readonly WasapiCapture _capture;
    private readonly JitterBuffer _buffer;
    private float[] _scratch = new float[4096];

    /// <summary>Processed voice, stereo float at the mixer rate.</summary>
    public ISampleProvider Output { get; }

    public VoiceProcessor Voice { get; }

    public event Action<Exception>? Failed;

    public MicInput(MMDevice device, int mixerSampleRate, VoiceSettings settings, VoiceFxSettings fxSettings)
    {
        _capture = new WasapiCapture(device, useEventSync: true, audioBufferMillisecondsLength: 20);
        int captureRate = _capture.WaveFormat.SampleRate;
        _buffer = new JitterBuffer(captureRate, channels: 1);
        _capture.DataAvailable += OnData;
        _capture.RecordingStopped += (_, e) => { if (e.Exception != null) Failed?.Invoke(e.Exception); };

        ISampleProvider mono = _buffer;
        if (captureRate != VoiceProcessor.SampleRate)
            mono = new WdlResamplingSampleProvider(mono, VoiceProcessor.SampleRate);
        Voice = new VoiceProcessor(mono, settings, fxSettings);

        ISampleProvider stereo = new MonoToStereoSampleProvider(Voice);
        Output = mixerSampleRate == VoiceProcessor.SampleRate
            ? stereo
            : new WdlResamplingSampleProvider(stereo, mixerSampleRate);
    }

    public void Start() => _capture.StartRecording();

    private void OnData(object? sender, WaveInEventArgs e)
    {
        var fmt = _capture.WaveFormat;
        int channels = fmt.Channels;
        int bytesPerSample = fmt.BitsPerSample / 8;
        int frames = e.BytesRecorded / (bytesPerSample * channels);
        if (_scratch.Length < frames) _scratch = new float[frames];

        bool isFloat = fmt.Encoding == WaveFormatEncoding.IeeeFloat ||
                       (fmt is WaveFormatExtensible ext && ext.SubFormat == NAudio.Dmo.AudioMediaSubtypes.MEDIASUBTYPE_IEEE_FLOAT);

        // Downmix to mono: USB mics like the SoloCast are mono capsules exposed as two identical channels.
        for (int f = 0; f < frames; f++)
        {
            int frameOffset = f * channels * bytesPerSample;
            float sum = 0;
            for (int c = 0; c < channels; c++)
                sum += ReadSample(e.Buffer, frameOffset + c * bytesPerSample, bytesPerSample, isFloat);
            _scratch[f] = sum / channels;
        }
        _buffer.Write(_scratch, frames);
    }

    private static float ReadSample(byte[] buffer, int offset, int bytesPerSample, bool isFloat)
    {
        if (isFloat) return BitConverter.ToSingle(buffer, offset);
        return bytesPerSample switch
        {
            2 => BitConverter.ToInt16(buffer, offset) / 32768f,
            3 => ((buffer[offset] << 8 | buffer[offset + 1] << 16 | buffer[offset + 2] << 24) >> 8) / 8388608f,
            4 => BitConverter.ToInt32(buffer, offset) / 2147483648f,
            _ => 0f,
        };
    }

    public void Dispose()
    {
        try { _capture.StopRecording(); } catch { /* device may already be gone */ }
        _capture.Dispose();
        Voice.Dispose();
    }
}
