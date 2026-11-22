namespace PaCo.Server.Services;

public sealed class ServerOptions
{
    public const string Section = "PaCo";

    /// <summary>Directory for database, signing key and data-protection keys.</summary>
    public string DataDir { get; set; } = "paco-data";

    /// <summary>Public base URL as seen from the children's PCs, e.g. https://example.duckdns.org:8790.</summary>
    public string? PublicUrl { get; set; }

    /// <summary>
    /// 32 random bytes (Base64) kept OUTSIDE the data directory (deploy/.env). Encrypts the key ring that protects all
    /// secrets; without it a copy of the data directory cannot be decrypted. Keep a copy in a password manager.
    /// </summary>
    public string? MasterKey { get; set; }

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

    /// <summary>
    /// Shared secret of the optional LAN watcher (deploy/lan-watch). Empty = feature off: the endpoint does not exist
    /// and the website hides the setting.
    /// </summary>
    public string? LanWatchToken { get; set; }

    /// <summary>Minutes a PC must be visible in the network while PaCo is silent before the parents are warned.</summary>
    public int LanSilentMinutes { get; set; } = 15;

    public bool LanWatchEnabled => !string.IsNullOrWhiteSpace(LanWatchToken) && LanWatchToken.Trim().Length >= 32;

    /// <summary>Public key (PEM) for release signatures; default: the key built into the server (release-signing.pub).</summary>
    public string? ReleasePublicKey { get; set; }

    public static string BuiltInReleasePublicKey { get; } = ReadBuiltIn();

    private static string ReadBuiltIn()
    {
        using var s = typeof(ServerOptions).Assembly.GetManifestResourceStream("PaCo.Server.release-signing.pub");
        return s is null ? "" : new StreamReader(s).ReadToEnd();
    }

    /// <summary>Requests per IP and 5 minutes on sign-in pages (brute-force protection).</summary>
    public int AccountRateLimit { get; set; } = 30;
}
