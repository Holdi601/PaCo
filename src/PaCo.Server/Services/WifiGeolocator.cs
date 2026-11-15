using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.Options;
using PaCo.Shared;

namespace PaCo.Server.Services;

/// <summary>
/// Looks up a position from the Wi-Fi access points around a device in a world Wi-Fi map: BeaconDB (open,
/// Mozilla-Location-Service compatible) by default, or Google's Geolocation API if a key is configured.
/// IP fallback is disabled explicitly – the request comes from this server, so an IP-based answer would be the
/// server's location, not the device's.
/// </summary>
public sealed class WifiGeolocator(IHttpClientFactory http, IOptions<ServerOptions> options, ILogger<WifiGeolocator> log)
{
    public sealed record Fix(double Latitude, double Longitude, double AccuracyMeters, string Source);

    public bool Enabled => !string.IsNullOrWhiteSpace(options.Value.GoogleGeolocationApiKey) || !string.IsNullOrWhiteSpace(options.Value.WifiGeolocationUrl);

    public async Task<Fix?> LocateAsync(IReadOnlyList<WifiNetwork> networks, CancellationToken ct)
    {
        // "_nomap" SSIDs opted out of Wi-Fi mapping; locally administered MACs (random/hotspot) are useless.
        var aps = networks
            .Where(n => !(n.Ssid ?? "").EndsWith("_nomap", StringComparison.OrdinalIgnoreCase))
            .Where(n => n.Bssid.Length == 17 && (Convert.ToByte(n.Bssid[..2], 16) & 0x02) == 0)
            .OrderByDescending(n => n.Rssi).Take(20).ToList();
        if (aps.Count < 2 || !Enabled) return null;

        try
        {
            using var client = http.CreateClient("wifi-geo");
            client.DefaultRequestHeaders.UserAgent.ParseAdd("PaCo-Server/1.4 (+https://github.com/Holdi601/PaCo)");
            HttpResponseMessage resp;
            string source;
            if (!string.IsNullOrWhiteSpace(options.Value.GoogleGeolocationApiKey))
            {
                source = "WLAN-Karte (Google)";
                resp = await client.PostAsJsonAsync($"https://www.googleapis.com/geolocation/v1/geolocate?key={options.Value.GoogleGeolocationApiKey}", new
                {
                    considerIp = false,
                    wifiAccessPoints = aps.Select(a => new { macAddress = a.Bssid, signalStrength = a.Rssi }),
                }, ct);
            }
            else
            {
                source = "WLAN-Karte (BeaconDB)";
                resp = await client.PostAsJsonAsync(options.Value.WifiGeolocationUrl, new
                {
                    considerIp = false,
                    fallbacks = new { lacf = false, ipf = false },
                    wifiAccessPoints = aps.Select(a => new { macAddress = a.Bssid, signalStrength = a.Rssi, frequency = a.FrequencyMhz }),
                }, ct);
            }
            using (resp)
            {
                if (!resp.IsSuccessStatusCode)
                {
                    log.LogInformation("WLAN-Ortung ohne Ergebnis ({Status})", (int)resp.StatusCode);
                    return null;
                }
                using var doc = await JsonDocument.ParseAsync(await resp.Content.ReadAsStreamAsync(ct), cancellationToken: ct);
                var loc = doc.RootElement.GetProperty("location");
                var fix = new Fix(loc.GetProperty("lat").GetDouble(), loc.GetProperty("lng").GetDouble(), doc.RootElement.GetProperty("accuracy").GetDouble(), source);
                log.LogInformation("WLAN-Ortung über {Source}: ±{Accuracy:F0} m aus {Count} WLANs", source, fix.AccuracyMeters, aps.Count);
                return fix;
            }
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException or KeyNotFoundException or InvalidOperationException)
        {
            log.LogWarning("WLAN-Ortung fehlgeschlagen: {Message}", ex.Message);
            return null;
        }
    }
}
