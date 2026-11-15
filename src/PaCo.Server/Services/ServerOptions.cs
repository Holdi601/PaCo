namespace PaCo.Server.Services;

public sealed class ServerOptions
{
    public const string Section = "PaCo";

    /// <summary>Directory for database, signing key and data-protection keys.</summary>
    public string DataDir { get; set; } = "paco-data";

    /// <summary>Public base URL as seen from the children's PCs, e.g. https://example.duckdns.org:8790.</summary>
    public string? PublicUrl { get; set; }

    /// <summary>Path of the client installer (PaCo-Setup.exe) offered for download.</summary>
    public string ClientPath { get; set; } = "client/PaCo-Setup.exe";

    /// <summary>PEM certificate (full chain) for HTTPS. Empty = use ASP.NET Core defaults (development).</summary>
    public string? TlsCertPath { get; set; }

    public string? TlsKeyPath { get; set; }

    /// <summary>HTTPS port when <see cref="TlsCertPath"/> is set.</summary>
    public int HttpsPort { get; set; } = 8790;

    /// <summary>ntfy base URL PaCo publishes to (e.g. http://ntfy inside the Docker network). Empty = no push.</summary>
    public string? NtfyUrl { get; set; }

    /// <summary>ntfy URL parents subscribe to from their phones (e.g. https://example.duckdns.org:8792).</summary>
    public string? NtfyPublicUrl { get; set; }

    /// <summary>ntfy access token with write permission (PaCo is the only publisher).</summary>
    public string? NtfyToken { get; set; }

    /// <summary>MLS-compatible Wi-Fi geolocation endpoint (default: BeaconDB). Empty = disabled.</summary>
    public string? WifiGeolocationUrl { get; set; } = "https://api.beacondb.net/v1/geolocate";

    /// <summary>Optional Google Geolocation API key – used instead of BeaconDB when set.</summary>
    public string? GoogleGeolocationApiKey { get; set; }

    /// <summary>GitHub repository ("owner/name") whose releases provide client updates.</summary>
    public string UpdateRepository { get; set; } = "Holdi601/PaCo";

    /// <summary>Requests per IP and 5 minutes on sign-in pages (brute-force protection).</summary>
    public int AccountRateLimit { get; set; } = 30;
}
