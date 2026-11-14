using System.Security.Cryptography;
using Microsoft.EntityFrameworkCore;
using PaCo.Server.Data;
using PaCo.Server.Services;
using PaCo.Shared;

namespace PaCo.Server.Api;

/// <summary>
/// HTTP API used by the Windows client. Runs without a parent family context: every query filters by the
/// authenticated device's family explicitly (IgnoreQueryFilters + FamilyId == device.FamilyId).
/// </summary>
public static class DeviceApi
{
    public const string RateLimitPair = "pair";
    public const string RateLimitDevice = "device";

    private const int MaxEventsPerSync = 200;
    private const int MaxAppsPerDay = 300;

    public static void Map(WebApplication app)
    {
        app.MapPost(ApiRoutes.Pair, PairAsync).RequireRateLimiting(RateLimitPair).DisableAntiforgery();
        app.MapPost(ApiRoutes.Assign, AssignAsync).RequireRateLimiting(RateLimitDevice).DisableAntiforgery();
        app.MapPost(ApiRoutes.Sync, SyncAsync).RequireRateLimiting(RateLimitDevice).DisableAntiforgery();
        app.MapGet(ApiRoutes.ClientDownload, DownloadClientAsync).RequireRateLimiting(RateLimitPair);
    }

    private static async Task<IResult> PairAsync(PairRequest req, PaCoDb db, SigningKeyStore keys, TimeProvider clock, ILoggerFactory lf, CancellationToken ct)
    {
        var log = lf.CreateLogger("DeviceApi");
        var hash = Secrets.Sha256Hex(Secrets.NormalizeCode(req.PairingCode));
        var now = clock.GetUtcNow();
        var code = await db.PairingCodes.IgnoreQueryFilters().SingleOrDefaultAsync(p => p.CodeHash == hash, ct);
        if (code is null || code.UsedUtc is not null || code.ExpiresUtc < now)
        {
            log.LogWarning("Kopplung mit ungültigem/abgelaufenem Code abgelehnt");
            return Results.Problem("Der Kopplungscode ist ungültig, abgelaufen oder wurde schon benutzt. Bitte auf der Website ein neues Gerät hinzufügen.", statusCode: 400);
        }

        var family = await db.Families.SingleAsync(f => f.Id == code.FamilyId, ct);
        var token = Secrets.DeviceToken();
        var device = new Device
        {
            FamilyId = family.Id,
            Name = Trim(req.MachineName, 100),
            MachineName = Trim(req.MachineName, 100),
            TokenHash = Secrets.Sha256Hex(token),
            ClientVersion = Trim(req.ClientVersion, 40),
            OsVersion = Trim(req.OsVersion, 100),
            LastSeenUtc = now,
        };
        db.Devices.Add(device);
        foreach (var acc in req.Accounts.Where(a => !string.IsNullOrWhiteSpace(a.Sid)).DistinctBy(a => a.Sid).Take(50))
        {
            db.DeviceAccounts.Add(new DeviceAccount
            {
                FamilyId = family.Id,
                DeviceId = device.Id,
                Sid = Trim(acc.Sid, 200),
                UserName = Trim(acc.UserName, 100),
                FullName = acc.FullName is null ? null : Trim(acc.FullName, 200),
                IsAdmin = acc.IsAdmin,
                LastSeenUtc = now,
            });
        }
        code.UsedUtc = now;
        await db.SaveChangesAsync(ct);
        log.LogInformation("Gerät {Device} für Familie {Family} gekoppelt", device.Id, family.Id);

        var children = await db.Children.IgnoreQueryFilters().Where(c => c.FamilyId == family.Id).OrderBy(c => c.Name)
            .Select(c => new ChildRef(c.Id, c.Name)).ToListAsync(ct);

        return Results.Ok(new PairResponse
        {
            DeviceId = device.Id,
            DeviceToken = token,
            PolicyPublicKeyPem = keys.PublicKeyPem,
            UnlockKey = Convert.ToBase64String(family.UnlockKey),
            FamilyName = family.Name,
            Children = children,
        });
    }

