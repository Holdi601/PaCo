using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;
using PaCo.Server.Data;
using PaCo.Server.Services;
using PaCo.Shared;

namespace PaCo.Server.Pages.Children;

public sealed class StatsModel(PaCoDb db, ParentActions actions) : PageModel
{
    public Child Child { get; private set; } = null!;

    public DateOnly Day { get; private set; }

    public DateOnly Today { get; private set; }

    public TimeZoneInfo Tz { get; private set; } = TimeZoneInfo.Utc;

    public IReadOnlyList<(DateOnly Day, long Seconds, long? LimitSeconds)> Last14 { get; private set; } = [];

    public IReadOnlyList<AppTime> Apps { get; private set; } = [];

    public long DayTotal { get; private set; }

    public long DayBonus { get; private set; }

    public long WeekAverage { get; private set; }

    public IReadOnlyList<DeviceEventEntity> Events { get; private set; } = [];

    public IReadOnlyDictionary<Guid, string> DeviceNames { get; private set; } = new Dictionary<Guid, string>();

    public async Task<IActionResult> OnGetAsync(Guid id, DateOnly? day)
    {
        var child = await db.Children.FirstOrDefaultAsync(c => c.Id == id);
        if (child is null) return NotFound();
        Child = child;
        var (tz, today, _) = await actions.FamilyNowAsync();
        Tz = tz;
        Today = today;
        Day = day is { } d && d <= today && d > today.AddDays(-90) ? d : today;

        var from = today.AddDays(-13);
        var rows = await db.UsageDays.Where(u => u.ChildId == id && u.Day >= from).ToListAsync();
        var rules = child.GetDays();
        Last14 = Enumerable.Range(0, 14).Select(i => today.AddDays(-i)).Select(dd =>
                (dd, rows.Where(r => r.Day == dd).Sum(r => r.TotalSeconds), rules[(int)dd.DayOfWeek].LimitMinutes is { } m ? m * 60L : (long?)null))
            .ToList();
        WeekAverage = (long)Last14.Take(7).Average(x => x.Seconds);

        var dayRows = Day >= from ? rows.Where(r => r.Day == Day).ToList() : await db.UsageDays.Where(u => u.ChildId == id && u.Day == Day).ToListAsync();
        var agg = PolicyBuilder.Aggregate(dayRows);
        DayTotal = agg.TotalSeconds;
        DayBonus = agg.TotalSeconds - agg.RegularSeconds;
        Apps = agg.Apps.OrderByDescending(a => a.Value.Seconds)
            .Select(a => new AppTime(a.Key, string.IsNullOrWhiteSpace(a.Value.DisplayName) ? a.Key : a.Value.DisplayName, a.Value.Seconds)).ToList();

        var since = DateTimeOffset.UtcNow.AddDays(-14);
        Events = await db.DeviceEvents.Where(e => e.ChildId == id && e.TimeUtc > since).OrderByDescending(e => e.TimeUtc).Take(50).ToListAsync();
        DeviceNames = await db.Devices.ToDictionaryAsync(d => d.Id, d => d.Name);
        return Page();
    }

    public static string EventText(DeviceEventKind k) => k switch
    {
        DeviceEventKind.ForcedLogoff => "Abgemeldet",
        DeviceEventKind.Locked => "Gesperrt",
        DeviceEventKind.Unlocked => "Entsperrt",
        DeviceEventKind.UnlockFailed => "Falscher Code",
        DeviceEventKind.AppBlocked => "Programm blockiert",
        DeviceEventKind.ClockTampering => "Uhr verstellt",
        DeviceEventKind.ChildIsAdmin => "Admin-Rechte",
        _ => k.ToString(),
    };
}
