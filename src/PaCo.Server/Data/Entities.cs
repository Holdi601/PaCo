using System.ComponentModel.DataAnnotations;
using Microsoft.AspNetCore.Identity;
using PaCo.Shared;

namespace PaCo.Server.Data;

/// <summary>Marks rows that belong to exactly one family. A global query filter isolates them per family.</summary>
public interface IFamilyScoped
{
    Guid FamilyId { get; set; }
}

public sealed class Family
{
    public Guid Id { get; set; } = Guid.NewGuid();

    [MaxLength(100)]
    public string Name { get; set; } = "";

    [MaxLength(64)]
    public string TimeZoneId { get; set; } = "Europe/Berlin";

    /// <summary>HMAC key for offline unlock codes (shared with the family's devices at pairing).</summary>
    public byte[] UnlockKey { get; set; } = [];

    public DateTimeOffset CreatedUtc { get; set; } = DateTimeOffset.UtcNow;
}

public sealed class ParentUser : IdentityUser<Guid>
{
    /// <summary>Null for the super admin, who manages accounts but sees no family data.</summary>
    public Guid? FamilyId { get; set; }

    public Family? Family { get; set; }

    [MaxLength(100)]
    public string DisplayName { get; set; } = "";

    public bool MustChangePassword { get; set; }

    public DateTimeOffset CreatedUtc { get; set; } = DateTimeOffset.UtcNow;

    public DateTimeOffset? LastLoginUtc { get; set; }

    /// <summary>Invited but the invitation link has not been used yet (no password set).</summary>
    public bool InvitePending => PasswordHash is null;
}

public sealed class Child : IFamilyScoped
{
    public Guid Id { get; set; } = Guid.NewGuid();

    public Guid FamilyId { get; set; }

    [MaxLength(60)]
    public string Name { get; set; } = "";

    public ExpiryAction ExpiryAction { get; set; } = ExpiryAction.LockScreen;

    public bool Paused { get; set; }

    public bool LockedByParent { get; set; }

    /// <summary>JSON array of 7 <see cref="DayRule"/> (index = DayOfWeek).</summary>
    public string DayRulesJson { get; set; } = "";

    /// <summary>JSON array of process names.</summary>
    public string BlockedAppsJson { get; set; } = "[]";

    /// <summary>JSON <see cref="WebFilterPolicy"/>.</summary>
    public string WebJson { get; set; } = "";

    public DateTimeOffset CreatedUtc { get; set; } = DateTimeOffset.UtcNow;

    public IReadOnlyList<DayRule> GetDays()
    {
        if (string.IsNullOrEmpty(DayRulesJson)) return DefaultDays();
        var days = PaCoJson.Deserialize<DayRule[]>(DayRulesJson);
        return days.Length == 7 ? days : DefaultDays();
    }

    public void SetDays(IReadOnlyList<DayRule> days)
    {
        if (days.Count != 7) throw new ArgumentException("Es müssen genau 7 Tage sein.", nameof(days));
        DayRulesJson = PaCoJson.Serialize(days);
    }

    public IReadOnlyList<string> GetBlockedApps() => PaCoJson.Deserialize<string[]>(string.IsNullOrEmpty(BlockedAppsJson) ? "[]" : BlockedAppsJson);

    public void SetBlockedApps(IEnumerable<string> apps) => BlockedAppsJson = PaCoJson.Serialize(apps.ToArray());

    public WebFilterPolicy GetWeb() => string.IsNullOrEmpty(WebJson) ? new WebFilterPolicy() : PaCoJson.Deserialize<WebFilterPolicy>(WebJson);

    public void SetWeb(WebFilterPolicy web) => WebJson = PaCoJson.Serialize(web);

    /// <summary>Sensible defaults for a new child: 2 h per day, 07:00–20:00.</summary>
    public static DayRule[] DefaultDays() =>
        Enumerable.Range(0, 7)
            .Select(_ => new DayRule { LimitMinutes = 120, WindowStart = new TimeOnly(7, 0), WindowEnd = new TimeOnly(20, 0) })
            .ToArray();
}

public sealed class Device : IFamilyScoped
{
    public Guid Id { get; set; } = Guid.NewGuid();

    public Guid FamilyId { get; set; }

    [MaxLength(100)]
    public string Name { get; set; } = "";

    [MaxLength(100)]
    public string MachineName { get; set; } = "";

    /// <summary>SHA-256 of the device token (hex).</summary>
    [MaxLength(64)]
    public string TokenHash { get; set; } = "";

    public DateTimeOffset PairedUtc { get; set; } = DateTimeOffset.UtcNow;

    public DateTimeOffset? LastSeenUtc { get; set; }

    [MaxLength(40)]
    public string? ClientVersion { get; set; }

    [MaxLength(100)]
    public string? OsVersion { get; set; }

    /// <summary>Client clock minus server clock at last sync, in seconds.</summary>
    public long ClockSkewSeconds { get; set; }

    public bool Revoked { get; set; }

