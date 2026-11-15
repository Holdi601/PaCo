using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;
using PaCo.Server.Data;
using PaCo.Server.Services;
using PaCo.Shared;

namespace PaCo.Server.Pages.Admin;

public sealed record DeviceHealth(Device Device, string Family, int Errors24h, IReadOnlyList<DeviceEventEntity> RecentErrors);

/// <summary>
/// Technical overview for the server operator: versions, last contact and technical errors of all devices.
/// Deliberately no children, usage, alarms, screenshots or locations.
/// </summary>
public sealed class HealthModel(PaCoDb db, ClientPackage client, TimeProvider clock) : PageModel
{
    private static readonly DeviceEventKind[] Technical = [DeviceEventKind.ClientError, DeviceEventKind.AgentCrashed, DeviceEventKind.AgentFailure, DeviceEventKind.PolicyRejected];

    public IReadOnlyList<DeviceHealth> Devices { get; private set; } = [];

    public DateTimeOffset Now { get; private set; }

    public string? CurrentVersion => client.Version;

    public async Task OnGetAsync()
    {
        Now = clock.GetUtcNow();
        var families = await db.Families.ToDictionaryAsync(f => f.Id, f => f.Name);
        var devices = await db.Devices.IgnoreQueryFilters().Where(d => !d.Revoked).OrderBy(d => d.Name).ToListAsync();
        var since = Now.AddDays(-7);
        var errors = await db.DeviceEvents.IgnoreQueryFilters()
            .Where(e => e.TimeUtc > since && Technical.Contains(e.Kind)).OrderByDescending(e => e.TimeUtc).ToListAsync();
        var dayAgo = Now.AddDays(-1);
        Devices = devices.Select(d => new DeviceHealth(d, families.GetValueOrDefault(d.FamilyId, "?"),
                errors.Count(e => e.DeviceId == d.Id && e.TimeUtc > dayAgo),
                errors.Where(e => e.DeviceId == d.Id).Take(15).ToList()))
            .OrderByDescending(h => h.Errors24h).ThenBy(h => h.Family).ToList();
    }
}
