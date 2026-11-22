namespace PaCo.Shared;

public enum TimeGroupKind
{
    /// <summary>Own daily limit (e.g. "Games 60 min"); the time also counts towards the normal daily time.</summary>
    Limit = 0,

    /// <summary>Learning time: while one of these apps/sites is in the foreground, no PC time is used up.</summary>
    Free = 1,
}

/// <summary>
/// A group of programs and/or websites with its own rule – a limit shorter than the daily time, or "does not count".
/// Time counts only while a member is the foreground window (for websites: the active browser tab).
/// </summary>
public sealed record TimeGroup
{
    public Guid Id { get; init; } = Guid.NewGuid();

    public string Name { get; init; } = "";

    public TimeGroupKind Kind { get; init; }

    /// <summary>Minutes per day for <see cref="TimeGroupKind.Limit"/>; null = only measured, no limit.</summary>
    public int? MinutesPerDay { get; init; }

    /// <summary>Process names (lower case, without .exe).</summary>
    public IReadOnlyList<string> Apps { get; init; } = [];

    /// <summary>Domains; a site matches its subdomains too ("youtube.com" matches "m.youtube.com").</summary>
    public IReadOnlyList<string> Sites { get; init; } = [];

    /// <summary>
    /// Program description (FileDescription) seen for an app when the parents picked it. For learning time the running
    /// program must still have this description – a game renamed to "anton.exe" does not become learning time.
    /// </summary>
    public IReadOnlyDictionary<string, string> AppDescriptions { get; init; } = new Dictionary<string, string>();
}

public static class TimeGroups
{
    public static bool SiteMatches(string host, string site) =>
        host.Equals(site, StringComparison.OrdinalIgnoreCase) || host.EndsWith("." + site, StringComparison.OrdinalIgnoreCase);

    /// <summary>"https://www.YouTube.com/watch?v=1" / "youtube.com/watch" / "youtube.com" → "www.youtube.com" / "youtube.com".</summary>
    public static string? HostOf(string? urlOrText)
    {
        if (string.IsNullOrWhiteSpace(urlOrText)) return null;
        var s = urlOrText.Trim();
        if (s.Contains(' ')) return null; // a search, not an address
        if (!s.Contains("://", StringComparison.Ordinal)) s = "https://" + s;
        if (!Uri.TryCreate(s, UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https")) return null;
        var host = uri.IdnHost.ToLowerInvariant();
        return host.Contains('.') ? host : null;
    }

    /// <summary>Groups the foreground app/site belongs to.</summary>
    public static List<TimeGroup> Matching(IEnumerable<TimeGroup> groups, string? process, string? description, string? host)
    {
        var result = new List<TimeGroup>();
        foreach (var g in groups)
        {
            var app = process is not null && g.Apps.Contains(process, StringComparer.OrdinalIgnoreCase) && DescriptionOk(g, process, description);
            var site = host is not null && g.Sites.Any(s => SiteMatches(host, s));
            if (app || site) result.Add(g);
        }
        return result;
    }

    private static bool DescriptionOk(TimeGroup g, string process, string? description)
    {
        if (g.Kind != TimeGroupKind.Free) return true; // limits always apply, renamed or not
        // Learning time only for a program PaCo has seen before with this description (filled in by the server from
        // the usage statistics) – an unknown or renamed program never runs "for free".
        var expected = g.AppDescriptions.FirstOrDefault(kv => kv.Key.Equals(process, StringComparison.OrdinalIgnoreCase)).Value;
        return !string.IsNullOrEmpty(expected) && string.Equals(expected, description, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Seconds left today for a limited group (null = no limit).</summary>
    public static long? SecondsLeft(TimeGroup g, DayUsage usage, IReadOnlyDictionary<Guid, Dictionary<DateOnly, long>> elsewhere, DateOnly day)
    {
        if (g.Kind != TimeGroupKind.Limit || g.MinutesPerDay is not { } minutes) return null;
        var other = elsewhere.TryGetValue(g.Id, out var perDay) ? perDay.GetValueOrDefault(day) : 0;
        return Math.Max(0, minutes * 60L - usage.Groups.GetValueOrDefault(g.Id) - other);
    }
}
