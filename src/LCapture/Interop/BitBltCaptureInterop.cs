using System.Runtime.InteropServices;

namespace LCapture.Interop;

/// <summary>
/// GDI window/screen capture (PrintWindow/BitBlt), the same "compatibility" method OBS falls
/// back to. It reads the desktop's already-composed image, so it sees hardware video overlays
/// (e.g. a browser's accelerated &lt;video&gt;) that Windows.Graphics.Capture can miss — at the
/// cost of including the window's own title bar and being slower on large windows.
/// </summary>
internal static unsafe class BitBltCaptureInterop
{
    [DllImport("user32.dll")] private static extern bool GetWindowRect(IntPtr hwnd, out Rect rect);
    [DllImport("user32.dll")] private static extern IntPtr GetDC(IntPtr hwnd);
    [DllImport("user32.dll")] private static extern int ReleaseDC(IntPtr hwnd, IntPtr hdc);
    [DllImport("user32.dll")] private static extern bool PrintWindow(IntPtr hwnd, IntPtr hdcBlt, uint flags);
    [DllImport("user32.dll")] private static extern bool GetMonitorInfo(IntPtr monitor, ref MonitorInfo info);

    [DllImport("gdi32.dll")] private static extern IntPtr CreateCompatibleDC(IntPtr hdc);
    [DllImport("gdi32.dll")] private static extern IntPtr CreateDIBSection(
        IntPtr hdc, in BitmapInfoHeader bmi, uint usage, out IntPtr bits, IntPtr section, uint offset);
    [DllImport("gdi32.dll")] private static extern IntPtr SelectObject(IntPtr hdc, IntPtr obj);
    [DllImport("gdi32.dll")] private static extern bool DeleteObject(IntPtr obj);
    [DllImport("gdi32.dll")] private static extern bool DeleteDC(IntPtr hdc);
    [DllImport("gdi32.dll")] private static extern bool BitBlt(
        IntPtr hdcDest, int xDest, int yDest, int width, int height, IntPtr hdcSrc, int xSrc, int ySrc, uint rop);

    [StructLayout(LayoutKind.Sequential)]
    private struct Rect { public int Left, Top, Right, Bottom; }

    [StructLayout(LayoutKind.Sequential)]
    private struct MonitorInfo
    {
        public int Size;
        public Rect Monitor;
        public Rect WorkArea;
        public uint Flags;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct BitmapInfoHeader
    {
        public uint Size;
        public int Width, Height;
        public short Planes, BitCount;
        public uint Compression, SizeImage;
        public int XPelsPerMeter, YPelsPerMeter;
        public uint ClrUsed, ClrImportant;
    }

    private const uint PwRenderFullContent = 2;
    private const uint SrcCopy = 0x00CC0020;

    private static bool GetMonitorRect(IntPtr monitor, out int left, out int top, out int width, out int height)
    {
        var info = new MonitorInfo { Size = Marshal.SizeOf<MonitorInfo>() };
        if (!GetMonitorInfo(monitor, ref info)) { left = top = width = height = 0; return false; }
        left = info.Monitor.Left;
        top = info.Monitor.Top;
        width = info.Monitor.Right - info.Monitor.Left;
        height = info.Monitor.Bottom - info.Monitor.Top;
        return true;
    }

    private static bool GetWindowSize(IntPtr hwnd, out int width, out int height)
    {
        if (!GetWindowRect(hwnd, out var rect)) { width = height = 0; return false; }
        width = rect.Right - rect.Left;
        height = rect.Bottom - rect.Top;
        return width > 0 && height > 0;
    }

    /// <summary>
    /// A reusable top-down 32bpp DIB section that a window or a monitor is captured into. Recreated
    /// only when the size changes, like the WGC frame pool is.
    /// </summary>
    public sealed class Frame : IDisposable
    {
        private IntPtr _dc, _bitmap, _oldBitmap;

        public IntPtr Bits { get; private set; }
        public int Width { get; private set; }
        public int Height { get; private set; }
        public int Stride => Width * 4;

        private void EnsureSize(int width, int height)
        {
            if (width == Width && height == Height && _dc != IntPtr.Zero) return;
            Release();

            var screenDc = GetDC(IntPtr.Zero);
            try
            {
                _dc = CreateCompatibleDC(screenDc);
                var bmi = new BitmapInfoHeader
                {
                    Size = (uint)Marshal.SizeOf<BitmapInfoHeader>(),
                    Width = width,
                    Height = -height, // negative = top-down, so row 0 is the top row
                    Planes = 1,
                    BitCount = 32,
                };
                _bitmap = CreateDIBSection(_dc, in bmi, 0, out var bits, IntPtr.Zero, 0);
                if (_bitmap == IntPtr.Zero) throw new InvalidOperationException("CreateDIBSection falhou");
                Bits = bits;
                _oldBitmap = SelectObject(_dc, _bitmap);
                Width = width;
                Height = height;
            }
            finally
            {
                ReleaseDC(IntPtr.Zero, screenDc);
            }
        }

        /// <summary>Captures a window (PrintWindow, so it includes hardware video overlays) or a
        /// monitor (BitBlt from the screen) into this frame. False on failure (e.g. the window
        /// just closed); the caller should skip that tick, not treat it as fatal.</summary>
        public bool Capture(CaptureTarget target)
        {
            if (target.IsMonitor)
            {
                if (!GetMonitorRect(target.Monitor, out int left, out int top, out int width, out int height) ||
                    width <= 0 || height <= 0) return false;
                EnsureSize(width, height);

                var screenDc = GetDC(IntPtr.Zero);
                try
                {
                    return BitBlt(_dc, 0, 0, width, height, screenDc, left, top, SrcCopy);
                }
                finally
                {
                    ReleaseDC(IntPtr.Zero, screenDc);
                }
            }

            if (!GetWindowSize(target.Window, out int w, out int h)) return false;
            EnsureSize(w, h);
            return PrintWindow(target.Window, _dc, PwRenderFullContent);
        }

        private void Release()
        {
            if (_dc != IntPtr.Zero)
            {
                if (_oldBitmap != IntPtr.Zero) SelectObject(_dc, _oldBitmap);
                DeleteDC(_dc);
            }
            if (_bitmap != IntPtr.Zero) DeleteObject(_bitmap);
            _dc = _bitmap = _oldBitmap = IntPtr.Zero;
            Bits = IntPtr.Zero;
            Width = Height = 0;
        }

        public void Dispose() => Release();
    }
}
