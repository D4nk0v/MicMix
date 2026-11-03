using System.IO;
using System.IO.Pipes;
using System.Windows;
using MicMix.Audio;
using NAudio.CoreAudioApi;
using NAudio.Wave;
using NAudio.Wave.SampleProviders;

namespace MicMix.Obs;

/// <summary>
/// The helper side of <see cref="ObsFeed"/>: receives one track over the pipe and plays it into the
/// silent device. Exits as soon as the main MicMix closes the pipe (or dies).
/// </summary>
public static class ObsFeedHost
{
    /// <param name="args">pipeName, deviceId, sampleRate, windowTitle</param>
    public static void Run(Application app, string[] args)
    {
        string pipeName = args[0], deviceId = args[1], title = args[3];
        int sampleRate = int.Parse(args[2]);

        // OBS "Application Audio Capture" picks programs by their window, so this process needs a
        // visible top-level window. Kept off-screen, out of the taskbar and Alt+Tab, never focused.
        var window = new Window
        {
            Title = title,
            Width = 1, Height = 1, Left = -32000, Top = -32000,
            WindowStyle = WindowStyle.None, ResizeMode = ResizeMode.NoResize,
            ShowInTaskbar = false, ShowActivated = false, Focusable = false,
        };
        window.Show();

        // WASAPI objects live on their own MTA thread, like the main engine's.
        var thread = new Thread(() =>
        {
            int exitCode = 0;
            try
            {
                Pump(pipeName, deviceId, sampleRate);
            }
            catch (Exception ex)
            {
                exitCode = 2;
                Log(title + ": " + ex);
            }
            app.Dispatcher.BeginInvoke(() => app.Shutdown(exitCode));
        }) { IsBackground = true, Name = "OBS feed" };
        thread.SetApartmentState(ApartmentState.MTA);
        thread.Start();
    }

    /// <summary>Helpers have no UI, so failures go to %AppData%\MicMix\obs-feed.log.</summary>
    private static void Log(string message)
    {
        try
        {
            string dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "MicMix");
            Directory.CreateDirectory(dir);
            File.AppendAllText(Path.Combine(dir, "obs-feed.log"), $"{DateTime.Now:yyyy-MM-dd HH:mm:ss} {message}{Environment.NewLine}");
        }
        catch { }
    }

    private static void Pump(string pipeName, string deviceId, int sampleRate)
    {
        var buffer = new JitterBuffer(sampleRate, channels: 2, targetMs: 40, maxMs: 150);
        using var enumerator = new MMDeviceEnumerator();
        var device = enumerator.GetDevice(deviceId);
        int deviceRate = device.AudioClient.MixFormat.SampleRate;
        ISampleProvider feed = deviceRate == sampleRate ? buffer : new WdlResamplingSampleProvider(buffer, deviceRate);
        using var output = new WasapiOut(device, AudioClientShareMode.Shared, useEventSync: true, latency: 30);
        output.PlaybackStopped += (_, e) => { if (e.Exception != null) Log("playback stopped: " + e.Exception); };
        output.Init(feed);
        output.Play();

        // CurrentUserOnly: refuse a pipe of that name owned by anyone but us.
        using var pipe = new NamedPipeClientStream(".", pipeName, PipeDirection.In, PipeOptions.CurrentUserOnly);
        pipe.Connect(5000);
        var bytes = new byte[sampleRate * 2 * sizeof(float) / 50]; // 20 ms
        var samples = new float[bytes.Length / sizeof(float)];
        int carry = 0;
        try
        {
            while (true)
            {
                int read = pipe.Read(bytes, carry, bytes.Length - carry);
                if (read == 0) break; // main MicMix closed the pipe
                int total = carry + read;
                int usable = total - total % (2 * sizeof(float)); // whole stereo frames only
                Buffer.BlockCopy(bytes, 0, samples, 0, usable);
                buffer.Write(samples, usable / sizeof(float));
                carry = total - usable;
                if (carry > 0) Array.Copy(bytes, usable, bytes, 0, carry);
            }
        }
        catch (IOException) { /* main MicMix died */ }
        output.Stop();
    }
}
