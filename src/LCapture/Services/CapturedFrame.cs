using Windows.Graphics.Imaging;

namespace LCapture.Services;

/// <summary>
/// A Bgra8 frame ready for the preview. Dispose it once drawn: it may be holding the capture
/// device's own buffer (<paramref name="owner"/>), which goes back to the device on dispose.
/// </summary>
public sealed class CapturedFrame(SoftwareBitmap bitmap, IDisposable? owner) : IDisposable
{
    public SoftwareBitmap Bitmap { get; } = bitmap;

    public void Dispose()
    {
        Bitmap.Dispose();
        owner?.Dispose();
    }
}
