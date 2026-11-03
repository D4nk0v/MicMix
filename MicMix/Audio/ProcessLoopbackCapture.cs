using System.Runtime.InteropServices;
using NAudio.CoreAudioApi;
using NAudio.CoreAudioApi.Interfaces;
using NAudio.Wave;

namespace MicMix.Audio;

/// <summary>
/// Captures everything a single process (and its child processes) renders, regardless of which
/// output device it plays to. Uses the Windows process-loopback API (Windows 10 2004+ / 11).
/// </summary>
public sealed class ProcessLoopbackCapture : IDisposable
{
    private readonly int _processId;
    private readonly Action<float[], int> _onSamples;
    private readonly Thread _thread;
    private readonly ManualResetEventSlim _started = new();
    private volatile bool _stop;
    private Exception? _startError;

    public WaveFormat WaveFormat { get; }
    public int ProcessId => _processId;

    /// <summary>Raised (on the capture thread) if capture dies unexpectedly.</summary>
    public event Action<Exception>? Failed;

    /// <param name="onSamples">Receives interleaved float samples in <see cref="WaveFormat"/> and the sample count.</param>
    public ProcessLoopbackCapture(int processId, int sampleRate, Action<float[], int> onSamples)
    {
        _processId = processId;
        _onSamples = onSamples;
        WaveFormat = WaveFormat.CreateIeeeFloatWaveFormat(sampleRate, 2);
        _thread = new Thread(Run) { IsBackground = true, Name = $"ProcessLoopback {processId}" };
        _thread.SetApartmentState(ApartmentState.MTA);
    }

    /// <summary>Starts capture and blocks until the stream is running or failed to start.</summary>
    public void Start()
    {
        _thread.Start();
        _started.Wait();
        if (_startError != null) throw _startError;
    }

    private void Run()
    {
        AudioClient? client = null;
        using var bufferEvent = new EventWaitHandle(false, EventResetMode.AutoReset);
        try
        {
            client = new AudioClient(Activate(_processId));
            const long bufferDuration = 200_000; // 20 ms in 100-ns units
            client.Initialize(AudioClientShareMode.Shared,
                AudioClientStreamFlags.Loopback | AudioClientStreamFlags.EventCallback |
                AudioClientStreamFlags.AutoConvertPcm | AudioClientStreamFlags.SrcDefaultQuality,
                bufferDuration, 0, WaveFormat, Guid.Empty);
            client.SetEventHandle(bufferEvent.SafeWaitHandle.DangerousGetHandle());
            var capture = client.AudioCaptureClient;
            client.Start();
            _started.Set();

            var samples = new float[WaveFormat.SampleRate * 2];
            while (!_stop)
            {
                bufferEvent.WaitOne(100);
                int packet = capture.GetNextPacketSize();
                while (packet > 0 && !_stop)
                {
                    IntPtr data = capture.GetBuffer(out int frames, out AudioClientBufferFlags flags);
                    int count = frames * 2;
                    if (samples.Length < count) samples = new float[count];
                    if ((flags & AudioClientBufferFlags.Silent) != 0)
                        Array.Clear(samples, 0, count);
                    else
                        Marshal.Copy(data, samples, 0, count);
                    capture.ReleaseBuffer(frames);
                    _onSamples(samples, count);
                    packet = capture.GetNextPacketSize();
                }
            }
            client.Stop();
        }
        catch (Exception ex)
        {
            if (!_started.IsSet)
            {
                _startError = ex;
                _started.Set();
            }
            else if (!_stop)
            {
                Failed?.Invoke(ex);
            }
        }
        finally
        {
            client?.Dispose();
        }
    }

    public void Dispose()
    {
        _stop = true;
        if (_thread.IsAlive) _thread.Join(500);
        _started.Dispose();
    }

    #region Interop

    private const string ProcessLoopbackDevice = "VAD\\Process_Loopback";
    private static readonly Guid IAudioClientId = new("1CB9AD4C-DBFA-4c32-B178-C2F568A703B2");

    private static IAudioClient Activate(int processId)
    {
        var activationParams = new AudioClientActivationParams
        {
            ActivationType = 1, // AUDIOCLIENT_ACTIVATION_TYPE_PROCESS_LOOPBACK
            TargetProcessId = (uint)processId,
            ProcessLoopbackMode = 0, // PROCESS_LOOPBACK_MODE_INCLUDE_TARGET_PROCESS_TREE
        };
        int paramsSize = Marshal.SizeOf<AudioClientActivationParams>();
        IntPtr paramsPtr = Marshal.AllocHGlobal(paramsSize);
        IntPtr propVariantPtr = Marshal.AllocHGlobal(Marshal.SizeOf<BlobPropVariant>());
        try
        {
            Marshal.StructureToPtr(activationParams, paramsPtr, false);
            Marshal.StructureToPtr(new BlobPropVariant { vt = 65 /* VT_BLOB */, cbSize = (uint)paramsSize, pBlobData = paramsPtr },
                propVariantPtr, false);

            var handler = new CompletionHandler();
            int hr = ActivateAudioInterfaceAsync(ProcessLoopbackDevice, IAudioClientId, propVariantPtr, handler, out _);
            Marshal.ThrowExceptionForHR(hr);
            if (!handler.Done.Wait(TimeSpan.FromSeconds(5)))
                throw new TimeoutException("Process loopback activation timed out.");
            Marshal.ThrowExceptionForHR(handler.Result);
            return (IAudioClient)handler.Interface!;
        }
        finally
        {
            Marshal.FreeHGlobal(propVariantPtr);
            Marshal.FreeHGlobal(paramsPtr);
        }
    }

    [DllImport("Mmdevapi.dll", ExactSpelling = true, PreserveSig = true)]
    private static extern int ActivateAudioInterfaceAsync(
        [MarshalAs(UnmanagedType.LPWStr)] string deviceInterfacePath,
        [MarshalAs(UnmanagedType.LPStruct)] Guid riid,
        IntPtr activationParams,
        IActivateCompletionHandler completionHandler,
        out IActivateAsyncOperation activationOperation);

    [StructLayout(LayoutKind.Sequential)]
    private struct AudioClientActivationParams
    {
        public int ActivationType;
        public uint TargetProcessId;
        public int ProcessLoopbackMode;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct BlobPropVariant
    {
        public ushort vt;
        public ushort wReserved1, wReserved2, wReserved3;
        public uint cbSize;
        public IntPtr pBlobData;
    }

    [ComImport, Guid("72A22D78-CDE4-431D-B8CC-843A71199B6D"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    public interface IActivateAsyncOperation
    {
        void GetActivateResult(out int activateResult, [MarshalAs(UnmanagedType.IUnknown)] out object activatedInterface);
    }

    [ComImport, Guid("41D949AB-9862-444A-80F6-C261334DA5EB"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    public interface IActivateCompletionHandler
    {
        void ActivateCompleted(IActivateAsyncOperation activateOperation);
    }

    [ComImport, Guid("94ea2b94-e9cc-49e0-c0ff-ee64ca8f5b90"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    public interface IAgileObject { }

    public sealed class CompletionHandler : IActivateCompletionHandler, IAgileObject
    {
        public readonly ManualResetEventSlim Done = new();
        public int Result;
        public object? Interface;

        public void ActivateCompleted(IActivateAsyncOperation activateOperation)
        {
            activateOperation.GetActivateResult(out Result, out Interface);
            Done.Set();
        }
    }

    #endregion
}
