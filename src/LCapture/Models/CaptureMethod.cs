namespace LCapture.Models;

/// <summary>
/// How a window or screen is captured. Automatic picks WGC when supported; WGC gives a
/// borderless capture but misses hardware video overlays (browser video, e.g. YouTube) on some
/// systems; BitBlt reads the desktop's already-composed image (like OBS's compatibility mode),
/// which includes that video at the cost of the window's own title bar and more CPU use.
/// </summary>
public enum CaptureMethod { Automatic, Wgc, BitBlt }
