namespace LCapture.Models;

public enum RecordingQuality { High, Medium, Low }

/// <summary>What the user picked for recordings, independent of the current source.</summary>
/// <param name="TargetHeight">Output height in pixels; 0 keeps the source resolution.</param>
/// <param name="Fps30">Halve 60 fps sources (and cap anything faster) to 30 fps.</param>
public sealed record RecordingChoice(int TargetHeight, bool Fps30, RecordingQuality Quality);

/// <summary>A recording choice resolved against a concrete source format.</summary>
public sealed record RecordingProfile(
    uint Width, uint Height, uint FpsNumerator, uint FpsDenominator, uint VideoBitrate)
{
    public const uint AudioBitrate = 192_000;

    /// <summary>Heights offered in the settings, besides "same as the source".</summary>
    public static readonly int[] PresetHeights = [2160, 1440, 1080, 720];

    // Bits per pixel per frame for H.264. 1080p60: High ≈ 12 Mbps, Medium ≈ 8, Low ≈ 5.
    private static double BitsPerPixel(RecordingQuality quality) => quality switch
    {
        RecordingQuality.High => 0.10,
        RecordingQuality.Medium => 0.065,
        _ => 0.04,
    };

    public double Fps => (double)FpsNumerator / FpsDenominator;

    /// <summary>Approximate file size per hour of recording, video and audio together.</summary>
    public double GigabytesPerHour => (VideoBitrate + AudioBitrate) / 8.0 * 3600 / 1e9;

    public static RecordingProfile Resolve(VideoFormatInfo source, RecordingChoice choice)
    {
        var (width, height) = OutputSize(source.Width, source.Height, choice.TargetHeight);

        uint fpsNum = source.FpsNumerator, fpsDen = Math.Max(source.FpsDenominator, 1);
        if (choice.Fps30 && source.Fps > 31)
            (fpsNum, fpsDen) = (30, 1);

        double fps = (double)fpsNum / fpsDen;
        // Higher frame rates need fewer bits per frame: consecutive frames are more alike.
        double motion = Math.Clamp(Math.Sqrt(60.0 / Math.Max(fps, 1)), 1, 1.5);
        double bitrate = width * (double)height * fps * BitsPerPixel(choice.Quality) * motion;
        uint clamped = (uint)Math.Clamp(bitrate, 1_000_000, 80_000_000);

        return new RecordingProfile(width, height, fpsNum, fpsDen, clamped);
    }

    /// <summary>Scales down to the target height, keeping the aspect ratio; never scales up.</summary>
    public static (uint Width, uint Height) OutputSize(uint sourceWidth, uint sourceHeight, int targetHeight)
    {
        if (targetHeight <= 0 || targetHeight >= sourceHeight || sourceHeight == 0)
            return (Even(sourceWidth), Even(sourceHeight));

        uint height = (uint)targetHeight;
        uint width = (uint)Math.Round(sourceWidth * (double)height / sourceHeight);
        return (Even(width), Even(height));
    }

    // H.264 encoders need even dimensions (4:2:0 chroma).
    private static uint Even(uint value) => value & ~1u;
}
