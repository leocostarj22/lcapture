using LCapture.Models;
using Windows.Graphics.Imaging;

namespace LCapture.Services;

/// <summary>What the compositor needs to draw one layer: where, how opaque, and whether it gets a frame line.</summary>
public sealed record CompositorLayer(Guid Id, RectN Rect, double Opacity, bool Border);

/// <summary>
/// Draws the layers (webcams, window, pictures) over the main picture's frames, bottom to top,
/// so the preview and the recording show exactly the same thing. Each layer keeps its latest
/// content at a working size, so it can be redrawn at any position and size without waiting
/// for its source (a still window or a picture sends nothing new). While recording, every frame
/// keeps the recording's size (<see cref="LockedCanvas"/>), letterboxing a main source whose
/// size changes.
/// </summary>
public sealed unsafe class FrameCompositor
{
    private const int MasterMax = 1920;

    private sealed class Content
    {
        public uint[]? Master;
        public int MasterWidth, MasterHeight, Version;
        public bool HasAlpha;

        // Master scaled to the size it is drawn at, rebuilt only when that size or the content changes.
        public uint[]? Display;
        public int[][]? DisplaySpans;
        public int DisplayWidth, DisplayHeight, DisplayVersion;
    }

    private readonly object _lock = new();
    private readonly Dictionary<Guid, Content> _content = [];
    private IReadOnlyList<CompositorLayer> _layers = [];
    private (int Width, int Height)? _lockedCanvas;

    /// <summary>Fixed output size while recording; null = follow the main source.</summary>
    public (int Width, int Height)? LockedCanvas
    {
        get { lock (_lock) return _lockedCanvas; }
        set { lock (_lock) _lockedCanvas = value; }
    }

    /// <summary>The layers to draw, bottom to top (UI thread; takes effect with the next frame).</summary>
    public void SetLayers(IReadOnlyList<CompositorLayer> layers)
    {
        lock (_lock) _layers = layers;
    }

    public bool HasLayers
    {
        get { lock (_lock) return _layers.Count > 0; }
    }

    /// <summary>Pixel size of a layer's content, once it has some (to keep its shape).</summary>
    public (int Width, int Height)? ContentSize(Guid id)
    {
        lock (_lock)
            return _content.TryGetValue(id, out var c) && c.Master is not null ? (c.MasterWidth, c.MasterHeight) : null;
    }

    public void ClearContent(Guid id)
    {
        lock (_lock) _content.Remove(id);
    }

    /// <summary>
    /// A new frame from a video layer's source (any thread); disposes it. With
    /// <paramref name="keyBlack"/>, black becomes transparent and dark tones see-through.
    /// </summary>
    public void SetVideoFrame(Guid id, CapturedFrame frame, KeyColor key = KeyColor.None)
    {
        try
        {
            SetContent(id, frame.Bitmap, hasAlpha: false, key);
        }
        finally
        {
            frame.Dispose();
        }
    }

    /// <summary>A picture layer's content: Bgra8 with premultiplied alpha. The caller keeps the bitmap.</summary>
    public void SetPicture(Guid id, SoftwareBitmap bitmap) => SetContent(id, bitmap, hasAlpha: true);

    private void SetContent(Guid id, SoftwareBitmap bitmap, bool hasAlpha, KeyColor key = KeyColor.None)
    {
        using var src = new BitmapLock(bitmap, BitmapBufferAccessMode.Read);
        if (src.Width <= 0 || src.Height <= 0) return;

        double scale = Math.Min(1.0, (double)MasterMax / Math.Max(src.Width, src.Height));
        int width = Math.Max(2, (int)(src.Width * scale));
        int height = Math.Max(2, (int)(src.Height * scale));

        var pixels = new uint[width * height];
        fixed (uint* dst = pixels)
            PixelOps.Scale(src.Data, src.Width, src.Height, src.Stride, (byte*)dst, width * 4, 0, 0, width, height, hasAlpha);

        if (key == KeyColor.White)
        {
            // Inverse of "over white": alpha = 255 - darkest channel, premultiplied colour = c - darkest.
            for (int i = 0; i < pixels.Length; i++)
            {
                uint p = pixels[i];
                uint b = p & 0xFF, g = (p >> 8) & 0xFF, r = (p >> 16) & 0xFF;
                uint min = Math.Min(b, Math.Min(g, r));
                pixels[i] = ((255 - min) << 24) | ((r - min) << 16) | ((g - min) << 8) | (b - min);
            }
            hasAlpha = true;
        }
        else if (key == KeyColor.Black)
        {
            // Alpha = brightest channel: the colours are then already "premultiplied", black
            // vanishes and glows keep their light — the usual way to overlay effects on black.
            for (int i = 0; i < pixels.Length; i++)
            {
                uint p = pixels[i];
                uint a = Math.Max(p & 0xFF, Math.Max((p >> 8) & 0xFF, (p >> 16) & 0xFF));
                pixels[i] = (p & 0x00FFFFFF) | (a << 24);
            }
            hasAlpha = true;
        }

        lock (_lock)
        {
            if (!_content.TryGetValue(id, out var content))
                _content[id] = content = new Content();
            content.Master = pixels;
            content.MasterWidth = width;
            content.MasterHeight = height;
            content.HasAlpha = hasAlpha;
            content.Version++;
        }
    }

