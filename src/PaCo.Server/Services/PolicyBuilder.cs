using Microsoft.EntityFrameworkCore;
using PaCo.Server.Data;
using PaCo.Shared;
using PaCo.Shared.I18n;

namespace PaCo.Server.Services;

/// <summary>Builds the signed rule set for a device and aggregates usage across devices.</summary>
public sealed class PolicyBuilder(PaCoDb db, SigningKeyStore keys, TimeProvider clock)
{
    public async Task<PolicyDocument> BuildAsync(Device device, CancellationToken ct)
    {
        var family = await db.Families.SingleAsync(f => f.Id == device.FamilyId, ct);
        var tz = FamilyClock.Resolve(family.TimeZoneId);
        var now = clock.GetUtcNow();
        var today = FamilyClock.Day(now, tz);
        var days = new[] { today.AddDays(-1), today };

        var accounts = await db.DeviceAccounts.IgnoreQueryFilters()
            .Where(a => a.DeviceId == device.Id && a.FamilyId == device.FamilyId && a.ChildId != null)
            .ToListAsync(ct);
        var childIds = accounts.Select(a => a.ChildId!.Value).Distinct().ToList();

        var children = await db.Children.IgnoreQueryFilters()
            .Where(c => c.FamilyId == device.FamilyId && childIds.Contains(c.Id))
            .ToListAsync(ct);
        var grants = await db.TimeGrants.IgnoreQueryFilters()
            .Where(g => g.FamilyId == device.FamilyId && childIds.Contains(g.ChildId) && days.Contains(g.Day))
            .ToListAsync(ct);
        var otherUsage = await db.UsageDays.IgnoreQueryFilters()
            .Where(u => u.FamilyId == device.FamilyId && u.DeviceId != device.Id && childIds.Contains(u.ChildId) && days.Contains(u.Day))
            .ToListAsync(ct);

        var childPolicies = children.Select(c =>
        {
            var mine = otherUsage.Where(u => u.ChildId == c.Id).ToList();
            var grantUse = mine.SelectMany(u => u.GetGrantSeconds()).GroupBy(kv => kv.Key).ToDictionary(g => g.Key, g => g.Sum(kv => kv.Value));
            return new ChildPolicy
            {
                ChildId = c.Id,
                Name = c.Name,
                Paused = c.Paused,
                LockedByParent = c.LockedByParent,
                ExpiryAction = c.ExpiryAction,
                Days = c.GetDays(),
                BlockedApps = c.BlockSystemTools ? c.GetBlockedApps().Concat(Child.SystemTools).Distinct().ToList() : c.GetBlockedApps(),
                Web = c.GetWeb(),
                Language = Languages.IsSupported(c.Language) ? c.Language : Languages.Source,
                Alerts = WithLanguages(c.GetAlerts(), c.Language),
                NotifyChildOnScreenshot = c.NotifyChildOnScreenshot,
                Grants = grants.Where(g => g.ChildId == c.Id).Select(g => new TimeGrant
                {
                    Id = g.Id,
                    Day = g.Day,
                    Minutes = g.Minutes,
                    ConsumedSecondsElsewhere = grantUse.GetValueOrDefault(g.Id),
                }).ToList(),
                RegularSecondsElsewhere = mine.GroupBy(u => u.Day).ToDictionary(g => g.Key, g => g.Sum(u => u.RegularSeconds)),
            };
        }).ToList();

        return new PolicyDocument
        {
            DeviceId = device.Id,
            IssuedUtc = now,
            TimeZoneId = family.TimeZoneId,
            Revoked = device.Revoked,
            Accounts = accounts.Select(a => new AccountMapping { Sid = a.Sid, ChildId = a.ChildId!.Value }).ToList(),
            Children = childPolicies,
        };
    }

    public async Task<SignedEnvelope> BuildSignedAsync(Device device, CancellationToken ct) => keys.Sign(await BuildAsync(device, ct));

    /// <summary>Usage of a child for a day summed over all devices (for the parents' dashboard).</summary>
    public static DayUsage Aggregate(IEnumerable<UsageDay> rows)
    {
        var total = new DayUsage();
        foreach (var r in rows)
        {
            total.RegularSeconds += r.RegularSeconds;
            foreach (var (id, s) in r.GetGrantSeconds())
                total.GrantSeconds[id] = total.GrantSeconds.GetValueOrDefault(id) + s;
            total.Codes.AddRange(r.GetCodes());
            foreach (var (proc, app) in r.GetApps())
            {
                if (!total.Apps.TryGetValue(proc, out var t))
                    total.Apps[proc] = t = new AppUsage { DisplayName = app.DisplayName };
                t.Seconds += app.Seconds;
            }
        }
        return total;
    }

    /// <summary>Policy view of a child for evaluating status on the website (no other-device split needed).</summary>
    public static ChildPolicy ToPolicy(Child c, IEnumerable<TimeGrantEntity> grants) => new()
    {
        ChildId = c.Id,
        Name = c.Name,
        Paused = c.Paused,
        LockedByParent = c.LockedByParent,
        ExpiryAction = c.ExpiryAction,
        Days = c.GetDays(),
        Grants = grants.Where(g => g.ChildId == c.Id).Select(g => new TimeGrant { Id = g.Id, Day = g.Day, Minutes = g.Minutes }).ToList(),
    };

    /// <summary>Alarm keyword lists: the child's language and English are always checked, plus any the parents added.</summary>
    private static AlertSettings WithLanguages(AlertSettings a, string childLang) =>
        a with { Languages = a.Languages.Append(Languages.IsSupported(childLang) ? childLang : Languages.Source).Append("en").Where(Languages.IsSupported).Distinct().ToList() };
}
