using NAudio.CoreAudioApi;
using NAudio.Wave;
using NAudio.Wave.SampleProviders;
using Windows.Devices.Enumeration;
using Windows.Media.Devices;

namespace LCapture.Services;

/// <summary>
/// Captures audio from a WASAPI input device (e.g. the audio side of an HDMI capture card)
/// and plays it back live through the default output device. Pure passthrough: nothing is
/// recorded to disk.
/// </summary>
public sealed class AudioLoopbackService : IDisposable
{
    private WasapiCapture? _capture;
    private WasapiOut? _output;
    private VolumeSampleProvider? _volumeProvider;

    public event EventHandler<string>? ErrorOccurred;

    public bool IsRunning => _capture is not null;

    public float Volume
    {
        get => _volumeProvider?.Volume ?? 1f;
        set
        {
            if (_volumeProvider is not null)
                _volumeProvider.Volume = Math.Clamp(value, 0f, 1f);
        }
    }

    // Names that indicate the audio side of a capture card. Deliberately excludes generic terms
    // like "usb", which would also match USB microphones and cause feedback.
    private static readonly string[] CaptureCardNameHints =
        ["fifine", "ampligame", "capture", "hdmi", "elgato", "avermedia", "cam link"];

    public static IReadOnlyList<MMDevice> GetInputDevices()
    {
        using var enumerator = new MMDeviceEnumerator();
        return enumerator.EnumerateAudioEndPoints(DataFlow.Capture, DeviceState.Active).ToList();
    }

    /// <summary>
    /// The audio input that belongs to the same physical device as the given video source
    /// (same ContainerId), or one whose name looks like a capture card. Returns null rather
    /// than guessing, so a microphone is never looped back to the speakers by default.
    /// </summary>
    public static async Task<MMDevice?> FindForVideoSourceAsync(
        IReadOnlyList<MMDevice> inputs, DeviceInformation? videoSource)
    {
        var containerId = videoSource is null ? null : VideoCaptureService.GetContainerId(videoSource);
        if (containerId is not null)
        {
            var audioInterfaces = await DeviceInformation.FindAllAsync(
                MediaDevice.GetAudioCaptureSelector(), [VideoCaptureService.ContainerIdProperty]);

            // WinRT ids embed the WASAPI endpoint id: \\?\SWD#MMDEVAPI#{0.0.1.00000000}.{guid}#{...}
            var match = audioInterfaces
                .Where(a => VideoCaptureService.GetContainerId(a) == containerId)
                .SelectMany(a => inputs.Where(m => a.Id.Contains(m.ID, StringComparison.OrdinalIgnoreCase)))
                .FirstOrDefault();
            if (match is not null) return match;
        }

        return inputs.FirstOrDefault(d =>
            CaptureCardNameHints.Any(h => d.FriendlyName.Contains(h, StringComparison.OrdinalIgnoreCase)));
    }

    public void Start(MMDevice device, float initialVolume)
    {
        Stop();

        var capture = new WasapiCapture(device) { ShareMode = AudioClientShareMode.Shared };

        var buffer = new BufferedWaveProvider(capture.WaveFormat)
        {
            DiscardOnBufferOverflow = true,
            BufferDuration = TimeSpan.FromMilliseconds(500),
        };

        capture.DataAvailable += (_, e) => buffer.AddSamples(e.Buffer, 0, e.BytesRecorded);
        capture.RecordingStopped += (_, e) =>
        {
            if (e.Exception is not null)
                ErrorOccurred?.Invoke(this, e.Exception.Message);
        };

        var volumeProvider = new VolumeSampleProvider(buffer.ToSampleProvider())
        {
            Volume = Math.Clamp(initialVolume, 0f, 1f),
        };

        // Shared-mode WASAPI render: the Windows audio engine resamples/converts as needed,
        // so the capture device's native format does not need to match the output device's.
        var output = new WasapiOut(AudioClientShareMode.Shared, 100);

        try
        {
            output.Init(volumeProvider);
            capture.StartRecording();
            output.Play();
        }
        catch
        {
            output.Dispose();
            capture.Dispose();
            throw;
        }

        _capture = capture;
        _volumeProvider = volumeProvider;
        _output = output;
    }

    public void Stop()
    {
        if (_output is not null)
        {
            try { _output.Stop(); } catch { /* already stopped */ }
            _output.Dispose();
            _output = null;
        }

        if (_capture is not null)
        {
            try { _capture.StopRecording(); } catch { /* already stopped */ }
            _capture.Dispose();
            _capture = null;
        }

        _volumeProvider = null;
    }

    public void Dispose() => Stop();
}
