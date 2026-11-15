using System.Security.Claims;
using System.Security.Cryptography;
using Microsoft.EntityFrameworkCore;
using PaCo.Server.Data;
using PaCo.Shared;

namespace PaCo.Server.Services;

public sealed record AppTime(string Process, string Name, long Seconds);

/// <summary>One PC of a child as seen by the website.</summary>
public sealed record DeviceView(
    string Name,
    bool Online,
    DateTimeOffset? LastSeenUtc,
    ChildLiveStatus? Live,
    long UsedTodaySeconds,
    IReadOnlyList<string> WindowsUsers);

public sealed record ChildStatus(
    Child Child,
    DayRule Rule,
    DayUsage Today,
    Evaluation Eval,
    bool IsActive,
    bool IsOnLockScreen,
    IReadOnlyList<AppTime> TopApps,
    IReadOnlyList<TimeGrantEntity> TodayGrants,
    IReadOnlyList<DeviceView> Devices)
{
    public int DeviceCount => Devices.Count;

    /// <summary>
    /// Best known remaining time: what an online PC reports (it also knows offline unlock codes), otherwise the
    /// server's estimate.
    /// </summary>
    public long? RemainingSeconds =>
        Devices.Where(d => d.Online && d.Live is not null).Select(d => d.Live!).OrderByDescending(l => l.Active).FirstOrDefault() is { } l
            ? l.RemainingSeconds
            : Eval.RemainingSeconds;
}

/// <summary>Operations parents trigger on the website. All queries are family-filtered via the DbContext.</summary>
public sealed class ParentActions(PaCoDb db, IFamilyContext family, TimeProvider clock)
{
    public Guid FamilyId => family.FamilyId ?? throw new InvalidOperationException("Keine Familie angemeldet.");

    public async Task<Family> GetFamilyAsync(CancellationToken ct = default) => await db.Families.SingleAsync(f => f.Id == FamilyId, ct);

    public async Task<(TimeZoneInfo Tz, DateOnly Today, DateTimeOffset Now)> FamilyNowAsync(CancellationToken ct = default)
    {
        var fam = await GetFamilyAsync(ct);
        var tz = FamilyClock.Resolve(fam.TimeZoneId);
        var now = clock.GetUtcNow();
        return (tz, FamilyClock.Day(now, tz), now);
    }

    public Task<Child?> FindChildAsync(Guid id, CancellationToken ct = default) => db.Children.FirstOrDefaultAsync(c => c.Id == id, ct);

    public async Task<IReadOnlyList<ChildStatus>> StatusAsync(CancellationToken ct = default)
    {
        var (tz, today, now) = await FamilyNowAsync(ct);
        var children = await db.Children.OrderBy(c => c.Name).ToListAsync(ct);
        var usage = await db.UsageDays.Where(u => u.Day == today).ToListAsync(ct);
        var grants = await db.TimeGrants.Where(g => g.Day == today).OrderBy(g => g.CreatedUtc).ToListAsync(ct);
        var devices = await db.Devices.Where(d => !d.Revoked).ToListAsync(ct);
        var accounts = await db.DeviceAccounts.Where(a => a.ChildId != null).ToListAsync(ct);

        var onlineDevices = devices.Where(d => d.IsOnline(now)).ToList();
        var active = onlineDevices.SelectMany(d => d.GetActiveChildren()).ToHashSet();
        var locked = onlineDevices.SelectMany(d => d.GetLockedChildren()).ToHashSet();

        return children.Select(c =>
        {
            var u = PolicyBuilder.Aggregate(usage.Where(x => x.ChildId == c.Id));
            var policy = PolicyBuilder.ToPolicy(c, grants);
            var eval = TimeRules.Evaluate(policy, u, now, tz);
            var top = u.Apps.OrderByDescending(a => a.Value.Seconds).Take(5)
                .Select(a => new AppTime(a.Key, string.IsNullOrWhiteSpace(a.Value.DisplayName) ? a.Key : a.Value.DisplayName, a.Value.Seconds)).ToList();
            var views = accounts.Where(a => a.ChildId == c.Id).GroupBy(a => a.DeviceId)
                .Select(g => devices.FirstOrDefault(d => d.Id == g.Key) is { } d
                    ? new DeviceView(d.Name, d.IsOnline(now), d.LastSeenUtc,
                        d.IsOnline(now) ? d.GetLive().FirstOrDefault(l => l.ChildId == c.Id) : null,
                        usage.Where(x => x.ChildId == c.Id && x.DeviceId == d.Id).Sum(x => x.TotalSeconds),
                        g.Select(a => a.UserName).ToList())
                    : null)
                .Where(v => v is not null).Select(v => v!).OrderBy(v => v.Name).ToList();
            return new ChildStatus(c, policy.RuleFor(FamilyClock.ToLocal(now, tz).DayOfWeek), u, eval,
                active.Contains(c.Id), locked.Contains(c.Id), top, grants.Where(g => g.ChildId == c.Id).ToList(), views);
        }).ToList();
    }

