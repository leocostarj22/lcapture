using System.Diagnostics;
using LCapture.Interop;
using LCapture.Models;
using Windows.Foundation.Metadata;
using Windows.Graphics;
using Windows.Graphics.Capture;
using Windows.Graphics.DirectX;
using Windows.Graphics.DirectX.Direct3D11;
using Windows.Graphics.Imaging;

namespace LCapture.Services;

/// <summary>
/// Captures a window (e.g. the browser) or a whole monitor, and delivers frames like the capture
/// card does. Two methods, chosen per <see cref="CaptureMethod"/>:
/// - WGC (Windows.Graphics.Capture, the API OBS uses by default): borderless, only sends a frame
///   when the content changes.
/// - BitBlt (PrintWindow/BitBlt, OBS's "compatibility" method): polls at a fixed rate and reads
///   the desktop's already-composed image, at the cost of the window's own title bar and more CPU
///   use.
/// Neither method, capturing just a window, shows a browser's hardware-accelerated &lt;video&gt;
/// (confirmed on real hardware): it is drawn through a compositor overlay plane that a
/// per-window capture never sees, WGC or BitBlt. Capturing the whole monitor instead (either
/// method) does include it, the same way DWM only flattens that overlay plane for a full-screen
/// capture — tell the user to pick a monitor target with the browser maximised on it, not the
/// browser's own window. This is a different thing from protected video (Netflix, Disney+…),
/// which comes out black even on a monitor capture; that one really is Windows' copy protection.
/// </summary>
public sealed class WindowCaptureSource : IDisposable
{
    private const int BitBltFps = 30; // PrintWindow is too slow on large windows to push further.

    private static IDirect3DDevice? s_device;
    private static readonly object s_deviceLock = new();

    private GraphicsCaptureItem? _item;
    private Direct3D11CaptureFramePool? _pool;
    private GraphicsCaptureSession? _session;
    private SizeInt32 _size;
    private int _converting;

    private Thread? _bitBltThread;
    private volatile bool _bitBltStop;
    private BitBltCaptureInterop.Frame? _bitBltFrame;

    // Copy of the latest frame: a still window sends nothing new, but the layers may need its
    // picture again (after a swap, or to keep a recording going).
    private readonly object _lastLock = new();
    private SoftwareBitmap? _last;

    /// <summary>When the latest frame arrived (MediaClock), or null if none yet.</summary>
    public TimeSpan? LastFrameTime { get; private set; }

    public CaptureTarget? Target { get; private set; }
    public bool IsRunning => _session is not null || _bitBltThread is not null;

    /// <summary>True when the captured window is minimised: Windows then stops sending frames.</summary>
    public bool IsMinimized => Target is { IsMonitor: false } t && GraphicsCaptureInterop.IsIconic(t.Window);

    /// <summary>Raised on a thread-pool thread. The handler owns and must Dispose the frame.</summary>
    public event EventHandler<CapturedFrame>? FrameArrived;

    /// <summary>The window was closed (raised on a background thread).</summary>
    public event EventHandler? Closed;

    /// <summary>Shared Direct3D device (window capture, and video files' frames).</summary>
    internal static IDirect3DDevice Device
    {
        get
        {
            lock (s_deviceLock)
                return s_device ??= GraphicsCaptureInterop.CreateDevice();
        }
    }

    /// <summary>
    /// Asks Windows (11) for programmatic, borderless capture. Desktop apps normally get it
    /// without a prompt; without it, capture still works, with the yellow frame.
    /// </summary>
    public static async Task RequestAccessAsync()
    {
        const string accessType = "Windows.Graphics.Capture.GraphicsCaptureAccess";
        if (!ApiInformation.IsTypePresent(accessType)) return;
        try
        {
            await GraphicsCaptureAccess.RequestAccessAsync(GraphicsCaptureAccessKind.Programmatic);
            await GraphicsCaptureAccess.RequestAccessAsync(GraphicsCaptureAccessKind.Borderless);
        }
        catch
        {
            // Not available on this build: keep the defaults.
        }
    }

    public void Start(CaptureTarget target, CaptureMethod method = CaptureMethod.Automatic)
    {
        Stop();

        bool bitBlt = method == CaptureMethod.BitBlt ||
                      (method == CaptureMethod.Automatic && !GraphicsCaptureInterop.IsSupported);
        if (bitBlt) StartBitBlt(target);
        else StartWgc(target);
    }

    private void StartWgc(CaptureTarget target)
    {
        var item = GraphicsCaptureInterop.CreateItem(target);
        var size = item.Size;
        var pool = Direct3D11CaptureFramePool.CreateFreeThreaded(
            Device, DirectXPixelFormat.B8G8R8A8UIntNormalized, 2, size);
        var session = pool.CreateCaptureSession(item);

        // Windows 11: no yellow frame around the captured window. Best effort; older builds and
        // some configurations insist on the border.
        try
        {
            if (ApiInformation.IsPropertyPresent(typeof(GraphicsCaptureSession).FullName, nameof(GraphicsCaptureSession.IsBorderRequired)))
                session.IsBorderRequired = false;
        }
        catch
        {
            // Keep the border.
        }

        item.Closed += OnItemClosed;
        pool.FrameArrived += OnFrameArrived;

        _item = item;
        _pool = pool;
        _session = session;
        _size = size;
        Target = target;
        session.StartCapture();
    }

