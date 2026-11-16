using LCapture.Interop;
using Windows.Graphics.Imaging;
using WinRT;

namespace LCapture.Services;

/// <summary>Raw access to a SoftwareBitmap's Bgra8 pixels while it is locked.</summary>
internal sealed unsafe class BitmapLock : IDisposable
{
    private readonly BitmapBuffer _buffer;
    private readonly Windows.Foundation.IMemoryBufferReference _reference;

    public byte* Data { get; }
    public int Stride { get; }
    public int Width { get; }
    public int Height { get; }

    public BitmapLock(SoftwareBitmap bitmap, BitmapBufferAccessMode mode)
    {
        _buffer = bitmap.LockBuffer(mode);
        _reference = _buffer.CreateReference();
        _reference.As<IMemoryBufferByteAccess>().GetBuffer(out byte* data, out _);
        var plane = _buffer.GetPlaneDescription(0);
        Data = data + plane.StartIndex;
        Stride = plane.Stride;
        Width = bitmap.PixelWidth;
        Height = bitmap.PixelHeight;
    }

    public void Dispose()
    {
        _reference.Dispose();
        _buffer.Dispose();
    }
}

/// <summary>
/// CPU pixel routines for 32-bit BGRA images. Video is opaque (alpha ignored, written as 255);
/// pictures (logos, frames) keep their alpha, premultiplied.
/// </summary>
internal static unsafe class PixelOps
{
    public static void Copy(byte* src, int srcStride, byte* dst, int dstStride, int width, int height)
    {
        long rowBytes = (long)width * 4;
        if (srcStride == dstStride && srcStride == rowBytes)
        {
            Buffer.MemoryCopy(src, dst, rowBytes * height, rowBytes * height);
            return;
        }

        for (int y = 0; y < height; y++)
            Buffer.MemoryCopy(src + (long)y * srcStride, dst + (long)y * dstStride, rowBytes, rowBytes);
    }

    /// <summary>Largest rectangle with the source's aspect ratio that fits the target, centred.</summary>
    public static (int X, int Y, int Width, int Height) Fit(int srcWidth, int srcHeight, int dstWidth, int dstHeight)
    {
        double scale = Math.Min((double)dstWidth / srcWidth, (double)dstHeight / srcHeight);
        int w = Math.Clamp((int)Math.Round(srcWidth * scale), 1, dstWidth);
        int h = Math.Clamp((int)Math.Round(srcHeight * scale), 1, dstHeight);
        return ((dstWidth - w) / 2, (dstHeight - h) / 2, w, h);
    }

    /// <summary>
    /// Bilinear scale of the whole source into the rectangle (dx, dy, dw, dh) of the destination.
    /// Rows are split across cores when the output is large (a full 1080p frame).
    /// </summary>
    public static void Scale(byte* src, int srcWidth, int srcHeight, int srcStride,
                             byte* dst, int dstStride, int dx, int dy, int dw, int dh, bool keepAlpha = false)
    {
        if (dw <= 0 || dh <= 0 || srcWidth <= 0 || srcHeight <= 0) return;

        // Horizontal sample positions and 8-bit weights, shared by every row.
        var x0 = new int[dw];
        var wx = new int[dw];
        for (int x = 0; x < dw; x++)
        {
            double sx = Math.Clamp((x + 0.5) * srcWidth / dw - 0.5, 0, srcWidth - 1);
            x0[x] = (int)sx;
            wx[x] = (int)((sx - x0[x]) * 256);
        }

        nint srcBase = (nint)src, dstBase = (nint)dst;
        void Row(int y)
        {
            double sy = Math.Clamp((y + 0.5) * srcHeight / dh - 0.5, 0, srcHeight - 1);
            int y0 = (int)sy;
            int y1 = Math.Min(y0 + 1, srcHeight - 1);
            int fy = (int)((sy - y0) * 256);

            uint* top = (uint*)((byte*)srcBase + (long)y0 * srcStride);
            uint* bottom = (uint*)((byte*)srcBase + (long)y1 * srcStride);
            uint* outRow = (uint*)((byte*)dstBase + (long)(dy + y) * dstStride) + dx;
            int last = srcWidth - 1;

            for (int x = 0; x < dw; x++)
            {
                int ix = x0[x];
                int ix1 = ix < last ? ix + 1 : last;
                int fx = wx[x];
                outRow[x] = keepAlpha
                    ? BlendArgb(top[ix], top[ix1], bottom[ix], bottom[ix1], fx, fy)
                    : Blend(top[ix], top[ix1], bottom[ix], bottom[ix1], fx, fy);
            }
        }

        if ((long)dw * dh >= 400_000)
            Parallel.For(0, dh, Row);
        else
            for (int y = 0; y < dh; y++) Row(y);
    }