    public async Task GrantAsync(Guid childId, int minutes, ClaimsPrincipal user, CancellationToken ct = default)
    {
        if (minutes is < 0 or > 600) throw new ArgumentOutOfRangeException(nameof(minutes));
        var child = await FindChildAsync(childId, ct) ?? throw new KeyNotFoundException();
        var (_, today, _) = await FamilyNowAsync(ct);
        db.TimeGrants.Add(new TimeGrantEntity { FamilyId = FamilyId, ChildId = child.Id, Day = today, Minutes = minutes, CreatedBy = UserId(user) });
        // Granting time implies the parent wants the child to be able to use it.
        child.LockedByParent = false;
        await db.SaveChangesAsync(ct);
    }

    public async Task RevokeTodayGrantsAsync(Guid childId, CancellationToken ct = default)
    {
        var (_, today, _) = await FamilyNowAsync(ct);
        var grants = await db.TimeGrants.Where(g => g.ChildId == childId && g.Day == today).ToListAsync(ct);
        db.TimeGrants.RemoveRange(grants);
        await db.SaveChangesAsync(ct);
    }

    public async Task SetLockedAsync(Guid childId, bool locked, CancellationToken ct = default)
    {
        var child = await FindChildAsync(childId, ct) ?? throw new KeyNotFoundException();
        child.LockedByParent = locked;
        await db.SaveChangesAsync(ct);
    }

    public async Task SetPausedAsync(Guid childId, bool paused, CancellationToken ct = default)
    {
        var child = await FindChildAsync(childId, ct) ?? throw new KeyNotFoundException();
        child.Paused = paused;
        await db.SaveChangesAsync(ct);
    }

    /// <summary>Creates a one-time offline unlock code for today.</summary>
    public async Task<string> CreateUnlockCodeAsync(Guid childId, int minutes, ClaimsPrincipal user, CancellationToken ct = default)
    {
        if (!UnlockCodes.AllowedMinutes.Contains(minutes)) throw new ArgumentOutOfRangeException(nameof(minutes));
        var child = await FindChildAsync(childId, ct) ?? throw new KeyNotFoundException();
        var fam = await GetFamilyAsync(ct);
        var (_, today, _) = await FamilyNowAsync(ct);

        var used = (await db.UnlockCodes.Where(c => c.ChildId == child.Id && c.Day == today).Select(c => c.Nonce).ToListAsync(ct)).ToHashSet();
        if (used.Count > 900) throw new InvalidOperationException("Für heute wurden zu viele Codes erzeugt.");
        int nonce;
        do nonce = RandomNumberGenerator.GetInt32(0, UnlockCodes.MaxNonce + 1); while (used.Contains(nonce));

        db.UnlockCodes.Add(new UnlockCodeIssue { FamilyId = FamilyId, ChildId = child.Id, Day = today, Nonce = nonce, Minutes = minutes, CreatedBy = UserId(user) });
        await db.SaveChangesAsync(ct);
        return UnlockCodes.Generate(fam.UnlockKey, child.Id, today, minutes, nonce);
    }

