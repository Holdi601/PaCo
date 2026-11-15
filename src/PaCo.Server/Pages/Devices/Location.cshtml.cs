using System.Globalization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;
using PaCo.Server.Data;
using PaCo.Server.Services;
using PaCo.Shared;

namespace PaCo.Server.Pages.Devices;

/// <summary>Location history of one device (family-filtered like everything else).</summary>
public sealed class LocationModel(PaCoDb db, ParentActions actions, TimeProvider clock) : PageModel
{
    public Device Device { get; private set; } = null!;

    public IReadOnlyList<DeviceLocationEntity> Points { get; private set; } = [];

    public IReadOnlyList<DeviceIpEntity> Ips { get; private set; } = [];

    public string MapJson { get; private set; } = "[]";

    public TimeZoneInfo Tz { get; private set; } = TimeZoneInfo.Utc;

    public DateTimeOffset Now { get; private set; }

    [BindProperty(SupportsGet = true)]
    public int Days { get; set; } = 1;

    public bool Waiting => Device.LocateRequestedUtc is { } r && Now - r < TimeSpan.FromMinutes(2);

    /// <summary>Newest point with coordinates (shown on the Google map).</summary>
    public DeviceLocationEntity? Latest => Points.LastOrDefault(p => p.Latitude is not null && p.Longitude is not null);

    /// <summary>Newest point with a Wi-Fi scan.</summary>
    public DeviceLocationEntity? LatestScan => Points.LastOrDefault(p => p.NearbyJson.Length > 2);

    public static string Coord(double v) => v.ToString("F6", CultureInfo.InvariantCulture);

    /// <summary>Google Maps embed without API key (single marker).</summary>
    public static string GoogleEmbed(double lat, double lon) => $"https://maps.google.com/maps?q={Coord(lat)},{Coord(lon)}&z=16&output=embed";

    public static string GoogleLink(double lat, double lon) => $"https://www.google.com/maps/search/?api=1&query={Coord(lat)},{Coord(lon)}";

    public async Task<IActionResult> OnGetAsync(Guid id)
    {
        var d = await db.Devices.FirstOrDefaultAsync(x => x.Id == id);
        if (d is null) return NotFound();
        Device = d;
        Now = clock.GetUtcNow();
        (Tz, _, _) = await actions.FamilyNowAsync();
        Days = Days is 1 or 7 or 30 ? Days : 1;
        var since = Now.AddDays(-Days);
        Points = await db.Locations.Where(l => l.DeviceId == id && l.TimeUtc > since).OrderBy(l => l.TimeUtc).ToListAsync();
        Ips = await db.DeviceIps.Where(i => i.DeviceId == id).OrderByDescending(i => i.LastSeenUtc).Take(20).ToListAsync();
        MapJson = PaCoJson.Serialize(Points.Where(p => p.Latitude is not null && p.Longitude is not null).Select(p => new
        {
            lat = p.Latitude, lon = p.Longitude, acc = p.AccuracyMeters, ssid = p.Ssid,
            time = TimeZoneInfo.ConvertTime(p.TimeUtc, Tz).ToString("dd.MM. HH:mm", CultureInfo.GetCultureInfo("de-DE")),
        }));
        return Page();
    }

    public async Task<IActionResult> OnPostModeAsync(Guid id, LocationMode mode)
    {
        var d = await db.Devices.FirstOrDefaultAsync(x => x.Id == id && !x.Revoked);
        if (d is null) return NotFound();
        d.LocationMode = mode;
        await db.SaveChangesAsync();
        TempData["Message"] = mode switch
        {
            LocationMode.Off => "Standort-Erfassung ausgeschaltet.",
            LocationMode.OnRequest => "Standort wird nur noch auf Anforderung („Jetzt orten“) ermittelt.",
            _ => "Standort wird alle 15 Minuten aufgezeichnet (30 Tage gespeichert).",
        };
        return RedirectToPage(new { id });
    }

    public async Task<IActionResult> OnPostLocateAsync(Guid id)
    {
        var d = await db.Devices.FirstOrDefaultAsync(x => x.Id == id && !x.Revoked);
        if (d is null) return NotFound();
        if (d.LocationMode == LocationMode.Off) d.LocationMode = LocationMode.OnRequest;
        d.LocateRequestedUtc = clock.GetUtcNow();
        await db.SaveChangesAsync();
        return RedirectToPage(new { id });
    }

    public async Task<IActionResult> OnPostClearAsync(Guid id)
    {
        var d = await db.Devices.FirstOrDefaultAsync(x => x.Id == id);
        if (d is null) return NotFound();
        db.Locations.RemoveRange(await db.Locations.Where(l => l.DeviceId == id).ToListAsync());
        await db.SaveChangesAsync();
        TempData["Message"] = "Standortverlauf gelöscht.";
        return RedirectToPage(new { id });
    }

    public static string SourceText(string? s) => s switch
    {
        "WiFi" => "WLAN-Ortung",
        "Satellite" => "GPS",
        "Cellular" => "Mobilfunk",
        "IPAddress" => "IP-Adresse (ungenau)",
        null => "–",
        _ => s,
    };
}
