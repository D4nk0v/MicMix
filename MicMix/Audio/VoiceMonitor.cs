using System.Diagnostics;
using NAudio.CoreAudioApi;
using NAudio.Wave;
using NAudio.Wave.SampleProviders;

namespace MicMix.Audio;

/// <summary>"Послушать себя": copies a signal into the default headphones/speakers.</summary>
public sealed class VoiceMonitor : IDisposable
{
    private readonly int _sampleRate;
    private readonly object _lock = new();
    private JitterBuffer? _buffer;
    private WasapiOut? _out;

    public VoiceMonitor(int sampleRate) => _sampleRate = sampleRate;

    /// <summary>Wraps <paramref name="source"/> so whatever passes through is also heard while monitoring.</summary>
    public ISampleProvider Tap(ISampleProvider source) => new TapProvider(source, this);

    public void SetEnabled(bool enabled)
    {
        lock (_lock)
        {
            if (enabled == (_out != null)) return;
            if (!enabled)
            {
                Stop();
                return;
            }

            try
            {
                using var enumerator = new MMDeviceEnumerator();
                var device = enumerator.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia);
                if (device.FriendlyName.Contains("CABLE", StringComparison.OrdinalIgnoreCase)) return; // would loop
                var buffer = new JitterBuffer(_sampleRate, channels: 2, targetMs: 30, maxMs: 120);
                int deviceRate = device.AudioClient.MixFormat.SampleRate;
                ISampleProvider feed = deviceRate == _sampleRate ? buffer : new WdlResamplingSampleProvider(buffer, deviceRate);
                var output = new WasapiOut(device, AudioClientShareMode.Shared, useEventSync: true, latency: 30);
                output.Init(feed);
                output.Play();
                _buffer = buffer;
                _out = output;
            }
            catch (Exception ex)
            {
                Debug.WriteLine("Monitor start failed: " + ex.Message);
                Stop();
            }
        }
    }

    private void Stop()
    {
        _buffer = null;
        try { _out?.Stop(); } catch { }
        _out?.Dispose();
        _out = null;
    }

    public void Dispose()
    {
        lock (_lock) Stop();
    }

    private sealed class TapProvider(ISampleProvider source, VoiceMonitor monitor) : ISampleProvider
    {
        private float[] _copy = Array.Empty<float>();

        public WaveFormat WaveFormat => source.WaveFormat;

        public int Read(float[] buffer, int offset, int count)
        {
            int read = source.Read(buffer, offset, count);
            var target = monitor._buffer;
            if (target != null)
            {
                if (_copy.Length < read) _copy = new float[read];
                Array.Copy(buffer, offset, _copy, 0, read);
                target.Write(_copy, read);
            }
            return read;
        }
    }
}
