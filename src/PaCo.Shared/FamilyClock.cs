namespace PaCo.Shared;

/// <summary>
/// Converts UTC into the family's local day and time. The child's PC time zone is deliberately ignored: standard
/// users may change the Windows time zone, but not the UTC clock.
/// </summary>
public static class FamilyClock
{
    public static TimeZoneInfo Resolve(string timeZoneId)
    {
        if (TimeZoneInfo.TryFindSystemTimeZoneById(timeZoneId, out var tz))
            return tz;
        if (TimeZoneInfo.TryConvertIanaIdToWindowsId(timeZoneId, out var win) && TimeZoneInfo.TryFindSystemTimeZoneById(win, out tz))
            return tz;
        if (TimeZoneInfo.TryConvertWindowsIdToIanaId(timeZoneId, out var iana) && TimeZoneInfo.TryFindSystemTimeZoneById(iana, out tz))
            return tz;
        return TimeZoneInfo.FindSystemTimeZoneById(OperatingSystem.IsWindows() ? "W. Europe Standard Time" : "Europe/Berlin");
    }

    public static DateTime ToLocal(DateTimeOffset utc, TimeZoneInfo tz) => TimeZoneInfo.ConvertTime(utc, tz).DateTime;

    public static DateOnly Day(DateTimeOffset utc, TimeZoneInfo tz) => DateOnly.FromDateTime(ToLocal(utc, tz));

    public static string DayKey(DateOnly day) => day.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture);
}
