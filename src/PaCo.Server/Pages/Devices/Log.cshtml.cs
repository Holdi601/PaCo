using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;
using PaCo.Server.Data;
using PaCo.Server.Services;
using PaCo.Shared;

namespace PaCo.Server.Pages.Devices;

/// <summary>Event log of one device (incl. bypass attempts and technical errors) and the client's diagnostic log.</summary>
public sealed class LogModel(PaCoDb db, ParentActions actions, IDataProtectionProvider dp, TimeProvider clock) : PageModel
{
    public Device Device { get; private set; } = null!;

    public IReadOnlyList<DeviceEventEntity> Events { get; private set; } = [];

    public IReadOnlyDictionary<Guid, string> ChildNames { get; private set; } = new Dictionary<Guid, string>();

    public string? DiagnosticsText { get; private set; }

    public DateTimeOffset? DiagnosticsTime { get; private set; }

    public TimeZoneInfo Tz { get; private set; } = TimeZoneInfo.Utc;

    public DateTimeOffset Now { get; private set; }

    [BindProperty(SupportsGet = true)]
    public string Filter { get; set; } = "all";

    public bool Waiting => Device.DiagnosticsRequestedUtc is { } r && Now - r < TimeSpan.FromMinutes(10);

    public async Task<IActionResult> OnGetAsync(Guid id)
    {
        var d = await db.Devices.FirstOrDefaultAsync(x => x.Id == id);
        if (d is null) return NotFound();
        Device = d;
        Now = clock.GetUtcNow();
        (Tz, _, _) = await actions.FamilyNowAsync();
        ChildNames = await db.Children.ToDictionaryAsync(c => c.Id, c => c.Name);
        var q = db.DeviceEvents.Where(e => e.DeviceId == id);
        q = Filter switch
        {
            "suspicious" => q.Where(e => Format.Suspicious.Contains(e.Kind)),
            "errors" => q.Where(e => e.Kind == DeviceEventKind.ClientError || e.Kind == DeviceEventKind.AgentCrashed || e.Kind == DeviceEventKind.AgentFailure),
            _ => q,
        };
        Events = await q.OrderByDescending(e => e.TimeUtc).Take(300).ToListAsync();
        var diag = await db.Diagnostics.Where(x => x.DeviceId == id).OrderByDescending(x => x.TimeUtc).FirstOrDefaultAsync();
        if (diag is not null)
        {
            DiagnosticsTime = diag.TimeUtc;
            try { DiagnosticsText = dp.CreateProtector("PaCo.Diagnostics.v1").Unprotect(diag.ProtectedText); }
            catch (System.Security.Cryptography.CryptographicException) { DiagnosticsText = Ui.T("(nicht lesbar)"); }
        }
        return Page();
    }

    public async Task<IActionResult> OnPostRequestAsync(Guid id)
    {
        var d = await db.Devices.FirstOrDefaultAsync(x => x.Id == id && !x.Revoked);
        if (d is null) return NotFound();
        d.DiagnosticsRequestedUtc = clock.GetUtcNow();
        await db.SaveChangesAsync();
        return RedirectToPage(new { id, Filter });
    }
}