    /// <summary>
    /// Returns the frame to show and record. Without layers or a size change it is the same
    /// frame, untouched (no copy); otherwise a new frame, and <paramref name="main"/> is disposed.
    /// </summary>
    public CapturedFrame Compose(CapturedFrame main)
    {
        var bitmap = main.Bitmap;
        int srcWidth = bitmap.PixelWidth, srcHeight = bitmap.PixelHeight;

        int canvasWidth, canvasHeight;
        IReadOnlyList<CompositorLayer> layers;
        lock (_lock)
        {
            (canvasWidth, canvasHeight) = _lockedCanvas ?? (srcWidth, srcHeight);
            layers = _layers;
        }

        var draws = Prepare(layers, canvasWidth, canvasHeight);
        bool resize = canvasWidth != srcWidth || canvasHeight != srcHeight;
        if (draws.Count == 0 && !resize) return main;

        if (!resize && !bitmap.IsReadOnly)
        {
            // Our own frames (e.g. window capture copies) can be drawn on directly.
            using var target = new BitmapLock(bitmap, BitmapBufferAccessMode.ReadWrite);
            foreach (var draw in draws) Draw(target, draw);
            return main;
        }

        var output = new SoftwareBitmap(BitmapPixelFormat.Bgra8, canvasWidth, canvasHeight, BitmapAlphaMode.Ignore);
        try
        {
            using (var dst = new BitmapLock(output, BitmapBufferAccessMode.Write))
            using (var src = new BitmapLock(bitmap, BitmapBufferAccessMode.Read))
            {
                if (resize)
                {
                    var (x, y, w, h) = PixelOps.Fit(srcWidth, srcHeight, canvasWidth, canvasHeight);
                    PixelOps.FillOutside(dst.Data, dst.Stride, canvasWidth, canvasHeight, x, y, w, h);
                    PixelOps.Scale(src.Data, srcWidth, srcHeight, src.Stride, dst.Data, dst.Stride, x, y, w, h);
                }
                else
                {
                    PixelOps.Copy(src.Data, src.Stride, dst.Data, dst.Stride, srcWidth, srcHeight);
                }

                foreach (var draw in draws) Draw(dst, draw);
            }
        }
        catch
        {
            output.Dispose();
            throw;
        }

        var composed = new CapturedFrame(output, owner: null, main.Timestamp);
        main.Dispose();
        return composed;
    }

    /// <param name="Spans">Pictures only: per row, start/end pairs of the non-transparent parts.</param>
    private readonly record struct DrawItem(uint[] Pixels, int[][]? Spans, int X, int Y, int Width, int Height,
                                            int Opacity, bool Blend, bool Border);

    /// <summary>
    /// Per row, the [start, end) runs of pixels that are not fully transparent. A webcam frame or a
    /// watermark is mostly empty: drawing only these runs keeps it cheap even at full size.
    /// </summary>
    private static int[][] FindSpans(uint[] pixels, int width, int height)
    {
        var spans = new int[height][];
        var runs = new List<int>();
        for (int y = 0; y < height; y++)
        {
            runs.Clear();
            int row = y * width, start = -1;
            for (int x = 0; x < width; x++)
            {
                bool visible = pixels[row + x] >> 24 != 0;
                if (visible && start < 0) start = x;
                else if (!visible && start >= 0)
                {
                    runs.Add(start);
                    runs.Add(x);
                    start = -1;
                }
            }
            if (start >= 0)
            {
                runs.Add(start);
                runs.Add(width);
            }
            spans[y] = runs.ToArray();
        }
        return spans;
    }

