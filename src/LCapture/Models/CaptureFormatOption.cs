using Windows.Media.Capture.Frames;

namespace LCapture.Models;

public sealed class CaptureFormatOption
{
    public required uint Width { get; init; }
    public required uint Height { get; init; }
    public required uint FrameRateNumerator { get; init; }
    public required uint FrameRateDenominator { get; init; }
    public required string Subtype { get; init; }
    public required MediaFrameFormat RawFormat { get; init; }

    public double Fps => FrameRateDenominator == 0 ? 0 : (double)FrameRateNumerator / FrameRateDenominator;

    public bool IsCompressed =>
        Subtype.Equals("MJPG", StringComparison.OrdinalIgnoreCase) ||
        Subtype.Equals("H264", StringComparison.OrdinalIgnoreCase) ||
        Subtype.Equals("HEVC", StringComparison.OrdinalIgnoreCase);

    /// <summary>Friendly tier name for well-known resolutions (empty for unusual ones).</summary>
    public string Tier => Height switch
    {
        >= 2160 => "4K",
        >= 1440 => "2K",
        1080 or 1200 => "Full HD",
        720 => "HD",
        _ => string.Empty,
    };

    public bool Matches(uint width, uint height, uint fpsNum, uint fpsDen, string subtype) =>
        Width == width && Height == height && FrameRateNumerator == fpsNum &&
        FrameRateDenominator == fpsDen && string.Equals(Subtype, subtype, StringComparison.OrdinalIgnoreCase);

    /// <summary>Same resolution and (rounded) frame rate, regardless of pixel format.</summary>
    public bool SameProfile(uint width, uint height, double fps) =>
        Width == width && Height == height && Math.Round(Fps) == Math.Round(fps);

    public override string ToString()
    {
        var resolution = $"{Width}×{Height}";
        var head = Tier.Length > 0 ? $"{Tier} · {resolution}" : resolution;
        var text = $"{head} · {Fps:0.##} fps";
        return IsCompressed ? $"{text} · {Subtype.ToUpperInvariant()}" : text;
    }
}
