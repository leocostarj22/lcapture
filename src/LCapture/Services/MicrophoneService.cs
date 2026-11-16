using NAudio.CoreAudioApi;
using NAudio.Wave;

namespace LCapture.Services;

/// <summary>
/// Listens to a microphone for the recording and the level meter. Unlike the capture card's
/// audio it is never played through the speakers: that would cause echo and feedback.
/// </summary>
public sealed class MicrophoneService : IDisposable
{
    private WasapiCapture? _capture;
    private readonly AudioLevel _level = new();

    public string? DeviceId { get; private set; }
    public WaveFormat? Format => _capture?.WaveFormat;
    public bool IsRunning => _capture is not null;

    /// <summary>Peak of the latest packets, 0..1 (read by the meter on the UI thread).</summary>
    public float Peak => _level.TakePeak();

    /// <summary>Raised on the capture thread for every packet. The buffer is reused afterwards.</summary>
    public event AudioLoopbackService.AudioDataHandler? DataCaptured;

    public event EventHandler<string>? ErrorOccurred;

    public void Start(MMDevice device)
    {
        Stop();

        var capture = new WasapiCapture(device) { ShareMode = AudioClientShareMode.Shared };
        var format = capture.WaveFormat;
        capture.DataAvailable += (_, e) =>
        {
            _level.Measure(e.Buffer, e.BytesRecorded, format);
            DataCaptured?.Invoke(e.Buffer, e.BytesRecorded, MediaClock.Now);
        };
        capture.RecordingStopped += (_, e) =>
        {
            if (e.Exception is not null)
                ErrorOccurred?.Invoke(this, e.Exception.Message);
        };

        try
        {
            capture.StartRecording();
        }
        catch
        {
            capture.Dispose();
            throw;
        }

        _capture = capture;
        DeviceId = device.ID;
    }

    public void Stop()
    {
        if (_capture is not null)
        {
            try { _capture.StopRecording(); } catch { /* already stopped */ }
            _capture.Dispose();
            _capture = null;
        }
        DeviceId = null;
    }

    public void Dispose() => Stop();
}

/// <summary>Peak level of audio packets, for the meters. Thread-safe: measured on the capture thread.</summary>
public sealed class AudioLevel
{
    private WaveFormat? _format;
    private Func<byte[], int, float>? _read;
    private float _peak;

    public void Measure(byte[] data, int count, WaveFormat format)
    {
        if (!ReferenceEquals(format, _format))
        {
            _format = format;
            _read = MixerInput.ReadSample(format);
        }
        if (_read is null) return;

        int step = format.BlockAlign;
        float peak = 0;
        // Every frame's first channel is plenty for a meter.
        for (int i = 0; i + step <= count; i += step)
            peak = Math.Max(peak, Math.Abs(_read(data, i)));

        // Keep the highest value until the meter reads it.
        float current = Volatile.Read(ref _peak);
        if (peak > current) Volatile.Write(ref _peak, peak);
    }

    /// <summary>Highest peak since the last call, then resets.</summary>
    public float TakePeak() => Interlocked.Exchange(ref _peak, 0f);
}