    public static Guid UserId(ClaimsPrincipal user) =>
        Guid.TryParse(user.FindFirstValue(ClaimTypes.NameIdentifier), out var id) ? id : Guid.Empty;
}

public static class Format
{
    public static string Duration(long seconds)
    {
        if (seconds < 60) return seconds <= 0 ? "0 Min" : "< 1 Min";
        var ts = TimeSpan.FromSeconds(seconds);
        return ts.TotalHours >= 1 ? $"{(int)ts.TotalHours} Std {ts.Minutes:00} Min" : $"{ts.Minutes} Min";
    }

    public static string Remaining(long? seconds) => seconds is null ? "unbegrenzt" : Duration(seconds.Value);

    public static string Window(DayRule r) =>
        r.WindowStart is { } s && r.WindowEnd is { } e ? $"{s:HH\\:mm}–{e:HH\\:mm} Uhr" : "ganztags";

    public static string Limit(DayRule r) => r.LimitMinutes is { } m ? Duration(m * 60L) : "unbegrenzt";

    public static string Ago(DateTimeOffset? t, DateTimeOffset now)
    {
        if (t is null) return "noch nie";
        var d = now - t.Value;
        if (d < TimeSpan.FromMinutes(2)) return "gerade eben";
        if (d < TimeSpan.FromHours(1)) return $"vor {(int)d.TotalMinutes} Min";
        if (d < TimeSpan.FromDays(1)) return $"vor {(int)d.TotalHours} Std";
        return $"vor {(int)d.TotalDays} Tagen";
    }

    public static string Grant(int minutes) => minutes == 0 ? "unbegrenzt (heute)" : $"+{minutes} Min";

    public static string Event(DeviceEventKind k) => k switch
    {
        DeviceEventKind.ServiceStarted => "Dienst gestartet",
        DeviceEventKind.ForcedLogoff => "Abgemeldet",
        DeviceEventKind.Locked => "Gesperrt",
        DeviceEventKind.Unlocked => "Freigegeben",
        DeviceEventKind.UnlockFailed => "Falscher Code",
        DeviceEventKind.AppBlocked => "Programm blockiert",
        DeviceEventKind.ClockTampering => "Uhr verstellt",
        DeviceEventKind.ChildIsAdmin => "Admin-Rechte",
        DeviceEventKind.PolicyRejected => "Regeln abgelehnt",
        DeviceEventKind.AgentFailure => "Sperre nicht möglich",
        DeviceEventKind.Uninstalled => "Deinstalliert",
        DeviceEventKind.ClientError => "Technischer Fehler",
        DeviceEventKind.SafeMode => "Abgesicherter Modus",
        DeviceEventKind.OfflineUsage => "Nutzung ohne Internet",
        DeviceEventKind.AgentCrashed => "Sitzungsprogramm beendet",
        _ => "Info",
    };

    /// <summary>Events that may indicate an attempt to get around PaCo.</summary>
    public static readonly DeviceEventKind[] Suspicious =
    [
        DeviceEventKind.ClockTampering, DeviceEventKind.UnlockFailed, DeviceEventKind.AppBlocked, DeviceEventKind.PolicyRejected,
        DeviceEventKind.SafeMode, DeviceEventKind.OfflineUsage, DeviceEventKind.AgentCrashed, DeviceEventKind.AgentFailure,
        DeviceEventKind.Uninstalled, DeviceEventKind.ChildIsAdmin,
    ];

    public static readonly string[] WeekdayNames = ["Sonntag", "Montag", "Dienstag", "Mittwoch", "Donnerstag", "Freitag", "Samstag"];

    /// <summary>Monday-first order for display.</summary>
    public static readonly DayOfWeek[] WeekOrder =
        [DayOfWeek.Monday, DayOfWeek.Tuesday, DayOfWeek.Wednesday, DayOfWeek.Thursday, DayOfWeek.Friday, DayOfWeek.Saturday, DayOfWeek.Sunday];
}