    private static async Task<IResult> AssignAsync(HttpContext http, AssignRequest req, PaCoDb db, CancellationToken ct)
    {
        var device = await AuthenticateAsync(http, db, ct);
        if (device is null) return Results.Unauthorized();
        if (device.Revoked) return Results.Problem("Gerät wurde entfernt.", statusCode: 403);

        var accounts = await db.DeviceAccounts.IgnoreQueryFilters().Where(a => a.DeviceId == device.Id && a.FamilyId == device.FamilyId).ToListAsync(ct);
        var children = await db.Children.IgnoreQueryFilters().Where(c => c.FamilyId == device.FamilyId).ToListAsync(ct);

        foreach (var a in req.Assignments.Take(50))
        {
            var acc = accounts.FirstOrDefault(x => string.Equals(x.Sid, a.Sid, StringComparison.OrdinalIgnoreCase));
            if (acc is null) continue;

            if (!string.IsNullOrWhiteSpace(a.NewChildName))
            {
                var name = Trim(a.NewChildName.Trim(), 60);
                var child = children.FirstOrDefault(c => string.Equals(c.Name, name, StringComparison.OrdinalIgnoreCase));
                if (child is null)
                {
                    child = new Child { FamilyId = device.FamilyId, Name = name, DayRulesJson = PaCoJson.Serialize(Child.DefaultDays()) };
                    child.SetWeb(new WebFilterPolicy());
                    db.Children.Add(child);
                    children.Add(child);
                }
                acc.ChildId = child.Id;
            }
            else if (a.ChildId is { } cid)
            {
                // Only children of the device's own family can be assigned.
                acc.ChildId = children.Any(c => c.Id == cid) ? cid : acc.ChildId;
            }
            else
            {
                acc.ChildId = null;
            }
        }
        await db.SaveChangesAsync(ct);
        return Results.Ok(children.OrderBy(c => c.Name).Select(c => new ChildRef(c.Id, c.Name)).ToList());
    }

    private static async Task<IResult> SyncAsync(HttpContext http, SyncRequest req, PaCoDb db, PolicyBuilder policies, ClientPackage client, TimeProvider clock, CancellationToken ct)
    {
        var device = await AuthenticateAsync(http, db, ct);
        if (device is null) return Results.Unauthorized();

        var now = clock.GetUtcNow();
        device.LastSeenUtc = now;
        device.ClientVersion = Trim(req.ClientVersion, 40);
        device.OsVersion = Trim(req.OsVersion, 100);
        device.ClockSkewSeconds = (long)(req.ClientUtcNow - now).TotalSeconds;
        device.ActiveChildrenJson = PaCoJson.Serialize(req.ActiveChildren.Take(20).ToArray());
        device.LockedChildrenJson = PaCoJson.Serialize(req.LockedChildren.Take(20).ToArray());
        device.LiveJson = PaCoJson.Serialize(req.Live.Take(20).Select(l => l with { WindowsUser = l.WindowsUser is { Length: > 100 } u ? u[..100] : l.WindowsUser }).ToArray());

        if (!device.Revoked)
        {
            await UpsertAccountsAsync(db, device, req.Accounts, now, ct);
            await UpsertUsageAsync(db, device, req.Usage, now, ct);
            await StoreEventsAsync(db, device, req.Events, ct);
        }
        await db.SaveChangesAsync(ct);

        return Results.Ok(new SyncResponse
        {
            Policy = await policies.BuildSignedAsync(device, ct),
            ServerUtcNow = now,
            LatestClientVersion = client.Version,
        });
    }

    private static async Task<IResult> DownloadClientAsync(HttpContext http, PaCoDb db, ClientPackage client, CancellationToken ct)
    {
        var device = await AuthenticateAsync(http, db, ct);
        if (device is null || device.Revoked) return Results.Unauthorized();
        if (!client.Available) return Results.NotFound();
        return Results.Stream(client.OpenPlain(), "application/octet-stream", "PaCo.exe");
    }

    private static async Task UpsertAccountsAsync(PaCoDb db, Device device, IReadOnlyList<WindowsAccount> reported, DateTimeOffset now, CancellationToken ct)
    {
        if (reported.Count == 0) return;
        var existing = await db.DeviceAccounts.IgnoreQueryFilters().Where(a => a.DeviceId == device.Id && a.FamilyId == device.FamilyId).ToListAsync(ct);
        foreach (var acc in reported.Where(a => !string.IsNullOrWhiteSpace(a.Sid)).DistinctBy(a => a.Sid).Take(50))
        {
            var row = existing.FirstOrDefault(e => string.Equals(e.Sid, acc.Sid, StringComparison.OrdinalIgnoreCase));
            if (row is null)
            {
                row = new DeviceAccount { FamilyId = device.FamilyId, DeviceId = device.Id, Sid = Trim(acc.Sid, 200) };
                db.DeviceAccounts.Add(row);
            }
            row.UserName = Trim(acc.UserName, 100);
            row.FullName = acc.FullName is null ? null : Trim(acc.FullName, 200);
            row.IsAdmin = acc.IsAdmin;
            row.LastSeenUtc = now;
        }
    }