    // Bilinear blend with two colour channels per 32-bit operation (R and B, then G).
    // Weights are 0..256, so each 8-bit lane times 256 still fits in its 16 bits.
    private static uint Blend(uint p00, uint p01, uint p10, uint p11, int fx, int fy)
    {
        uint top = Lerp(p00, p01, (uint)fx);
        uint bottom = Lerp(p10, p11, (uint)fx);
        return Lerp(top, bottom, (uint)fy) | 0xFF000000;
    }

    private static uint BlendArgb(uint p00, uint p01, uint p10, uint p11, int fx, int fy)
    {
        uint top = LerpArgb(p00, p01, (uint)fx);
        uint bottom = LerpArgb(p10, p11, (uint)fx);
        return LerpArgb(top, bottom, (uint)fy);
    }

    // All four channels: R and B in one lane pair, A and G in the other.
    private static uint LerpArgb(uint a, uint b, uint f)
    {
        uint inv = 256 - f;
        uint rb = (((a & 0x00FF00FF) * inv + (b & 0x00FF00FF) * f) >> 8) & 0x00FF00FF;
        uint ag = (((a >> 8) & 0x00FF00FF) * inv + ((b >> 8) & 0x00FF00FF) * f) & 0xFF00FF00;
        return rb | ag;
    }

    /// <summary>
    /// Draws premultiplied BGRA pixels over an opaque row ("source over"), at the given opacity
    /// (0..256). Fully transparent pixels are skipped, so a large frame with an empty middle is cheap.
    /// </summary>
    public static void BlendRow(uint* src, uint* dst, int count, int opacity)
    {
        for (int i = 0; i < count; i++)
        {
            uint s = src[i];
            if (opacity < 256)
            {
                uint rb = (((s & 0x00FF00FF) * (uint)opacity) >> 8) & 0x00FF00FF;
                uint ag = (((s >> 8) & 0x00FF00FF) * (uint)opacity) & 0xFF00FF00;
                s = rb | ag;
            }

            uint a = s >> 24;
            if (a == 0) continue;
            if (a == 255)
            {
                dst[i] = s;
                continue;
            }

            uint inv = 256 - a - (a >> 7); // 255 - a, on a 0..256 scale
            uint d = dst[i];
            uint drb = (((d & 0x00FF00FF) * inv) >> 8) & 0x00FF00FF;
            uint dg = (((d & 0x0000FF00) * inv) >> 8) & 0x0000FF00;
            dst[i] = (s + drb + dg) | 0xFF000000;
        }
    }

    private static uint Lerp(uint a, uint b, uint f)
    {
        uint inv = 256 - f;
        uint rb = (((a & 0x00FF00FF) * inv + (b & 0x00FF00FF) * f) >> 8) & 0x00FF00FF;
        uint g = (((a & 0x0000FF00) * inv + (b & 0x0000FF00) * f) >> 8) & 0x0000FF00;
        return rb | g;
    }

    /// <summary>Paints black everything outside the rectangle (the letterbox bars).</summary>
    public static void FillOutside(byte* dst, int dstStride, int width, int height, int x, int y, int w, int h)
    {
        for (int row = 0; row < height; row++)
        {
            var line = new Span<uint>(dst + (long)row * dstStride, width);
            if (row < y || row >= y + h)
            {
                line.Fill(0xFF000000);
                continue;
            }
            line[..x].Fill(0xFF000000);
            line[(x + w)..].Fill(0xFF000000);
        }
    }
}
