using NAudio.Wave;

namespace MicMix.Hotkeys;

/// <summary>
/// Tiny two-note blip in my own headphones (default output, never the cable) so a hotkey pressed
/// mid-game is confirmed without looking: rising = turned on, falling = turned off.
/// </summary>
public static class SoundCue
{
    public static void Play(bool on)
    {
        try
        {
            Start(on);
        }
        catch
        {
            // No output device right now, or NAudio.WinMM can't be loaded (the exe was moved while
            // running): the toggle still happened, the beep is just a courtesy.
        }
    }

    // Separate method so a failure to load NAudio.WinMM (thrown when this is compiled) lands in Play's catch.
    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
    private static void Start(bool on)
    {
        var output = new WaveOutEvent { DesiredLatency = 60 };
        output.Init(new Blip(on ? 660f : 880f, on ? 880f : 660f));
        output.PlaybackStopped += (_, _) => output.Dispose();
        output.Play();
    }

    private sealed class Blip(float first, float second) : ISampleProvider
    {
        private const int Rate = 48000;
        private const int NoteSamples = Rate * 70 / 1000;
        private int _pos;

        public WaveFormat WaveFormat { get; } = WaveFormat.CreateIeeeFloatWaveFormat(Rate, 1);

        public int Read(float[] buffer, int offset, int count)
        {
            int n = Math.Min(count, NoteSamples * 2 - _pos);
            for (int i = 0; i < n; i++, _pos++)
            {
                int inNote = _pos % NoteSamples;
                float freq = _pos < NoteSamples ? first : second;
                // Short fade in/out per note so it doesn't click.
                float env = MathF.Min(1f, MathF.Min(inNote, NoteSamples - inNote) / (Rate * 0.006f));
                buffer[offset + i] = 0.12f * env * MathF.Sin(2 * MathF.PI * freq * _pos / Rate);
            }
            return n;
        }
    }
}
