namespace PaCo.Shared;

/// <summary>What happens on a child's PC when no time is left.</summary>
public enum ExpiryAction
{
    /// <summary>Mute audio, show a black lock screen that needs a parent unlock code (or a grant from the website).</summary>
    LockScreen = 0,

    /// <summary>Mute audio and sign the child out immediately.</summary>
    Logoff = 1,
}

/// <summary>Rules for one weekday. Times are local times in the family's time zone.</summary>
public sealed record DayRule
{
    /// <summary>Daily budget in minutes. <c>null</c> = no limit.</summary>
    public int? LimitMinutes { get; init; }

    /// <summary>Start of the allowed window. <c>null</c> together with <see cref="WindowEnd"/> = whole day.</summary>
    public TimeOnly? WindowStart { get; init; }

    /// <summary>End of the allowed window (exclusive).</summary>
    public TimeOnly? WindowEnd { get; init; }

    public static DayRule Unrestricted { get; } = new();
}

public sealed record WebFilterPolicy
{
    /// <summary>URL patterns in Chromium URLBlocklist syntax, e.g. "tiktok.com".</summary>
    public IReadOnlyList<string> BlockedSites { get; init; } = [];

    public bool ForceSafeSearch { get; init; } = true;

    public bool YouTubeRestricted { get; init; } = true;

    public bool BlockPrivateBrowsing { get; init; } = true;

    /// <summary>Browser developer tools (F12) off – they can be used to get around page blocks.</summary>
    public bool BlockDevTools { get; init; } = true;
}

/// <summary>Extra minutes granted from the website for a given day.</summary>
public sealed record TimeGrant
{
    public Guid Id { get; init; }

    public DateOnly Day { get; init; }

    /// <summary>Minutes granted. 0 = unlimited for the rest of the day.</summary>
    public int Minutes { get; init; }

    /// <summary>Seconds of this grant already consumed on other devices.</summary>
    public long ConsumedSecondsElsewhere { get; init; }
}

public sealed record ChildPolicy
{
    public Guid ChildId { get; init; }

    public string Name { get; init; } = "";

    /// <summary>UI language for the child's PC (lock screen, warnings, help) – a code from <see cref="I18n.Languages"/>.</summary>
    public string Language { get; init; } = "de";

    /// <summary>Monitoring switched off by the parents: nothing is enforced, usage is still recorded.</summary>
    public bool Paused { get; init; }

    /// <summary>Parent pressed "jetzt sperren": blocked regardless of remaining time or grants.</summary>
    public bool LockedByParent { get; init; }

    public ExpiryAction ExpiryAction { get; init; } = ExpiryAction.LockScreen;

    /// <summary>Exactly 7 entries, index = <see cref="DayOfWeek"/> (0 = Sunday).</summary>
    public IReadOnlyList<DayRule> Days { get; init; } = Enumerable.Repeat(DayRule.Unrestricted, 7).ToArray();

    public IReadOnlyList<TimeGrant> Grants { get; init; } = [];

    /// <summary>Regular (non-grant) seconds used per day on other devices of this child.</summary>
    public IReadOnlyDictionary<DateOnly, long> RegularSecondsElsewhere { get; init; } = new Dictionary<DateOnly, long>();

    /// <summary>Process names (without .exe, case-insensitive) that are killed in the child's session.</summary>
    public IReadOnlyList<string> BlockedApps { get; init; } = [];

    /// <summary>Per-app/site limits and learning time (see <see cref="TimeGroup"/>).</summary>
    public IReadOnlyList<TimeGroup> Groups { get; init; } = [];

    /// <summary>Group seconds used on the child's other PCs (shared time only), by group id and day.</summary>
    public IReadOnlyDictionary<Guid, Dictionary<DateOnly, long>> GroupSecondsElsewhere { get; init; } = new Dictionary<Guid, Dictionary<DateOnly, long>>();

    public WebFilterPolicy Web { get; init; } = new();

    public AlertSettings Alerts { get; init; } = new();

    /// <summary>Tell the child (small notification) when the parents take a screenshot.</summary>
    public bool NotifyChildOnScreenshot { get; init; } = true;

    /// <summary>Windows user policies for the child's account: no Registry editor, command prompt, PowerShell scripts, script host.</summary>
    public bool HardenAccount { get; init; } = true;

    /// <summary>Base64 key for this child's unlock codes (see UnlockCodes.ChildKey).</summary>
    public string UnlockKey { get; init; } = "";

    public DayRule RuleFor(DayOfWeek day) => Days.Count == 7 ? Days[(int)day] : DayRule.Unrestricted;
}

public sealed record AccountMapping
{
    /// <summary>Windows security identifier of the local account.</summary>
    public string Sid { get; init; } = "";

    public Guid ChildId { get; init; }
}

/// <summary>The complete, signed rule set for one device.</summary>
public sealed record PolicyDocument
{
    public const int CurrentSchema = 1;

    /// <summary>Signed document type (see <see cref="SignedKinds"/>).</summary>
    public string Kind { get; init; } = SignedKinds.Policy;

    public int Schema { get; init; } = CurrentSchema;

    public Guid DeviceId { get; init; }

    public DateTimeOffset IssuedUtc { get; init; }

    /// <summary>IANA time zone of the family, e.g. "Europe/Berlin". Days and windows are evaluated in this zone.</summary>
    public string TimeZoneId { get; init; } = "Europe/Berlin";

    public IReadOnlyList<AccountMapping> Accounts { get; init; } = [];

    public IReadOnlyList<ChildPolicy> Children { get; init; } = [];

    /// <summary>Device was removed on the website: client stops enforcing and offers uninstall.</summary>
    public bool Revoked { get; init; }

    public ChildPolicy? ChildForSid(string sid)
    {
        var map = Accounts.FirstOrDefault(a => string.Equals(a.Sid, sid, StringComparison.OrdinalIgnoreCase));
        return map is null ? null : Children.FirstOrDefault(c => c.ChildId == map.ChildId);
    }
}
