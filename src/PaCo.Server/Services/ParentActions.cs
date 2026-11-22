using System.Globalization;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text.RegularExpressions;
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
    IReadOnlyList<string> WindowsUsers,
    bool NoTimeLimit = false);

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
            var u = PolicyBuilder.Aggregate(BudgetUsage(c, usage.Where(x => x.ChildId == c.Id).ToList()));
            var policy = PolicyBuilder.ToPolicy(c, grants);
            var eval = TimeRules.Evaluate(policy, u, now, tz);
            var top = u.Apps.OrderByDescending(a => a.Value.Seconds).Take(5)
                .Select(a => new AppTime(a.Key, string.IsNullOrWhiteSpace(a.Value.DisplayName) ? a.Key : a.Value.DisplayName, a.Value.Seconds)).ToList();
            var views = accounts.Where(a => a.ChildId == c.Id).GroupBy(a => a.DeviceId)
                .Select(g => devices.FirstOrDefault(d => d.Id == g.Key) is { } d
                    ? new DeviceView(d.Name, d.IsOnline(now), d.LastSeenUtc,
                        d.IsOnline(now) ? d.GetLive().FirstOrDefault(l => l.ChildId == c.Id) : null,
                        usage.Where(x => x.ChildId == c.Id && x.DeviceId == d.Id).Sum(x => x.TotalSeconds),
                        g.Select(a => a.UserName).ToList(), !c.TimeLimitOn(d.Id))
                    : null)
                .Where(v => v is not null).Select(v => v!).OrderBy(v => v.Name).ToList();
            return new ChildStatus(c, policy.RuleFor(FamilyClock.ToLocal(now, tz).DayOfWeek), u, eval,
                active.Contains(c.Id), locked.Contains(c.Id), top, grants.Where(g => g.ChildId == c.Id).ToList(), views);
        }).ToList();
    }

    /// <summary>
    /// Usage that counts against the daily time: PCs without time limit never count; with one budget per PC the most
    /// used PC decides (that one runs out first).
    /// </summary>
    public static IEnumerable<UsageDay> BudgetUsage(Child c, IReadOnlyList<UsageDay> rows)
    {
        var limited = rows.Where(r => c.TimeLimitOn(r.DeviceId)).ToList();
        if (!c.TimePerDevice) return limited;
        return limited.GroupBy(r => r.DeviceId).OrderByDescending(g => g.Sum(r => r.RegularSeconds)).FirstOrDefault()?.ToList() ?? [];
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
        if (seconds < 60) return seconds <= 0 ? Ui.T("0 Min") : Ui.T("< 1 Min");
        var ts = TimeSpan.FromSeconds(seconds);
        return ts.TotalHours >= 1
            ? Ui.F("{0} Std {1} Min", (int)ts.TotalHours, ts.Minutes.ToString("00", CultureInfo.InvariantCulture))
            : Ui.F("{0} Min", ts.Minutes);
    }

    public static string Remaining(long? seconds) => seconds is null ? Ui.T("unbegrenzt") : Duration(seconds.Value);

    public static string Window(DayRule r) =>
        r.WindowStart is { } s && r.WindowEnd is { } e ? Ui.F("{0}–{1} Uhr", s.ToString("t"), e.ToString("t")) : Ui.T("ganztags");

    public static string Limit(DayRule r) => r.LimitMinutes is { } m ? Duration(m * 60L) : Ui.T("unbegrenzt");

    public static string Ago(DateTimeOffset? t, DateTimeOffset now)
    {
        if (t is null) return Ui.T("noch nie");
        var d = now - t.Value;
        if (d < TimeSpan.FromMinutes(2)) return Ui.T("gerade eben");
        if (d < TimeSpan.FromHours(1)) return Ui.F("vor {0} Min", (int)d.TotalMinutes);
        if (d < TimeSpan.FromDays(1)) return Ui.F("vor {0} Std", (int)d.TotalHours);
        var days = (int)d.TotalDays;
        return days == 1 ? Ui.T("vor 1 Tag") : Ui.F("vor {0} Tagen", days);
    }

    public static string Grant(int minutes) => minutes == 0 ? Ui.T("unbegrenzt (heute)") : Ui.F("+{0} Min", minutes);

    public static string Event(DeviceEventKind k) => k switch
    {
        DeviceEventKind.ServiceStarted => Ui.T("Dienst gestartet"),
        DeviceEventKind.ForcedLogoff => Ui.T("Abgemeldet"),
        DeviceEventKind.Locked => Ui.T("Gesperrt"),
        DeviceEventKind.Unlocked => Ui.T("Freigegeben"),
        DeviceEventKind.UnlockFailed => Ui.T("Falscher Code"),
        DeviceEventKind.AppBlocked => Ui.T("Programm blockiert"),
        DeviceEventKind.ClockTampering => Ui.T("Uhr verstellt"),
        DeviceEventKind.ChildIsAdmin => Ui.T("Admin-Rechte"),
        DeviceEventKind.PolicyRejected => Ui.T("Regeln abgelehnt"),
        DeviceEventKind.AgentFailure => Ui.T("Sperre nicht möglich"),
        DeviceEventKind.Uninstalled => Ui.T("Deinstalliert"),
        DeviceEventKind.ClientError => Ui.T("Technischer Fehler"),
        DeviceEventKind.SafeMode => Ui.T("Abgesicherter Modus"),
        DeviceEventKind.OfflineUsage => Ui.T("Nutzung ohne Internet"),
        DeviceEventKind.AgentCrashed => Ui.T("Sitzungsprogramm beendet"),
        _ => Ui.T("Info"),
    };

    /// <summary>Events that may indicate an attempt to get around PaCo.</summary>
    public static readonly DeviceEventKind[] Suspicious =
    [
        DeviceEventKind.ClockTampering, DeviceEventKind.UnlockFailed, DeviceEventKind.AppBlocked, DeviceEventKind.PolicyRejected,
        DeviceEventKind.SafeMode, DeviceEventKind.OfflineUsage, DeviceEventKind.AgentCrashed, DeviceEventKind.AgentFailure,
        DeviceEventKind.Uninstalled, DeviceEventKind.ChildIsAdmin,
    ];

    /// <summary>Weekday names in the current language, indexed by <see cref="DayOfWeek"/> (0 = Sunday).</summary>
    public static string[] WeekdayNames => CultureInfo.CurrentCulture.DateTimeFormat.DayNames;

    /// <summary>Weekday name in the current language ("Montag").</summary>
    public static string DayName(DayOfWeek d) => CultureInfo.CurrentCulture.DateTimeFormat.GetDayName(d);

    /// <summary>Short weekday name in the current language ("Mo").</summary>
    public static string ShortDayName(DayOfWeek d) => CultureInfo.CurrentCulture.DateTimeFormat.GetAbbreviatedDayName(d);

    /// <summary>Day and month without the year in the current culture's order ("15.11.", "11/15", "11/15").</summary>
    public static string DayMonth(DateTime d) => d.ToString(DayMonthPattern(CultureInfo.CurrentCulture), CultureInfo.CurrentCulture);

    public static string DayMonth(DateOnly d) => DayMonth(d.ToDateTime(TimeOnly.MinValue));

    /// <summary>Short day/month plus time ("15.11. 14:05") for lists of recent events.</summary>
    public static string DayMonthTime(DateTimeOffset local) => $"{DayMonth(local.DateTime)} {local.ToString("t", CultureInfo.CurrentCulture)}";

    /// <summary>The culture's short date pattern with the year (and literal suffixes like 'г.') removed.</summary>
    private static string DayMonthPattern(CultureInfo culture)
    {
        var p = Regex.Replace(culture.DateTimeFormat.ShortDatePattern, @"\s*'[^']*'\.?", "");
        p = Regex.Replace(p, @"y+\.?\s*", "").Trim(' ', '/', '-', ',', '\u200f', '\u200e');
        return p.Contains('d') && p.Contains('M') ? p : culture.DateTimeFormat.MonthDayPattern;
    }

    /// <summary>Monday-first order for display.</summary>
    public static readonly DayOfWeek[] WeekOrder =
        [DayOfWeek.Monday, DayOfWeek.Tuesday, DayOfWeek.Wednesday, DayOfWeek.Thursday, DayOfWeek.Friday, DayOfWeek.Saturday, DayOfWeek.Sunday];
}
