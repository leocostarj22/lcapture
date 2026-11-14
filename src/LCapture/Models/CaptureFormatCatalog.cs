namespace LCapture.Models;

/// <summary>
/// Reduces the (often ~150) raw modes a capture card advertises to a short list of the best
/// ones: well-known resolutions only, one entry per resolution at its highest frame rate,
/// preferring uncompressed pixel formats. Works for any resolution the device offers, so a
/// card with 1440p / 2160p modes automatically gets those entries.
/// </summary>
public static class CaptureFormatCatalog
{
    private const double MinFps = 24;

    private static readonly HashSet<(uint Width, uint Height)> StandardModes =
    [
        (4096, 2160), (3840, 2160),
        (3440, 1440), (2560, 1600), (2560, 1440),
        (1920, 1200), (1920, 1080),
        (1280, 720),
    ];

    /// <summary>Curated list, highest resolution first.</summary>
    public static IReadOnlyList<CaptureFormatOption> Curate(IEnumerable<CaptureFormatOption> all)
    {
        var modes = all.ToList();

        var smooth = modes.Where(m => m.Fps >= MinFps).ToList();
        if (smooth.Count > 0) modes = smooth;

        var standard = modes.Where(m => StandardModes.Contains((m.Width, m.Height))).ToList();
        var pool = standard.Count > 0 ? standard : modes;

        var best = pool
            .GroupBy(m => (m.Width, m.Height))
            .Select(g => g
                .OrderByDescending(m => Math.Round(m.Fps))
                .ThenByDescending(m => m.Fps)
                .ThenBy(m => SubtypeRank(m.Subtype))
                .First())
            .OrderByDescending(m => (long)m.Width * m.Height)
            .ToList();

        // Unusual-only devices: keep just the few largest instead of a long list.
        return standard.Count > 0 ? best : best.Take(4).ToList();
    }

    /// <summary>
    /// Best default for a screen of the given native height: the largest mode that fits the
    /// screen and runs at 50+ fps; otherwise the largest that fits; otherwise the smallest.
    /// </summary>
    public static CaptureFormatOption PickDefault(IReadOnlyList<CaptureFormatOption> curated, double screenHeightPx)
    {
        var fitting = curated.Where(m => m.Height <= screenHeightPx + 1).ToList();
        return fitting.FirstOrDefault(m => m.Fps >= 50)
               ?? fitting.FirstOrDefault()
               ?? curated[^1];
    }

    // Lower is better: raw RGB, then 4:2:2, then 4:2:0, then compressed.
    private static int SubtypeRank(string subtype) => subtype.ToUpperInvariant() switch
    {
        "RGB32" or "ARGB32" or "RGB24" => 0,
        "YUY2" => 1,
        "NV12" => 2,
        "MJPG" => 9,
        "H264" or "HEVC" => 10,
        _ => 5,
    };
}