    private static async Task UpsertUsageAsync(PaCoDb db, Device device, IReadOnlyList<UsageReport> usage, DateTimeOffset now, CancellationToken ct)
    {
        if (usage.Count == 0) return;
        var childIds = await db.Children.IgnoreQueryFilters().Where(c => c.FamilyId == device.FamilyId).Select(c => c.Id).ToListAsync(ct);
        var family = await db.Families.SingleAsync(f => f.Id == device.FamilyId, ct);
        var today = FamilyClock.Day(now, FamilyClock.Resolve(family.TimeZoneId));
        // Days in the future can only come from a clock set forward – ignore them (the client merges them back).
        var reports = usage.Where(u => childIds.Contains(u.ChildId) && u.Day <= today && u.Day >= today.AddDays(-14)).Take(100).ToList();
        var days = reports.Select(r => r.Day).Distinct().ToList();
        var rows = await db.UsageDays.IgnoreQueryFilters()
            .Where(u => u.DeviceId == device.Id && u.FamilyId == device.FamilyId && days.Contains(u.Day)).ToListAsync(ct);

        foreach (var r in reports)
        {
            var row = rows.FirstOrDefault(x => x.ChildId == r.ChildId && x.Day == r.Day);
            if (row is null)
            {
                row = new UsageDay { FamilyId = device.FamilyId, DeviceId = device.Id, ChildId = r.ChildId, Day = r.Day };
                db.UsageDays.Add(row);
                rows.Add(row);
            }
            // Totals only ever grow within a day; ignore regressions (e.g. a restored old state file).
            var codes = r.Codes.Take(100).ToList();
            var bonus = r.GrantSeconds.Values.Sum() + codes.Sum(c => c.ConsumedSeconds);
            row.RegularSeconds = Math.Max(row.RegularSeconds, Clamp(r.RegularSeconds));
            row.BonusSeconds = Math.Max(row.BonusSeconds, Clamp(bonus));
            row.GrantSecondsJson = PaCoJson.Serialize(r.GrantSeconds.Take(50).ToDictionary(k => k.Key, v => Clamp(v.Value)));
            row.CodesJson = PaCoJson.Serialize(codes);
            row.AppsJson = PaCoJson.Serialize(r.Apps
                .OrderByDescending(a => a.Value.Seconds).Take(MaxAppsPerDay)
                .ToDictionary(a => Trim(a.Key, 80), a => new AppUsage { DisplayName = Trim(a.Value.DisplayName, 120), Seconds = Clamp(a.Value.Seconds) }));
            row.UpdatedUtc = now;

            foreach (var c in codes)
            {
                var issue = await db.UnlockCodes.IgnoreQueryFilters()
                    .SingleOrDefaultAsync(i => i.FamilyId == device.FamilyId && i.ChildId == r.ChildId && i.Day == r.Day && i.Nonce == c.Nonce, ct);
                if (issue is not null && issue.RedeemedUtc is null) issue.RedeemedUtc = c.RedeemedUtc.ToUniversalTime();
            }
        }
    }

    private static async Task StoreEventsAsync(PaCoDb db, Device device, IReadOnlyList<DeviceEvent> events, CancellationToken ct)
    {
        if (events.Count == 0) return;
        var ids = events.Take(MaxEventsPerSync).Select(e => e.Id).ToList();
        // Idempotent: the client resends events until a sync succeeds.
        var known = await db.DeviceEvents.IgnoreQueryFilters().Where(e => ids.Contains(e.Id)).Select(e => e.Id).ToListAsync(ct);
        foreach (var e in events.Take(MaxEventsPerSync).Where(e => !known.Contains(e.Id)))
        {
            db.DeviceEvents.Add(new DeviceEventEntity
            {
                Id = e.Id,
                FamilyId = device.FamilyId,
                DeviceId = device.Id,
                ChildId = e.ChildId,
                TimeUtc = e.TimeUtc.ToUniversalTime(),
                Kind = e.Kind,
                Message = Trim(e.Message, 500),
            });
        }
    }

    /// <summary>Header: <c>Authorization: Device {deviceId}:{token}</c>.</summary>
    public static async Task<Device?> AuthenticateAsync(HttpContext http, PaCoDb db, CancellationToken ct)
    {
        var header = http.Request.Headers.Authorization.ToString();
        if (!header.StartsWith("Device ", StringComparison.Ordinal)) return null;
        var parts = header["Device ".Length..].Split(':', 2);
        if (parts.Length != 2 || !Guid.TryParse(parts[0], out var id) || parts[1].Length is < 20 or > 100) return null;

        var device = await db.Devices.IgnoreQueryFilters().SingleOrDefaultAsync(d => d.Id == id, ct);
        if (device is null)
        {
            // Burn comparable time so device ids cannot be probed via timing.
            Secrets.FixedTimeEqualsHex(Secrets.Sha256Hex(parts[1]), new string('0', 64));
            return null;
        }
        return Secrets.FixedTimeEqualsHex(Secrets.Sha256Hex(parts[1]), device.TokenHash) ? device : null;
    }

    private static string Trim(string? s, int max) => s is null ? "" : s.Length <= max ? s : s[..max];

    private static long Clamp(long seconds) => Math.Clamp(seconds, 0, 86_400 * 2);
}