    /// <summary>Resolves each layer to pixels at its size on this canvas (cached between frames).</summary>
    private List<DrawItem> Prepare(IReadOnlyList<CompositorLayer> layers, int canvasWidth, int canvasHeight)
    {
        var draws = new List<DrawItem>(layers.Count);
        foreach (var layer in layers)
        {
            int x = (int)Math.Round(layer.Rect.X * canvasWidth);
            int y = (int)Math.Round(layer.Rect.Y * canvasHeight);
            int w = (int)Math.Round(layer.Rect.W * canvasWidth);
            int h = (int)Math.Round(layer.Rect.H * canvasHeight);
            if (w < 2 || h < 2 || layer.Opacity <= 0.01) continue;
            int opacity = (int)Math.Round(Math.Clamp(layer.Opacity, 0, 1) * 256);

            uint[] master;
            int masterWidth, masterHeight, version;
            bool hasAlpha;
            Content? content;
            lock (_lock)
            {
                if (!_content.TryGetValue(layer.Id, out content) || content.Master is null) continue;
                master = content.Master;
                masterWidth = content.MasterWidth;
                masterHeight = content.MasterHeight;
                version = content.Version;
                hasAlpha = content.HasAlpha;
                if (content.Display is not null && content.DisplayWidth == w && content.DisplayHeight == h &&
                    content.DisplayVersion == version)
                {
                    draws.Add(new DrawItem(content.Display, content.DisplaySpans, x, y, w, h, opacity,
                                           hasAlpha || opacity < 256, layer.Border));
                    continue;
                }
            }

            var pixels = new uint[w * h];
            fixed (uint* src = master)
            fixed (uint* dst = pixels)
                PixelOps.Scale((byte*)src, masterWidth, masterHeight, masterWidth * 4, (byte*)dst, w * 4, 0, 0, w, h, hasAlpha);
            var spans = hasAlpha ? FindSpans(pixels, w, h) : null;

            lock (_lock)
            {
                content.Display = pixels;
                content.DisplaySpans = spans;
                content.DisplayWidth = w;
                content.DisplayHeight = h;
                content.DisplayVersion = version;
            }
            draws.Add(new DrawItem(pixels, spans, x, y, w, h, opacity, hasAlpha || opacity < 256, layer.Border));
        }
        return draws;
    }

    private static void Draw(BitmapLock target, DrawItem item)
    {
        // Clip to the canvas: layers may be dragged partly outside.
        int x0 = Math.Max(0, item.X), y0 = Math.Max(0, item.Y);
        int x1 = Math.Min(target.Width, item.X + item.Width), y1 = Math.Min(target.Height, item.Y + item.Height);
        if (x1 <= x0 || y1 <= y0) return;

        int count = x1 - x0;
        fixed (uint* src = item.Pixels)
        {
            for (int row = y0; row < y1; row++)
            {
                uint* from = src + (long)(row - item.Y) * item.Width;
                uint* to = (uint*)(target.Data + (long)row * target.Stride) + item.X;
                if (item.Spans is { } spans)
                {
                    // Only the visible runs of this row, clipped to the canvas.
                    var runs = spans[row - item.Y];
                    for (int r = 0; r + 1 < runs.Length; r += 2)
                    {
                        int a = Math.Max(runs[r], x0 - item.X), b = Math.Min(runs[r + 1], x1 - item.X);
                        if (b > a) PixelOps.BlendRow(from + a, to + a, b - a, item.Opacity);
                    }
                }
                else if (item.Blend)
                    PixelOps.BlendRow(from + (x0 - item.X), to + (x0 - item.X), count, item.Opacity);
                else
                    new ReadOnlySpan<uint>(from + (x0 - item.X), count).CopyTo(new Span<uint>(to + (x0 - item.X), count));
            }
        }

        // A thin dark line separates a video layer from the picture under it.
        if (item.Border)
            DrawBorder(target, item.X, item.Y, item.Width, item.Height, 0xFF101012);
    }

    private static void DrawBorder(BitmapLock target, int x, int y, int width, int height, uint color)
    {
        const int thickness = 2;
        void Pixel(int px, int py)
        {
            if (px >= 0 && py >= 0 && px < target.Width && py < target.Height)
                ((uint*)(target.Data + (long)py * target.Stride))[px] = color;
        }

        for (int t = 1; t <= thickness; t++)
        {
            for (int px = x - t; px < x + width + t; px++)
            {
                Pixel(px, y - t);
                Pixel(px, y + height - 1 + t);
            }
            for (int py = y; py < y + height; py++)
            {
                Pixel(x - t, py);
                Pixel(x + width - 1 + t, py);
            }
        }
    }
}
