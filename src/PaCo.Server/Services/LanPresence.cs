using System.Security.Cryptography;
using System.Text;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using PaCo.Server.Data;
using PaCo.Shared;
using PaCo.Shared.I18n;

namespace PaCo.Server.Services;

/// <summary>
/// Optional: a small watcher container next to the server (deploy/lan-watch) reports every minute which hardware
/// addresses it sees in the server's own network. If a PC is visibly on there for a while but PaCo on it stays silent,
/// PaCo was probably switched off (or an adult started another operating system) – the parents get one push message.
/// A PC whose last message was "going to sleep" is ignored: some network cards keep answering while the PC sleeps.
/// Only addresses are processed; nothing is stored about other devices in the network.
/// </summary>
public static class LanPresence
{
    public const int MaxMacsPerDevice = 8;
    public const int MaxSeenPerReport = 4096;
    public static readonly TimeSpan MacLifetime = TimeSpan.FromDays(30);

    /// <summary>Gap in the watcher's sightings after which a new presence period starts.</summary>
    public static readonly TimeSpan PresenceGap = TimeSpan.FromMinutes(5);

    // i18n-keys:start shared
    public const string LanEvent = "Im Heimnetz eingeschaltet, aber PaCo meldet sich seit {0} nicht – die Eltern wurden benachrichtigt.";
    // i18n-keys:end

    // i18n-keys:start server
    private const string LanTitle = "PaCo: {0} ist an, meldet sich aber nicht";
    private const string LanText = "{0} ist im Heimnetz eingeschaltet, aber PaCo meldet sich seit {1} nicht. Benutzt gerade ein Erwachsener den PC (z. B. mit einem anderen Betriebssystem)? Sonst wurde PaCo vielleicht abgeschaltet – bitte nachsehen.";
    // i18n-keys:end

    public sealed record SeenReport(IReadOnlyList<string>? Macs);

    public static void Map(WebApplication app) =>
        app.MapPost(ApiRoutes.LanSeen, ReportAsync).RequireRateLimiting(Api.DeviceApi.RateLimitDevice).DisableAntiforgery()
            .WithMetadata(new Microsoft.AspNetCore.Mvc.RequestSizeLimitAttribute(256 * 1024));

    private static async Task<IResult> ReportAsync(HttpContext http, SeenReport report, PaCoDb db, Notifier notifier, IOptions<ServerOptions> options,
        TimeProvider clock, CancellationToken ct)
    {
        var o = options.Value;
        if (!o.LanWatchEnabled) return Results.NotFound();
        var header = http.Request.Headers.Authorization.ToString();
        var expected = "Bearer " + o.LanWatchToken!.Trim();
        if (!CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(header), Encoding.UTF8.GetBytes(expected))) return Results.Unauthorized();
        var seen = (report.Macs ?? []).Take(MaxSeenPerReport).Select(Normalize).OfType<string>().ToHashSet();
        var alerts = await ProcessAsync(db, notifier, o, seen, clock.GetUtcNow(), ct);
        return Results.Ok(new { matched = alerts.Matched, alerts = alerts.Alerts });
    }

    /// <summary>"aa:bb:cc:dd:ee:ff" / "AA-BB-…" / "AABBCC…" → "AABBCCDDEEFF"; null for anything else.</summary>
    public static string? Normalize(string? mac)
    {
        if (mac is null) return null;
        var hex = new string(mac.Where(Uri.IsHexDigit).ToArray()).ToUpperInvariant();
        if (hex.Length != 12 || mac.Length > 17 || hex is "000000000000" or "FFFFFFFFFFFF") return null;
        // Multicast addresses never belong to a network card.
        return (Convert.ToByte(hex[..2], 16) & 1) == 1 ? null : hex;
    }

    /// <summary>Stores the addresses a PC reported at its sync (merged with recent ones, at most 8).</summary>
    public static void RecordMacs(Device d, IReadOnlyList<string>? macs, DateTimeOffset now)
    {
        if (macs is null) return;
        var map = Macs(d);
        foreach (var m in macs.Take(MaxMacsPerDevice).Select(Normalize).OfType<string>()) map[m] = now.ToUnixTimeSeconds();
        var cutoff = (now - MacLifetime).ToUnixTimeSeconds();
        d.LanMacsJson = PaCoJson.Serialize(map.Where(kv => kv.Value >= cutoff).OrderByDescending(kv => kv.Value).Take(MaxMacsPerDevice)
            .ToDictionary(kv => kv.Key, kv => kv.Value));
    }

    public static Dictionary<string, long> Macs(Device d) =>
        string.IsNullOrEmpty(d.LanMacsJson) ? [] : PaCoJson.Deserialize<Dictionary<string, long>>(d.LanMacsJson) ?? [];

    public static async Task<(int Matched, int Alerts)> ProcessAsync(PaCoDb db, Notifier notifier, ServerOptions o, IReadOnlySet<string> seen,
        DateTimeOffset now, CancellationToken ct)
    {
        var devices = await db.Devices.IgnoreQueryFilters().Where(d => !d.Revoked && d.UninstalledUtc == null && d.LanMacsJson != "{}").ToListAsync(ct);
        var threshold = TimeSpan.FromMinutes(Math.Clamp(o.LanSilentMinutes, 1, 24 * 60));
        int matched = 0, alerts = 0;
        foreach (var d in devices)
        {
            if (!Macs(d).Keys.Any(seen.Contains)) continue;
            matched++;
            if (d.LanSeenUtc is not { } lastSeen || now - lastSeen > PresenceGap) d.LanSinceUtc = now;
            d.LanSeenUtc = now;
            if (!ShouldAlert(d, now, threshold)) continue;

            d.LanAlertSentUtc = now;
            alerts++;
            var fam = await db.Families.IgnoreQueryFilters().SingleAsync(f => f.Id == d.FamilyId, ct);
            var last = d.LastSeenUtc!.Value;
            db.DeviceEvents.Add(SilentDevices.Event(d, now, DeviceEventKind.Silent, LanEvent, SilentDevices.Stamp(last, fam)));
            if (fam.NtfyTopic is { } topic)
            {
                var lang = await AlertTexts.PushLanguageAsync(db, d.FamilyId, ct);
                var baseUrl = o.PublicUrl?.TrimEnd('/');
                notifier.Enqueue(new PushMessage(topic, Ui.In(lang, LanTitle, d.Name), Ui.In(lang, LanText, d.Name, SilentDevices.Local(last, fam, lang)),
                    4, baseUrl is null ? null : $"{baseUrl}/Devices", "warning"));
            }
        }
        if (matched > 0) await db.SaveChangesAsync(ct);
        return (matched, alerts);
    }

    /// <summary>Visible in the network for the whole threshold, PaCo silent just as long, and not asleep.</summary>
    public static bool ShouldAlert(Device d, DateTimeOffset now, TimeSpan threshold) =>
        d.LanAlert && d.LanAlertSentUtc is null
        && d.LanSinceUtc is { } since && now - since >= threshold
        && d.LastSeenUtc is { } last && now - last >= threshold
        && !(d.SuspendedUtc is { } sleep && sleep >= last);
}
