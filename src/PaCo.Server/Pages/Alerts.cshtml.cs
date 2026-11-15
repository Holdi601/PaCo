using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;
using PaCo.Server.Data;
using PaCo.Server.Services;

namespace PaCo.Server.Pages;

public sealed class AlertsModel(PaCoDb db, ParentActions actions, TimeProvider clock) : PageModel
{
    public IReadOnlyList<AlertEntity> Alerts { get; private set; } = [];

    public IReadOnlyDictionary<Guid, string> ChildNames { get; private set; } = new Dictionary<Guid, string>();

    public IReadOnlyDictionary<Guid, string> DeviceNames { get; private set; } = new Dictionary<Guid, string>();

    public TimeZoneInfo Tz { get; private set; } = TimeZoneInfo.Utc;

    [BindProperty(SupportsGet = true)]
    public Guid? Child { get; set; }

    /// <summary>Unread alerts of the whole family (the "mark all as read" button acts on all of them).</summary>
    public int UnreadTotal { get; private set; }

    public async Task OnGetAsync()
    {
        (Tz, _, _) = await actions.FamilyNowAsync();
        ChildNames = await db.Children.ToDictionaryAsync(c => c.Id, c => c.Name);
        DeviceNames = await db.Devices.ToDictionaryAsync(d => d.Id, d => d.Name);
        var q = db.Alerts.AsQueryable();
        if (Child is { } c) q = q.Where(a => a.ChildId == c);
        Alerts = await q.OrderByDescending(a => a.TimeUtc).Take(300).ToListAsync();
        UnreadTotal = await db.Alerts.CountAsync(a => a.ReadUtc == null);
    }

    public async Task<IActionResult> OnPostReadAllAsync()
    {
        var now = clock.GetUtcNow();
        foreach (var a in await db.Alerts.Where(a => a.ReadUtc == null).ToListAsync()) a.ReadUtc = now;
        await db.SaveChangesAsync();
        TempData["Message"] = "Alle Alarme sind als gelesen markiert.";
        return RedirectToPage(new { Child });
    }

    public async Task<IActionResult> OnPostDeleteAsync(Guid id)
    {
        var a = await db.Alerts.FirstOrDefaultAsync(x => x.Id == id);
        if (a is null) return NotFound();
        db.Alerts.Remove(a);
        await db.SaveChangesAsync();
        TempData["Message"] = "Alarm gelöscht.";
        return RedirectToPage(new { Child });
    }
}
