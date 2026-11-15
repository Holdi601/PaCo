using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;
using PaCo.Server.Data;
using PaCo.Server.Services;

namespace PaCo.Server.Pages.Children;

/// <summary>
/// Shows a requested screenshot. Every lookup goes through the family-filtered DbContext, so an id of another
/// family is simply "not found"; images are decrypted on the fly and never stored under wwwroot.
/// </summary>
public sealed class ScreenshotModel(PaCoDb db, ScreenshotStore store, ParentActions actions) : PageModel
{
    public ScreenshotEntity Shot { get; private set; } = null!;

    public Child Child { get; private set; } = null!;

    public IReadOnlyList<ScreenshotEntity> History { get; private set; } = [];

    public TimeZoneInfo Tz { get; private set; } = TimeZoneInfo.Utc;

    public async Task<IActionResult> OnGetAsync(Guid id)
    {
        var shot = await db.Screenshots.FirstOrDefaultAsync(s => s.Id == id);
        if (shot is null) return NotFound();
        var child = await db.Children.FirstOrDefaultAsync(c => c.Id == shot.ChildId);
        if (child is null) return NotFound();
        Shot = shot;
        Child = child;
        (Tz, _, _) = await actions.FamilyNowAsync();
        History = await db.Screenshots.Where(s => s.ChildId == shot.ChildId && s.Status == ScreenshotStatus.Done && s.Id != id)
            .OrderByDescending(s => s.RequestedUtc).Take(12).ToListAsync();
        return Page();
    }

    public async Task<IActionResult> OnGetImageAsync(Guid id, CancellationToken ct)
    {
        var shot = await db.Screenshots.FirstOrDefaultAsync(s => s.Id == id && s.Status == ScreenshotStatus.Done, ct);
        if (shot is null) return NotFound();
        var bytes = await store.LoadAsync(shot.FamilyId, shot.Id, ct);
        if (bytes is null) return NotFound();
        Response.Headers.CacheControl = "no-store, private";
        return File(bytes, "image/jpeg");
    }

    public async Task<IActionResult> OnPostDeleteAsync(Guid id)
    {
        var shot = await db.Screenshots.FirstOrDefaultAsync(s => s.Id == id);
        if (shot is null) return NotFound();
        store.Delete(shot.FamilyId, shot.Id);
        db.Screenshots.Remove(shot);
        await db.SaveChangesAsync();
        TempData["Message"] = Ui.T("Bildschirmfoto gelöscht.");
        return RedirectToPage("/Dashboard");
    }
}