    /// <summary>JSON array of child ids using the PC at the last sync.</summary>
    public string ActiveChildrenJson { get; set; } = "[]";

    /// <summary>JSON array of child ids seeing the lock screen at the last sync.</summary>
    public string LockedChildrenJson { get; set; } = "[]";

    /// <summary>JSON array of <see cref="ChildLiveStatus"/> from the last sync.</summary>
    public string LiveJson { get; set; } = "[]";

    public List<DeviceAccount> Accounts { get; set; } = [];

    public IReadOnlyList<ChildLiveStatus> GetLive() => PaCoJson.Deserialize<ChildLiveStatus[]>(string.IsNullOrEmpty(LiveJson) ? "[]" : LiveJson);

    public IReadOnlyList<Guid> GetActiveChildren() => PaCoJson.Deserialize<Guid[]>(ActiveChildrenJson);

    public IReadOnlyList<Guid> GetLockedChildren() => PaCoJson.Deserialize<Guid[]>(LockedChildrenJson);

    /// <summary>A device counts as online when it synced within the last 2 minutes.</summary>
    public bool IsOnline(DateTimeOffset now) => LastSeenUtc is { } seen && now - seen < TimeSpan.FromMinutes(2);
}

public sealed class DeviceAccount : IFamilyScoped
{
    public Guid Id { get; set; } = Guid.NewGuid();

    public Guid FamilyId { get; set; }

    public Guid DeviceId { get; set; }

    public Device? Device { get; set; }

    [MaxLength(200)]
    public string Sid { get; set; } = "";

    [MaxLength(100)]
    public string UserName { get; set; } = "";

    [MaxLength(200)]
    public string? FullName { get; set; }

    public bool IsAdmin { get; set; }

    public Guid? ChildId { get; set; }

    public Child? Child { get; set; }

    public DateTimeOffset LastSeenUtc { get; set; } = DateTimeOffset.UtcNow;
}

public sealed class PairingCode : IFamilyScoped
{
    public Guid Id { get; set; } = Guid.NewGuid();

    public Guid FamilyId { get; set; }

    [MaxLength(64)]
    public string CodeHash { get; set; } = "";

    public DateTimeOffset ExpiresUtc { get; set; }

    public DateTimeOffset? UsedUtc { get; set; }

    public Guid CreatedBy { get; set; }
}

public sealed class TimeGrantEntity : IFamilyScoped
{
    public Guid Id { get; set; } = Guid.NewGuid();

    public Guid FamilyId { get; set; }

    public Guid ChildId { get; set; }

    public DateOnly Day { get; set; }

    /// <summary>0 = unlimited for the rest of the day.</summary>
    public int Minutes { get; set; }

    public Guid CreatedBy { get; set; }

    public DateTimeOffset CreatedUtc { get; set; } = DateTimeOffset.UtcNow;
}

public sealed class UnlockCodeIssue : IFamilyScoped
{
    public Guid Id { get; set; } = Guid.NewGuid();

    public Guid FamilyId { get; set; }

    public Guid ChildId { get; set; }

    public DateOnly Day { get; set; }

    public int Nonce { get; set; }

    public int Minutes { get; set; }

    public Guid CreatedBy { get; set; }

    public DateTimeOffset CreatedUtc { get; set; } = DateTimeOffset.UtcNow;

    public DateTimeOffset? RedeemedUtc { get; set; }
}

/// <summary>Usage of one child on one device for one day, as last reported by the device (full totals).</summary>
public sealed class UsageDay : IFamilyScoped
{
    public Guid Id { get; set; } = Guid.NewGuid();

    public Guid FamilyId { get; set; }

    public Guid DeviceId { get; set; }

    public Guid ChildId { get; set; }

    public DateOnly Day { get; set; }

    public long RegularSeconds { get; set; }

    public long BonusSeconds { get; set; }

    public string GrantSecondsJson { get; set; } = "{}";

    public string CodesJson { get; set; } = "[]";

    public string AppsJson { get; set; } = "{}";

    public DateTimeOffset UpdatedUtc { get; set; } = DateTimeOffset.UtcNow;

    public long TotalSeconds => RegularSeconds + BonusSeconds;

    public Dictionary<Guid, long> GetGrantSeconds() => PaCoJson.Deserialize<Dictionary<Guid, long>>(GrantSecondsJson);

    public List<RedeemedCode> GetCodes() => PaCoJson.Deserialize<List<RedeemedCode>>(CodesJson);

    public Dictionary<string, AppUsage> GetApps() =>
        new(PaCoJson.Deserialize<Dictionary<string, AppUsage>>(AppsJson), StringComparer.OrdinalIgnoreCase);
}

public sealed class DeviceEventEntity : IFamilyScoped
{
    public Guid Id { get; set; } = Guid.NewGuid();

    public Guid FamilyId { get; set; }

    public Guid DeviceId { get; set; }

    public Guid? ChildId { get; set; }

    public DateTimeOffset TimeUtc { get; set; }

    public DeviceEventKind Kind { get; set; }

    [MaxLength(500)]
    public string Message { get; set; } = "";
}
