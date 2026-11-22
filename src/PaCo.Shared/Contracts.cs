namespace PaCo.Shared;

public static class ApiRoutes
{
    public const string Pair = "/api/device/pair";
    public const string Assign = "/api/device/assign";
    public const string Sync = "/api/device/sync";
    public const string ClientDownload = "/api/device/client";
    public const string Screenshot = "/api/device/screenshot";
    public const string Diagnostics = "/api/device/diagnostics";
}

public enum LocationMode
{
    Off,
    /// <summary>Only when a parent presses "Jetzt orten".</summary>
    OnRequest,
    /// <summary>Every 15 minutes, plus on request.</summary>
    Continuous,
}

/// <summary>A location fix of the device (or a failed attempt with whatever is known).</summary>
public sealed record LocationPoint
{
    public Guid Id { get; init; } = Guid.NewGuid();

    public DateTimeOffset TimeUtc { get; init; }

    public double? Latitude { get; init; }

    public double? Longitude { get; init; }

    public double? AccuracyMeters { get; init; }

    /// <summary>Windows position source (WiFi, Satellite, IPAddress, …).</summary>
    public string? Source { get; init; }

    /// <summary>Name of the connected Wi-Fi network.</summary>
    public string? Ssid { get; init; }

    public string? Error { get; init; }

    /// <summary>Wi-Fi access points around the device (for Wi-Fi-map positioning on the server).</summary>
    public IReadOnlyList<WifiNetwork> Nearby { get; init; } = [];
}

public sealed record WifiNetwork
{
    public string Bssid { get; init; } = "";

    public string? Ssid { get; init; }

    /// <summary>Signal strength in dBm.</summary>
    public int Rssi { get; init; }

    public int FrequencyMhz { get; init; }
}

public enum SecuritySeverity
{
    Ok,
    Info,
    Warning,
    Critical,
}

/// <summary>
/// Result of one security check on the child's PC. <see cref="Text"/> is German; <see cref="Template"/> + <see cref="Args"/>
/// let the website show it in the parent's language.
/// </summary>
public sealed record SecurityFinding(string Code, SecuritySeverity Severity, string Text, bool Fixable = false)
{
    public string? Template { get; init; }

    public IReadOnlyList<string> Args { get; init; } = [];
}

/// <summary>A parent asked for a screenshot of the child's screen.</summary>
public sealed record ScreenshotRequest(Guid RequestId, Guid ChildId);

public sealed record WindowsAccount
{
    public string Sid { get; init; } = "";

    public string UserName { get; init; } = "";

    public string? FullName { get; init; }

    public bool IsAdmin { get; init; }
}

public sealed record PairRequest
{
    public string PairingCode { get; init; } = "";

    public string MachineName { get; init; } = "";

    public string OsVersion { get; init; } = "";

    public string ClientVersion { get; init; } = "";

    public IReadOnlyList<WindowsAccount> Accounts { get; init; } = [];
}

public sealed record ChildRef(Guid Id, string Name);

public sealed record PairResponse
{
    public Guid DeviceId { get; init; }

    /// <summary>Secret device token. Only sent once; the server stores a hash.</summary>
    public string DeviceToken { get; init; } = "";

    public string PolicyPublicKeyPem { get; init; } = "";

    /// <summary>Base64 HMAC key for offline unlock codes.</summary>
    public string UnlockKey { get; init; } = "";

    public string FamilyName { get; init; } = "";

    public IReadOnlyList<ChildRef> Children { get; init; } = [];
}

public sealed record AccountAssignment
{
    public string Sid { get; init; } = "";

    /// <summary>Existing child. Null together with <see cref="NewChildName"/> = not monitored.</summary>
    public Guid? ChildId { get; init; }

    /// <summary>Create a new child with this name and map the account to it.</summary>
    public string? NewChildName { get; init; }
}

public sealed record AssignRequest
{
    public IReadOnlyList<AccountAssignment> Assignments { get; init; } = [];
}

public sealed record UsageReport
{
    public Guid ChildId { get; init; }

