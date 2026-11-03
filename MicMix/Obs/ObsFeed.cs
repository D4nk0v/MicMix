using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO;
using System.IO.Pipes;

namespace MicMix.Obs;

/// <summary>
/// One OBS track ("голос" or "программы"). Runs a helper copy of MicMix that plays the track into a
/// device nobody listens to, so OBS's "Application Audio Capture" can pick it up as its own source.
/// Samples go to the helper over a named pipe; the audio thread never blocks on it.
/// </summary>
public sealed class ObsFeed : IDisposable
{
    public const string HelperArg = "--obs-feed";

    private readonly string _name;
    private readonly object _lock = new();
    private BlockingCollection<float[]>? _queue;
    private Process? _helper;
    private NamedPipeServerStream? _pipe;
    private string? _deviceId;
    private int _sampleRate;
    private volatile bool _connected;
    private DateTime _restartAfter = DateTime.MinValue;

    /// <param name="name">"voice" or "apps": part of the helper's window title OBS matches on.</param>
    public ObsFeed(string name, string title)
    {
        _name = name;
        Title = title;
    }

    /// <summary>The helper's window title, as it appears in OBS's window list.</summary>
    public string Title { get; }

    public string Status { get; private set; } = "выключено";

    /// <summary>Starts the helper, or restarts it if it died or the device/sample rate changed. Call periodically.</summary>
    public void Ensure(string deviceId, int sampleRate)
    {
        lock (_lock)
        {
            bool alive = _helper is { HasExited: false };
            if (alive && _deviceId == deviceId && _sampleRate == sampleRate)
            {
                Status = _connected ? "работает" : "запускается…";
                return;
            }
            if (alive || _helper != null) StopLocked();
            if (DateTime.UtcNow < _restartAfter) return;

            _deviceId = deviceId;
            _sampleRate = sampleRate;
            // The pipe carries the live voice. Unguessable name, current-user-only ACL, and Send() checks that the
            // client is the helper we started, so no other local process can connect first and listen in.
            string pipeName = $"MicMix.Obs.{_name}.{Guid.NewGuid():N}";
            try
            {
                _pipe = new NamedPipeServerStream(pipeName, PipeDirection.Out, 1, PipeTransmissionMode.Byte,
                    PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
                _queue = new BlockingCollection<float[]>(boundedCapacity: 100);
                _helper = Process.Start(new ProcessStartInfo(Environment.ProcessPath!)
                {
                    ArgumentList = { HelperArg, pipeName, deviceId, sampleRate.ToString(), Title },
                    UseShellExecute = false,
                    CreateNoWindow = true,
                }) ?? throw new InvalidOperationException("helper did not start");
                var pipe = _pipe;
                var queue = _queue;
                int helperPid = _helper.Id;
                new Thread(() => Send(pipe, queue, helperPid)) { IsBackground = true, Name = $"OBS feed {_name}" }.Start();
                Status = "запускается…";
            }
            catch (Exception ex)
            {
                Status = "ошибка: " + ex.Message;
                StopLocked();
                _restartAfter = DateTime.UtcNow.AddSeconds(5);
            }
        }
    }

    private void Send(NamedPipeServerStream pipe, BlockingCollection<float[]> queue, int helperPid)
    {
        try
        {
            if (!pipe.WaitForConnectionAsync().Wait(TimeSpan.FromSeconds(10)))
                throw new TimeoutException("helper did not connect");
            if (!GetNamedPipeClientProcessId(pipe.SafePipeHandle, out uint clientPid) || clientPid != helperPid)
                throw new InvalidOperationException("unexpected process on the OBS pipe");
            _connected = true;
            var bytes = new byte[0];
            foreach (var block in queue.GetConsumingEnumerable())
            {
                int n = block.Length * sizeof(float);
                if (bytes.Length < n) bytes = new byte[n];
                Buffer.BlockCopy(block, 0, bytes, 0, n);
                pipe.Write(bytes, 0, n);
            }
        }
        catch (Exception ex) when (ex is IOException or TimeoutException or ObjectDisposedException or AggregateException or InvalidOperationException)
        {
            // Helper gone; Ensure() notices and restarts it.
        }
        finally
        {
            _connected = false;
            lock (_lock)
            {
                if (ReferenceEquals(pipe, _pipe)) _restartAfter = DateTime.UtcNow.AddSeconds(2);
            }
        }
    }

    /// <summary>Called from the audio thread with interleaved stereo floats.</summary>
    public void Write(float[] samples, int count)
    {
        var queue = _queue;
        if (!_connected || queue == null) return;
        var block = new float[count];
        Array.Copy(samples, block, count);
        if (!queue.TryAdd(block)) { /* helper is stalling; drop rather than build latency */ }
    }

    public void Stop()
    {
        lock (_lock)
        {
            StopLocked();
            Status = "выключено";
        }
    }

    private void StopLocked()
    {
        _connected = false;
        _queue?.CompleteAdding();
        _queue = null;
        try { _pipe?.Dispose(); } catch { }
        _pipe = null;
        try { if (_helper is { HasExited: false }) _helper.Kill(); } catch { }
        _helper?.Dispose();
        _helper = null;
        _deviceId = null;
    }

    public void Dispose() => Stop();

    [System.Runtime.InteropServices.DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GetNamedPipeClientProcessId(Microsoft.Win32.SafeHandles.SafePipeHandle pipe, out uint clientProcessId);
}
