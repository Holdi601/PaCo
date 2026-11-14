namespace PaCo.Client;

/// <summary>
/// Monotonic time since boot. Use this – never DateTime.UtcNow – for intervals, throttles and timeouts:
/// the wall clock can jump (BIOS, time sync, manipulation) and would freeze or skip such timers.
/// </summary>
internal static class Mono
{
    public static TimeSpan Now => TimeSpan.FromMilliseconds(Environment.TickCount64);

    public static TimeSpan Since(TimeSpan earlier) => Now - earlier;

    /// <summary>A point far in the past, so "elapsed since" checks fire immediately.</summary>
    public static readonly TimeSpan Never = TimeSpan.FromDays(-3650);
}