    public DateOnly Day { get; init; }

    public long RegularSeconds { get; init; }

    public IReadOnlyDictionary<Guid, long> GrantSeconds { get; init; } = new Dictionary<Guid, long>();

    public IReadOnlyList<RedeemedCode> Codes { get; init; } = [];

    public IReadOnlyDictionary<string, AppUsage> Apps { get; init; } = new Dictionary<string, AppUsage>();

    /// <summary>Foreground seconds per time group.</summary>
    public IReadOnlyDictionary<Guid, long> Groups { get; init; } = new Dictionary<Guid, long>();
}

public enum DeviceEventKind
{
    Info,
    ServiceStarted,
    ClockTampering,
    ChildIsAdmin,
    PolicyRejected,
    AgentFailure,
    ForcedLogoff,
    Locked,
    Unlocked,
    UnlockFailed,
    AppBlocked,
    Uninstalled,
    /// <summary>Error from the client log (technical problem).</summary>
    ClientError,
    /// <summary>The PC was started in safe mode.</summary>
    SafeMode,
    /// <summary>The PC was used while it had no connection to the server.</summary>
    OfflineUsage,
    /// <summary>The session agent ended unexpectedly.</summary>
    AgentCrashed,
    /// <summary>Server-side: the PC did not contact the server for as long as the parents allow.</summary>
    Silent,
}

public sealed record DeviceEvent
{
    public Guid Id { get; init; } = Guid.NewGuid();

    public DateTimeOffset TimeUtc { get; init; }

    public DeviceEventKind Kind { get; init; }

    public Guid? ChildId { get; init; }

    /// <summary>German text (also used when no template is available).</summary>
    public string Message { get; init; } = "";

    /// <summary>German composite-format template of <see cref="Message"/> – translated on the website.</summary>
    public string? Template { get; init; }

    public IReadOnlyList<string> Args { get; init; } = [];
}

public sealed record SyncRequest
{
    /// <summary>Random value per sync; the server signs it together with its time (<see cref="SignedServerTime"/>).</summary>
    public string? TimeNonce { get; init; }

    public string ClientVersion { get; init; } = "";

    public string OsVersion { get; init; } = "";

    public DateTimeOffset ClientUtcNow { get; init; }

    public IReadOnlyList<WindowsAccount> Accounts { get; init; } = [];

    /// <summary>Children currently signed in and using the PC (unlocked session).</summary>
    public IReadOnlyList<Guid> ActiveChildren { get; init; } = [];

    /// <summary>Children currently shown the lock screen.</summary>
    public IReadOnlyList<Guid> LockedChildren { get; init; } = [];

    /// <summary>Live status per monitored child as computed on the device (includes offline codes).</summary>
    public IReadOnlyList<ChildLiveStatus> Live { get; init; } = [];

    /// <summary>Latest security check of the PC (sent about once a day; null = unchanged).</summary>
    public IReadOnlyList<SecurityFinding>? Security { get; init; }

    /// <summary>Location fixes since the last successful sync.</summary>
    public IReadOnlyList<LocationPoint> Locations { get; init; } = [];

    /// <summary>Alarm hits since the last successful sync.</summary>
    public IReadOnlyList<AlertReport> Alerts { get; init; } = [];

    /// <summary>Complete per-day totals for this device (idempotent: server overwrites).</summary>
    public IReadOnlyList<UsageReport> Usage { get; init; } = [];

    public IReadOnlyList<DeviceEvent> Events { get; init; } = [];
}

public sealed record ChildLiveStatus
{
    public Guid ChildId { get; init; }

    public bool Allowed { get; init; }

    /// <summary>Null = unlimited.</summary>
    public long? RemainingSeconds { get; init; }

    public BlockReason Reason { get; init; }

    public long UsedTodaySeconds { get; init; }

    public bool Active { get; init; }

    public bool LockShown { get; init; }

    public string? WindowsUser { get; init; }

    /// <summary>Program in the foreground right now (display name), if any.</summary>
    public string? CurrentApp { get; init; }
}

