using System.Runtime.InteropServices;
using NAudio.Wave;
using static LCapture.Interop.ProcessLoopbackInterop;

namespace LCapture.Services;

/// <summary>
/// Captures the sound of one application (e.g. the browser shown in a window layer) for the
/// recording, or of everything except LCapture itself (for a whole-screen layer). Only the
/// app's own sound is captured, so the capture card's audio that LCapture plays is never doubled.
/// The app keeps playing through the speakers by itself; this only listens.
/// </summary>
public sealed class ProcessAudioCapture : IDisposable
{
    /// <summary>Process loopback exists from Windows 10 build 20348 (in practice, Windows 11).</summary>
    public static bool IsSupported => Environment.OSVersion.Version.Build >= 20348;

    public static readonly WaveFormat CaptureFormat = WaveFormat.CreateIeeeFloatWaveFormat(48000, 2);

    private IAudioClient? _client;
    private IAudioCaptureClient? _capture;
    private Thread? _thread;
    private AutoResetEvent? _event;
    private volatile bool _running;

    /// <summary>Raised on the capture thread with float 48 kHz stereo packets. The buffer is reused.</summary>
    public event AudioLoopbackService.AudioDataHandler? DataCaptured;

    public event EventHandler<string>? ErrorOccurred;

    /// <param name="processId">The app to listen to, or (exclude = true) the one to leave out.</param>
    public Task StartAsync(int processId, bool exclude)
    {
        Stop();

        // Activation, Initialize and Start must all run on the same explicitly-MTA thread: WASAPI's
        // process-loopback client is apartment-sensitive, and resuming on the WPF (STA) UI thread
        // after an `await` here (as a plain async method would) made the cast to IAudioClient fail
        // with E_NOINTERFACE even though activation itself had already succeeded.
        var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var setup = new Thread(() => Setup(processId, exclude, done)) { IsBackground = true, Name = "LCapture app audio setup" };
        setup.SetApartmentState(ApartmentState.MTA);
        setup.Start();
        return done.Task;
    }

    private void Setup(int processId, bool exclude, TaskCompletionSource done)
    {
        var activation = new AudioClientActivationParams
        {
            ActivationType = 1,
            TargetProcessId = (uint)processId,
            ProcessLoopbackMode = exclude ? ProcessLoopbackMode.ExcludeTargetProcessTree
                                          : ProcessLoopbackMode.IncludeTargetProcessTree,
        };

        IntPtr paramsPtr = Marshal.AllocHGlobal(Marshal.SizeOf<AudioClientActivationParams>());
        IntPtr variantPtr = Marshal.AllocHGlobal(Marshal.SizeOf<BlobPropVariant>());
        try
        {
            Marshal.StructureToPtr(activation, paramsPtr, false);
            Marshal.StructureToPtr(new BlobPropVariant
            {
                Vt = 65, // VT_BLOB
                BlobSize = (uint)Marshal.SizeOf<AudioClientActivationParams>(),
                BlobData = paramsPtr,
            }, variantPtr, false);

            var handler = new CompletionHandler();
            ActivateAudioInterfaceAsync(VirtualDeviceProcessLoopback, IidAudioClient, variantPtr, handler, out _);
            var client = handler.Task.GetAwaiter().GetResult(); // Blocks this thread; the OS calls back on its own.

            var format = new WaveFormatEx
            {
                FormatTag = 3, // WAVE_FORMAT_IEEE_FLOAT
                Channels = (ushort)CaptureFormat.Channels,
                SamplesPerSec = (uint)CaptureFormat.SampleRate,
                BitsPerSample = 32,
                BlockAlign = (ushort)CaptureFormat.BlockAlign,
                AvgBytesPerSec = (uint)CaptureFormat.AverageBytesPerSecond,
            };

            const long bufferDuration = 2_000_000; // 200 ms, in 100-ns units
            Check(client.Initialize(0, AudclntStreamflagsLoopback | AudclntStreamflagsEventCallback |
                                       AudclntStreamflagsAutoConvertPcm | AudclntStreamflagsSrcDefaultQuality,
                                    bufferDuration, 0, ref format, IntPtr.Zero));

            var signal = new AutoResetEvent(false);
            Check(client.SetEventHandle(signal.SafeWaitHandle.DangerousGetHandle()));
            Check(client.GetService(IidAudioCaptureClient, out object service));
            var capture = (IAudioCaptureClient)service;
            Check(client.Start());

            _client = client;
            _capture = capture;
            _event = signal;
            _running = true;
            _thread = new Thread(Run) { IsBackground = true, Name = "LCapture app audio" };
            _thread.Start();
            done.TrySetResult();
        }
        catch (Exception ex)
        {
            done.TrySetException(ex);
        }
        finally
        {
            Marshal.FreeHGlobal(variantPtr);
            Marshal.FreeHGlobal(paramsPtr);
        }
    }

    private void Run()
    {
        var buffer = new byte[CaptureFormat.AverageBytesPerSecond / 2];
        int blockAlign = CaptureFormat.BlockAlign;

        try
        {
            while (_running)
            {
                _event!.WaitOne(100);
                while (_running && _capture!.GetNextPacketSize(out uint next) >= 0 && next > 0)
                {
                    Check(_capture.GetBuffer(out IntPtr data, out uint frames, out uint flags, out _, out _));
                    int bytes = (int)frames * blockAlign;
                    if (buffer.Length < bytes) buffer = new byte[bytes];

                    if ((flags & AudclntBufferflagsSilent) != 0) Array.Clear(buffer, 0, bytes);
                    else Marshal.Copy(data, buffer, 0, bytes);
                    _capture.ReleaseBuffer(frames);

                    DataCaptured?.Invoke(buffer, bytes, MediaClock.Now);
                }
            }
        }
        catch (Exception ex)
        {
            if (_running) ErrorOccurred?.Invoke(this, ex.Message);
        }
    }

    public void Stop()
    {
        _running = false;
        _thread?.Join(500);
        _thread = null;

        if (_client is not null)
        {
            try { _client.Stop(); } catch { /* already stopped */ }
            Marshal.ReleaseComObject(_client);
            _client = null;
        }

        if (_capture is not null)
        {
            Marshal.ReleaseComObject(_capture);
            _capture = null;
        }

        _event?.Dispose();
        _event = null;
    }

    public void Dispose() => Stop();

    private static void Check(int hr)
    {
        if (hr < 0) Marshal.ThrowExceptionForHR(hr);
    }
}
