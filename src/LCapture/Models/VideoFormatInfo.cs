namespace LCapture.Models;

/// <summary>Size and frame rate of a video stream, whatever produces it (capture device or window).</summary>
public sealed record VideoFormatInfo(uint Width, uint Height, uint FpsNumerator, uint FpsDenominator)
{
    public double Fps => FpsDenominator == 0 ? 0 : (double)FpsNumerator / FpsDenominator;

    public static VideoFormatInfo From(CaptureFormatOption format) =>
        new(format.Width, format.Height, format.FrameRateNumerator, Math.Max(format.FrameRateDenominator, 1));
}
