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
    /// Single pages or videos (see <see cref="TimeGroups.NormalizePage"/>), e.g. "youtube.com/watch?v=abc" – only that
    /// video, not the rest of YouTube. A page also covers its sub-pages ("example.com/math" → "example.com/math/1").
    /// </summary>
    public IReadOnlyList<string> Pages { get; init; } = [];

    /// <summary>Optional names the parents gave the pages (key = normalized page) – used for the child's link folder.</summary>
    public IReadOnlyDictionary<string, string> PageTitles { get; init; } = new Dictionary<string, string>();

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

    private static readonly HashSet<string> YouTubeHosts = new(StringComparer.OrdinalIgnoreCase)
        { "youtube.com", "m.youtube.com", "music.youtube.com", "youtube-nocookie.com", "youtu.be" };

    /// <summary>
    /// A single page as a comparable key: host without "www." plus path, without query and fragment. Videos get one form
    /// for all their addresses: "https://youtu.be/ID", "youtube.com/shorts/ID", ".../embed/ID", ".../watch?v=ID&amp;t=3"
    /// → "youtube.com/watch?v=ID". Null for a bare domain (no path) or anything that is not a web address.
    /// </summary>
    public static string? NormalizePage(string? urlOrText)
    {
        if (string.IsNullOrWhiteSpace(urlOrText)) return null;
        var s = urlOrText.Trim();
        if (s.Contains(' ')) return null;
        if (!s.Contains("://", StringComparison.Ordinal)) s = "https://" + s;
        if (!Uri.TryCreate(s, UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https")) return null;
        var host = uri.IdnHost.ToLowerInvariant();
        if (host.StartsWith("www.", StringComparison.Ordinal)) host = host[4..];
        if (!host.Contains('.')) return null;
        var path = uri.AbsolutePath.TrimEnd('/');
        if (YouTubeHosts.Contains(host))
        {
            var id = host == "youtu.be" ? path.Trim('/')
                : path is "/watch" ? QueryValue(uri.Query, "v")
                : path.StartsWith("/shorts/", StringComparison.Ordinal) || path.StartsWith("/embed/", StringComparison.Ordinal) || path.StartsWith("/live/", StringComparison.Ordinal)
                    ? path.Split('/')[2] : null;
            if (id is { Length: > 0 } && id.All(c => char.IsLetterOrDigit(c) || c is '-' or '_')) return $"youtube.com/watch?v={id}";
        }
        return path.Length == 0 ? null : host + path.ToLowerInvariant();
    }

    /// <summary>Address to open for a normalized page ("youtube.com/watch?v=ID" → "https://www.youtube.com/watch?v=ID").</summary>
    public static string PageUrl(string page) =>
        "https://" + (page.StartsWith("youtube.com/", StringComparison.OrdinalIgnoreCase) ? "www." : "") + page;

    private static string? QueryValue(string query, string key)
    {
        foreach (var part in query.TrimStart('?').Split('&'))
        {
            var kv = part.Split('=', 2);
            if (kv.Length == 2 && kv[0] == key) return Uri.UnescapeDataString(kv[1]);
        }
        return null;
    }

    /// <summary>The open address is this page (videos: exactly this video) or one of its sub-pages.</summary>
    public static bool PageMatches(string? url, string page)
    {
        if (NormalizePage(url) is not { } open) return false;
        if (open.Equals(page, StringComparison.OrdinalIgnoreCase)) return true;
        return !page.Contains('?') && open.StartsWith(page + "/", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Groups the foreground app/site belongs to.</summary>
    public static List<TimeGroup> Matching(IEnumerable<TimeGroup> groups, string? process, string? description, string? host, string? url = null)
    {
        var result = new List<TimeGroup>();
        foreach (var g in groups)
        {
            var app = process is not null && g.Apps.Contains(process, StringComparer.OrdinalIgnoreCase) && DescriptionOk(g, process, description);
            var site = (host is not null && g.Sites.Any(s => SiteMatches(host, s))) || (url is not null && g.Pages.Any(p => PageMatches(url, p)));
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

    /// <summary>
    /// Learning mode is possible: the parents allowed it, there is at least one learning group, and the PC is blocked
    /// only because the time is used up (or outside the allowed hours, if the parents allowed that too).
    /// </summary>
    public static bool LearningModeAvailable(ChildPolicy child, Evaluation eval) =>
        child.LearningAfterExpiry && !child.Paused && !child.LockedByParent && !eval.Allowed
        && child.Groups.Any(g => g.Kind == TimeGroupKind.Free && (g.Apps.Count > 0 || g.Sites.Count > 0 || g.Pages.Count > 0))
        && (eval.Reason == BlockReason.LimitReached || (eval.Reason == BlockReason.OutsideWindow && child.LearningOutsideWindow));

    public static IReadOnlyList<TimeGroup> LearningGroups(ChildPolicy child) => child.Groups.Where(g => g.Kind == TimeGroupKind.Free).ToList();

    public static IReadOnlyList<string> LearningSites(ChildPolicy child) =>
        LearningGroups(child).SelectMany(g => g.Sites).Where(s => !string.IsNullOrWhiteSpace(s)).Distinct(StringComparer.OrdinalIgnoreCase).ToList();

    public static IReadOnlyList<string> LearningPages(ChildPolicy child) =>
        LearningGroups(child).SelectMany(g => g.Pages).Distinct(StringComparer.OrdinalIgnoreCase).ToList();

    /// <summary>
    /// Browser allow list in learning mode (everything else is blocked – the browsers apply this to embedded frames too,
    /// so a learning site cannot show other sites in an iframe). An allowed video may also be embedded, but only that one.
    /// </summary>
    public static IReadOnlyList<string> LearningAllowList(ChildPolicy child)
    {
        var list = new List<string>(LearningSites(child));
        foreach (var page in LearningPages(child))
        {
            list.Add(page);
            const string video = "youtube.com/watch?v=";
            if (page.StartsWith(video, StringComparison.OrdinalIgnoreCase))
            {
                var id = page[video.Length..];
                list.Add($"youtube.com/embed/{id}");
                list.Add($"youtube-nocookie.com/embed/{id}");
            }
        }
        return list.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
    }

    /// <summary>Learning mode: the open browser address is a learning site or page (unknown address = not allowed).</summary>
    public static bool IsLearningAddress(ChildPolicy child, string? host, string? url) =>
        (host is not null && LearningSites(child).Any(s => SiteMatches(host, s))) || (url is not null && LearningPages(child).Any(p => PageMatches(url, p)));

    /// <summary>A learning app: in a learning group and – like for learning time – with the known description.</summary>
    public static bool IsLearningApp(ChildPolicy child, string process, string? description) =>
        Matching(LearningGroups(child), process, description, null).Count > 0;

    /// <summary>Seconds left today for a limited group (null = no limit).</summary>
    public static long? SecondsLeft(TimeGroup g, DayUsage usage, IReadOnlyDictionary<Guid, Dictionary<DateOnly, long>> elsewhere, DateOnly day)
    {
        if (g.Kind != TimeGroupKind.Limit || g.MinutesPerDay is not { } minutes) return null;
        var other = elsewhere.TryGetValue(g.Id, out var perDay) ? perDay.GetValueOrDefault(day) : 0;
        return Math.Max(0, minutes * 60L - usage.Groups.GetValueOrDefault(g.Id) - other);
    }
}
