using LCapture.Models;
using Windows.Devices.Enumeration;
using Windows.Graphics.Imaging;
using Windows.Media.Capture;
using Windows.Media.Capture.Frames;
using Windows.Media.Devices;

namespace LCapture.Services;

/// <summary>
/// Wraps Windows.Media.Capture to open a UVC video source (e.g. a capture card such as
/// the Fifine AmpliGame V3), enumerate its native formats and stream frames as Bgra8
/// SoftwareBitmaps. View-only: nothing is ever recorded or encoded to disk.
/// </summary>
public sealed class VideoCaptureService : IAsyncDisposable
{
    private MediaCapture? _mediaCapture;
    private MediaFrameSource? _colorSource;
    private MediaFrameReader? _frameReader;

    /// <summary>Raised on a background thread. The caller owns and must Dispose the bitmap.</summary>
    public event EventHandler<SoftwareBitmap>? FrameArrived;

    public event EventHandler<string>? ErrorOccurred;

    public bool IsOpen => _mediaCapture is not null;
    public bool IsStreaming => _frameReader is not null;

    /// <summary>Identifies the physical device; shared by the video and audio sides of a capture card.</summary>
    public const string ContainerIdProperty = "System.Devices.ContainerId";

    private static readonly string[] CaptureCardNameHints = ["fifine", "ampligame"];

    public static async Task<IReadOnlyList<DeviceInformation>> GetDevicesAsync()
    {
        var devices = await DeviceInformation.FindAllAsync(
            MediaDevice.GetVideoCaptureSelector(), [ContainerIdProperty]);
        return devices.ToList();
    }

    public static Guid? GetContainerId(DeviceInformation device) =>
        device.Properties.TryGetValue(ContainerIdProperty, out var value) && value is Guid id ? id : null;

    /// <summary>
    /// Best automatic choice when the user has not picked a source yet: a known capture card,
    /// then any external device, and only then built-in webcams or virtual cameras.
    /// </summary>
    public static DeviceInformation PickDefault(IReadOnlyList<DeviceInformation> devices) =>
        devices.OrderBy(Priority).First();

    private static int Priority(DeviceInformation device)
    {
        if (CaptureCardNameHints.Any(h => device.Name.Contains(h, StringComparison.OrdinalIgnoreCase)))
            return 0;

        // Laptop webcams report the panel they are mounted on; USB capture cards do not.
        bool builtIn = device.EnclosureLocation is { Panel: not Panel.Unknown };
        bool isVirtual = device.Name.Contains("virtual", StringComparison.OrdinalIgnoreCase);
        return builtIn || isVirtual ? 2 : 1;
    }

    /// <summary>Opens the device (exclusive control) and returns its supported formats, best quality first.</summary>
    public async Task<IReadOnlyList<CaptureFormatOption>> OpenDeviceAsync(string deviceId)
    {
        await CloseAsync();

        var groups = await MediaFrameSourceGroup.FindAllAsync();
        var group = groups.FirstOrDefault(g =>
            g.SourceInfos.Any(si => si.DeviceInformation?.Id == deviceId));

        if (group is null)
            throw new InvalidOperationException("Não foi possível encontrar um grupo de captura para esta fonte de vídeo.");

        var mediaCapture = new MediaCapture();
        try
        {
            await mediaCapture.InitializeAsync(new MediaCaptureInitializationSettings
            {
                SourceGroup = group,
                SharingMode = MediaCaptureSharingMode.ExclusiveControl,
                MemoryPreference = MediaCaptureMemoryPreference.Cpu,
                StreamingCaptureMode = StreamingCaptureMode.Video,
            });
        }
        catch
        {
            mediaCapture.Dispose();
            throw;
        }

        var colorSource = mediaCapture.FrameSources.Values
            .FirstOrDefault(fs => fs.Info.SourceKind == MediaFrameSourceKind.Color);

        if (colorSource is null)
        {
            mediaCapture.Dispose();
            throw new InvalidOperationException("Esta fonte não expõe um stream de vídeo colorido.");
        }

        _mediaCapture = mediaCapture;
        _colorSource = colorSource;
        _mediaCapture.Failed += OnMediaCaptureFailed;

        var options = colorSource.SupportedFormats
            .Select(f => new CaptureFormatOption
            {
                Width = f.VideoFormat.Width,
                Height = f.VideoFormat.Height,
                FrameRateNumerator = f.FrameRate.Numerator,
                FrameRateDenominator = f.FrameRate.Denominator,
                Subtype = f.Subtype,
                RawFormat = f,
            })
            // Best quality first: highest resolution, then highest frame rate,
            // preferring an uncompressed subtype over MJPG at an equal res/fps tie.
            .OrderByDescending(o => (long)o.Width * o.Height)
            .ThenByDescending(o => o.Fps)
            .ThenBy(o => string.Equals(o.Subtype, "MJPG", StringComparison.OrdinalIgnoreCase) ? 1 : 0)
            .ToList();

        return options;
    }

    /// <summary>Applies the given format and (re)starts the frame reader. Safe to call while already streaming.</summary>
    public async Task StartAsync(CaptureFormatOption format)
    {
        if (_mediaCapture is null || _colorSource is null)
            throw new InvalidOperationException("Nenhuma fonte de vídeo aberta.");

        await StopReaderAsync();

        await _colorSource.SetFormatAsync(format.RawFormat);

        var reader = await _mediaCapture.CreateFrameReaderAsync(_colorSource, format.Subtype);
        reader.AcquisitionMode = MediaFrameReaderAcquisitionMode.Realtime;
        reader.FrameArrived += OnReaderFrameArrived;

        var status = await reader.StartAsync();
        if (status != MediaFrameReaderStartStatus.Success)
        {
            reader.FrameArrived -= OnReaderFrameArrived;
            reader.Dispose();
            throw new InvalidOperationException($"Falha ao iniciar o stream de vídeo ({status}).");
        }

        _frameReader = reader;
    }

    public async Task StopReaderAsync()
    {
        if (_frameReader is null) return;

        var reader = _frameReader;
        _frameReader = null;
        reader.FrameArrived -= OnReaderFrameArrived;
        await reader.StopAsync();
        reader.Dispose();
    }

    public async Task CloseAsync()
    {
        await StopReaderAsync();

        if (_mediaCapture is not null)
        {
            _mediaCapture.Failed -= OnMediaCaptureFailed;
            _mediaCapture.Dispose();
            _mediaCapture = null;
        }

        _colorSource = null;
    }

    private void OnReaderFrameArrived(MediaFrameReader sender, MediaFrameArrivedEventArgs args)
    {
        using var frame = sender.TryAcquireLatestFrame();
        if (frame?.VideoMediaFrame?.SoftwareBitmap is not SoftwareBitmap original)
            return;

        SoftwareBitmap converted;
        if (original.BitmapPixelFormat == BitmapPixelFormat.Bgra8 &&
            original.BitmapAlphaMode == BitmapAlphaMode.Premultiplied)
        {
            converted = SoftwareBitmap.Copy(original);
        }
        else
        {
            converted = SoftwareBitmap.Convert(original, BitmapPixelFormat.Bgra8, BitmapAlphaMode.Premultiplied);
        }

        FrameArrived?.Invoke(this, converted);
    }

    private void OnMediaCaptureFailed(MediaCapture sender, MediaCaptureFailedEventArgs errorEventArgs)
    {
        ErrorOccurred?.Invoke(this, errorEventArgs.Message);
    }

    public async ValueTask DisposeAsync()
    {
        await CloseAsync();
    }
}
