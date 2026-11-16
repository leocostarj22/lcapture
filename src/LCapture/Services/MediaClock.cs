using System.Diagnostics;

namespace LCapture.Services;

/// <summary>
/// The shared timeline for video frames and audio packets: QueryPerformanceCounter, the same clock
/// Media Foundation uses for <c>MediaFrameReference.SystemRelativeTime</c>.
/// </summary>
public static class MediaClock
{
    public static TimeSpan Now
    {
        get
        {
            long ticks = Stopwatch.GetTimestamp();
            // Split to avoid overflow: ticks * 10^7 overflows a long after ~10 days of uptime.
            long seconds = ticks / Stopwatch.Frequency;
            long remainder = ticks % Stopwatch.Frequency;
            return TimeSpan.FromTicks(seconds * TimeSpan.TicksPerSecond +
                                      remainder * TimeSpan.TicksPerSecond / Stopwatch.Frequency);
        }
    }
}
