using Microsoft.EntityFrameworkCore;
using PaCo.Server.Data;

namespace PaCo.Server.Services;

/// <summary>Small per-request lookups for the layout (navigation badges).</summary>
public sealed class FamilyInfo(PaCoDb db, IFamilyContext family)
{
    public async Task<int> UnreadAlertsAsync() =>
        family.FamilyId is null ? 0 : await db.Alerts.CountAsync(a => a.ReadUtc == null);
}