/// <summary>Signed server time for one sync: bound to the device and the client's random nonce (no replay).</summary>
public sealed record ServerTime
{
    public string Kind { get; init; } = SignedKinds.Time;

    public Guid DeviceId { get; init; }

    public string Nonce { get; init; } = "";

    public DateTimeOffset ServerUtc { get; init; }
}

public sealed record SyncResponse
{
    public SignedEnvelope Policy { get; init; } = new();

    public DateTimeOffset ServerUtcNow { get; init; }

    /// <summary>
    /// <see cref="ServerTime"/> signed with the policy key. The client only trusts the server time from this envelope,
    /// so a fake server (DNS/hosts redirect, own proxy) cannot move the clock to "tomorrow" for a fresh time budget.
    /// </summary>
    public SignedEnvelope? SignedTime { get; init; }

    /// <summary>Newest client version the server offers (informational).</summary>
    public string? LatestClientVersion { get; init; }

    /// <summary>
    /// Present only when a parent/admin requested an update for this device: a server-signed
    /// <see cref="UpdateManifest"/>. The client verifies signature and SHA-256 before installing.
    /// </summary>
    public SignedEnvelope? Update { get; init; }

    /// <summary>Pending screenshot requests for children on this device.</summary>
    public IReadOnlyList<ScreenshotRequest> Screenshots { get; init; } = [];

    public LocationMode LocationMode { get; init; }

    /// <summary>A parent pressed "Jetzt orten".</summary>
    public bool LocateNow { get; init; }

    /// <summary>A parent/admin asked for the client's diagnostic log.</summary>
    public bool SendDiagnostics { get; init; }
}

/// <summary>Signed description of a client build the device may install.</summary>
public sealed record UpdateManifest
{
    /// <summary>Signed document type – an update manifest can never be passed off as rules (or the other way round).</summary>
    public string Kind { get; init; } = SignedKinds.Update;

    public Guid DeviceId { get; init; }

    public string Version { get; init; } = "";

    /// <summary>Lower-case hex SHA-256 of PaCo-Setup.exe (without any appended setup payload).</summary>
    public string Sha256 { get; init; } = "";

    public long Size { get; init; }

    public DateTimeOffset IssuedUtc { get; init; }

    public static bool IsNewer(string? candidate, string? current) =>
        System.Version.TryParse(candidate, out var c) && (!System.Version.TryParse(current, out var cur) || c > cur);
}

/// <summary>
/// Configuration appended to PaCo-Setup.exe by the server at download time, so the installer already knows
/// server address and pairing code. Layout at end of file: [json utf8][int32 length LE][8 byte magic].
/// </summary>
public sealed record SetupPayload
{
    public string ServerUrl { get; init; } = "";

    public string PairingCode { get; init; } = "";

    public static readonly byte[] Magic = "PACOCFG1"u8.ToArray();

    public static byte[] Encode(SetupPayload payload)
    {
        var json = PaCoJson.SerializeToUtf8(payload);
        var result = new byte[json.Length + 4 + Magic.Length];
        json.CopyTo(result, 0);
        BitConverter.TryWriteBytes(result.AsSpan(json.Length, 4), json.Length);
        Magic.CopyTo(result, json.Length + 4);
        return result;
    }

    public static SetupPayload? TryRead(Stream file)
    {
        if (!file.CanSeek || file.Length < Magic.Length + 4) return null;
        Span<byte> tail = stackalloc byte[12];
        file.Seek(-12, SeekOrigin.End);
        file.ReadExactly(tail);
        if (!tail[4..].SequenceEqual(Magic)) return null;
        var len = BitConverter.ToInt32(tail[..4]);
        if (len <= 0 || len > 64 * 1024 || len > file.Length - 12) return null;
        var buf = new byte[len];
        file.Seek(-12 - len, SeekOrigin.End);
        file.ReadExactly(buf);
        try { return PaCoJson.Deserialize<SetupPayload>(buf); }
        catch { return null; }
    }
}
