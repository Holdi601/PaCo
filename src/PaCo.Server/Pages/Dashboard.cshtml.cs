using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;
using PaCo.Server.Data;
using PaCo.Server.Services;
using PaCo.Shared;

namespace PaCo.Server.Pages;

public sealed record Warning(string Level, string Text, string? Link = null);

public sealed class DashboardModel(ParentActions actions, PaCoDb db, TimeProvider clock) : PageModel
{
    public IReadOnlyList<ChildStatus> Children { get; private set; } = [];

    public IReadOnlyList<Warning> Warnings { get; private set; } = [];

    public string FamilyName { get; private set; } = "";

    public DateTimeOffset Now { get; private set; }

    public IReadOnlyDictionary<Guid, int> UnreadAlerts { get; private set; } = new Dictionary<Guid, int>();

    /// <summary>Unlock code just generated (shown once).</summary>
    [TempData]
    public string? NewCode { get; set; }

    [TempData]
    public string? NewCodeInfo { get; set; }

    public async Task OnGetAsync()
    {
        Now = clock.GetUtcNow();
        FamilyName = (await actions.GetFamilyAsync()).Name;
        Children = await actions.StatusAsync();
        Warnings = await WarningsAsync();
        UnreadAlerts = (await db.Alerts.Where(a => a.ReadUtc == null).Select(a => a.ChildId).ToListAsync())
            .GroupBy(x => x).ToDictionary(g => g.Key, g => g.Count());
    }

    public async Task<IActionResult> OnPostGrantAsync(Guid child, int minutes)
    {
        await actions.GrantAsync(child, minutes, User);
        var name = (await actions.FindChildAsync(child))?.Name;
        TempData["Message"] = minutes == 0 ? $"{name} darf heute unbegrenzt." : $"{name} hat {minutes} Minuten mehr bekommen. Ist der PC online, gilt das in wenigen Sekunden.";
        return RedirectToPage();
    }

    public async Task<IActionResult> OnPostRevokeGrantsAsync(Guid child)
    {
        await actions.RevokeTodayGrantsAsync(child);
        TempData["Message"] = "Zusatzzeit für heute zurückgenommen.";
        return RedirectToPage();
    }

    public async Task<IActionResult> OnPostLockAsync(Guid child, bool locked)
    {
        await actions.SetLockedAsync(child, locked);
        var name = (await actions.FindChildAsync(child))?.Name;
        TempData["Message"] = locked ? $"{name} ist jetzt gesperrt." : $"Sperre für {name} aufgehoben.";
        return RedirectToPage();
    }

    public async Task<IActionResult> OnPostLockAllAsync(bool locked)
    {
        foreach (var c in await db.Children.ToListAsync()) c.LockedByParent = locked;
        await db.SaveChangesAsync();
        TempData["Message"] = locked ? "Alle Kinder sind jetzt gesperrt." : "Alle Sperren aufgehoben.";
        return RedirectToPage();
    }

    public async Task<IActionResult> OnPostScreenshotAsync(Guid child)
    {
        var c = await actions.FindChildAsync(child);
        if (c is null) return NotFound();
        var shot = new ScreenshotEntity
        {
            FamilyId = actions.FamilyId, ChildId = c.Id, RequestedBy = ParentActions.UserId(User),
            RequestedUtc = clock.GetUtcNow(), Status = ScreenshotStatus.Pending,
        };
        db.Screenshots.Add(shot);
        await db.SaveChangesAsync();
        return RedirectToPage("/Children/Screenshot", new { id = shot.Id });
    }

    public async Task<IActionResult> OnPostCodeAsync(Guid child, int minutes)
    {
        var c = await actions.FindChildAsync(child);
        if (c is null) return NotFound();
        NewCode = await actions.CreateUnlockCodeAsync(child, minutes, User);
        NewCodeInfo = $"Freischaltcode für {c.Name}: {Format.Grant(minutes)}. Gilt nur heute und nur einmal – funktioniert auch ohne Internet.";
        return RedirectToPage();
    }

    private async Task<IReadOnlyList<Warning>> WarningsAsync()
    {
        var list = new List<Warning>();
        var devices = await db.Devices.Where(d => !d.Revoked).ToListAsync();
        var adminKids = await db.DeviceAccounts.Include(a => a.Child).Include(a => a.Device)
            .Where(a => a.ChildId != null && a.IsAdmin).ToListAsync();
        foreach (var a in adminKids)
            list.Add(new Warning("danger",
                $"Das Windows-Konto „{a.UserName}“ ({a.Child?.Name}) auf {a.Device?.Name} hat Administratorrechte – damit könnte PaCo umgangen werden. Bitte in ein Standardkonto umwandeln.",
                "/Devices/Index"));
        foreach (var d in devices.Where(d => d.LastSeenUtc is { } s && Now - s > TimeSpan.FromDays(2)))
            list.Add(new Warning("warning", $"{d.Name} hat sich {Format.Ago(d.LastSeenUtc, Now)} zuletzt gemeldet. Ist der PC aus, ist alles ok – sonst bitte prüfen.", "/Devices/Index"));
        foreach (var d in devices)
            foreach (var x in d.GetSecurity().Where(x => x.Severity == SecuritySeverity.Critical && !x.Code.StartsWith("child-admin")))
                list.Add(new Warning("danger", $"{d.Name}: {x.Text}", "/Devices/Index"));
        foreach (var d in devices.Where(d => Math.Abs(d.ClockSkewSeconds) > 600))
            list.Add(new Warning("warning", $"Die Uhr von {d.Name} geht {Math.Abs(d.ClockSkewSeconds) / 60} Minuten falsch."));

        var since = Now.AddDays(-3);
        var serious = new[] { DeviceEventKind.ClockTampering, DeviceEventKind.PolicyRejected, DeviceEventKind.AgentFailure, DeviceEventKind.Uninstalled };
        var events = await db.DeviceEvents.Where(e => e.TimeUtc > since && serious.Contains(e.Kind)).OrderByDescending(e => e.TimeUtc).Take(5).ToListAsync();
        foreach (var e in events)
            list.Add(new Warning("warning", $"{Format.Ago(e.TimeUtc, Now)}: {e.Message}", "/Devices/Index"));
        return list;
    }
}