    private void OnItemClosed(GraphicsCaptureItem sender, object args) => Closed?.Invoke(this, EventArgs.Empty);

    private void OnFrameArrived(Direct3D11CaptureFramePool sender, object args)
    {
        var frame = sender.TryGetNextFrame();
        if (frame is null) return;

        if (frame.ContentSize.Width != _size.Width || frame.ContentSize.Height != _size.Height)
        {
            // The window was resized: rebuild the pool at the new size and skip this frame.
            _size = frame.ContentSize;
            frame.Dispose();
            if (_size.Width > 0 && _size.Height > 0)
                sender.Recreate(Device, DirectXPixelFormat.B8G8R8A8UIntNormalized, 2, _size);
            return;
        }

        // Copying to the CPU takes a few ms; if the previous copy is still running, skip this frame.
        if (Interlocked.Exchange(ref _converting, 1) == 1)
        {
            frame.Dispose();
            return;
        }

        var now = MediaClock.Now;
        var timestamp = (now - frame.SystemRelativeTime).Duration() < TimeSpan.FromSeconds(2) ? frame.SystemRelativeTime : now;
        _ = CopyAsync(frame, timestamp);
    }

    private async Task CopyAsync(Direct3D11CaptureFrame frame, TimeSpan timestamp)
    {
        try
        {
            var bitmap = await SoftwareBitmap.CreateCopyFromSurfaceAsync(frame.Surface, BitmapAlphaMode.Premultiplied);
            frame.Dispose();
            Publish(bitmap, timestamp);
        }
        catch
        {
            frame.Dispose(); // A frame from a pool that was just closed; ignore it.
        }
        finally
        {
            Volatile.Write(ref _converting, 0);
        }
    }

    private void StartBitBlt(CaptureTarget target)
    {
        Target = target;
        _bitBltStop = false;
        _bitBltFrame = new BitBltCaptureInterop.Frame();
        _bitBltThread = new Thread(() => BitBltLoop(target))
        {
            IsBackground = true,
            Name = "LCapture BitBlt capture",
        };
        _bitBltThread.Start();
    }

    private void BitBltLoop(CaptureTarget target)
    {
        var sw = Stopwatch.StartNew();
        const double frameIntervalMs = 1000.0 / BitBltFps;
        double nextTick = 0;

        while (!_bitBltStop)
        {
            if (!target.IsMonitor && !GraphicsCaptureInterop.IsWindow(target.Window))
            {
                Closed?.Invoke(this, EventArgs.Empty);
                break;
            }

            if (!IsMinimized) CaptureBitBltFrame(target);

            nextTick += frameIntervalMs;
            double waitMs = nextTick - sw.Elapsed.TotalMilliseconds;
            if (waitMs > 0) Thread.Sleep((int)waitMs);
            else nextTick = sw.Elapsed.TotalMilliseconds; // fell behind: resync instead of catching up
        }
    }

    private unsafe void CaptureBitBltFrame(CaptureTarget target)
    {
        if (_bitBltFrame is not { } frame || !frame.Capture(target) || frame.Width <= 0 || frame.Height <= 0) return;

        var bitmap = new SoftwareBitmap(BitmapPixelFormat.Bgra8, frame.Width, frame.Height, BitmapAlphaMode.Ignore);
        using (var dst = new BitmapLock(bitmap, BitmapBufferAccessMode.Write))
            PixelOps.Copy((byte*)frame.Bits, frame.Stride, dst.Data, dst.Stride, frame.Width, frame.Height);
        Publish(bitmap, MediaClock.Now);
    }

    /// <summary>Keeps the latest-frame cache up to date and raises <see cref="FrameArrived"/>.</summary>
    private void Publish(SoftwareBitmap bitmap, TimeSpan timestamp)
    {
        lock (_lastLock)
        {
            _last?.Dispose();
            _last = SoftwareBitmap.Copy(bitmap);
            LastFrameTime = timestamp;
        }
        var handler = FrameArrived;
        if (handler is null) bitmap.Dispose();
        else handler(this, new CapturedFrame(bitmap, owner: null, timestamp));
    }

    /// <summary>A copy of the latest frame, stamped now; null if none. The caller owns it.</summary>
    public CapturedFrame? RepeatLastFrame()
    {
        lock (_lastLock)
        {
            if (_last is null) return null;
            var now = MediaClock.Now;
            LastFrameTime = now;
            return new CapturedFrame(SoftwareBitmap.Copy(_last), owner: null, now);
        }
    }

    public void Stop()
    {
        lock (_lastLock)
        {
            _last?.Dispose();
            _last = null;
            LastFrameTime = null;
        }

        if (_pool is not null) _pool.FrameArrived -= OnFrameArrived;
        if (_item is not null) _item.Closed -= OnItemClosed;
        _session?.Dispose();
        _pool?.Dispose();
        _session = null;
        _pool = null;
        _item = null;

        if (_bitBltThread is { } thread)
        {
            _bitBltStop = true;
            thread.Join(TimeSpan.FromSeconds(1));
            _bitBltThread = null;
        }
        _bitBltFrame?.Dispose();
        _bitBltFrame = null;

        Target = null;
    }

    public void Dispose() => Stop();
}
