namespace PaCo.Shared;

/// <summary>An unlock code that was redeemed on this device.</summary>
public sealed class RedeemedCode
{
    public int Nonce { get; set; }

    /// <summary>0 = unlimited for the rest of the day.</summary>
    public int Minutes { get; set; }

    public long ConsumedSeconds { get; set; }

    public DateTimeOffset RedeemedUtc { get; set; }
}

public sealed class AppUsage
{
    public string DisplayName { get; set; } = "";

    public long Seconds { get; set; }
}

/// <summary>Usage of one child on one device for one family-local day.</summary>
public sealed class DayUsage
{
    /// <summary>Seconds charged against the regular daily budget.</summary>
    public long RegularSeconds { get; set; }

    /// <summary>Seconds charged against website grants, by grant id.</summary>
    public Dictionary<Guid, long> GrantSeconds { get; set; } = [];

    public List<RedeemedCode> Codes { get; set; } = [];

    /// <summary>Foreground time per process name (lower case, without .exe).</summary>
    public Dictionary<string, AppUsage> Apps { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Foreground seconds per time group (limits and learning time).</summary>
    public Dictionary<Guid, long> Groups { get; set; } = [];

    public long TotalSeconds => RegularSeconds + GrantSeconds.Values.Sum() + Codes.Sum(c => c.ConsumedSeconds);
}

public enum BlockReason
{
    None = 0,
    LimitReached,
    OutsideWindow,
    LockedByParent,
}

public enum TimeSource
{
    /// <summary>Monitoring paused by parents.</summary>
    Unrestricted,
    Regular,
    Grant,
    Code,
    Blocked,
}

public readonly record struct Evaluation
{
    public bool Allowed => Source != TimeSource.Blocked;

    public TimeSource Source { get; init; }

    public BlockReason Reason { get; init; }

    /// <summary>Grant id (Source = Grant) – used for charging.</summary>
    public Guid GrantId { get; init; }

    /// <summary>Code nonce (Source = Code) – used for charging.</summary>
    public int CodeNonce { get; init; }

    /// <summary>Seconds left in total (regular + bonus). <c>null</c> = unlimited.</summary>
    public long? RemainingSeconds { get; init; }

    public DateOnly Day { get; init; }
}

public static class TimeRules
{
    public static Evaluation Evaluate(ChildPolicy policy, DayUsage usage, DateTimeOffset nowUtc, TimeZoneInfo tz)
    {
        var local = FamilyClock.ToLocal(nowUtc, tz);
        var day = DateOnly.FromDateTime(local);

        if (policy.Paused)
            return new Evaluation { Source = TimeSource.Unrestricted, Day = day };

        if (policy.LockedByParent)
            return new Evaluation { Source = TimeSource.Blocked, Reason = BlockReason.LockedByParent, RemainingSeconds = 0, Day = day };

        var rule = policy.RuleFor(local.DayOfWeek);
        var time = TimeOnly.FromDateTime(local);
        var hasWindow = rule.WindowStart is not null && rule.WindowEnd is not null;
        var inWindow = !hasWindow || (time >= rule.WindowStart!.Value && time < rule.WindowEnd!.Value);

        long? regularLeft = null;
        if (rule.LimitMinutes is { } limit)
        {
            policy.RegularSecondsElsewhere.TryGetValue(day, out var elsewhere);
            regularLeft = Math.Max(0, limit * 60L - usage.RegularSeconds - elsewhere);
        }

        var bonus = BonusBuckets(policy, usage, day);
        long? bonusLeft = bonus.Any(b => b.Left is null) ? null : bonus.Sum(b => b.Left!.Value);

        if (inWindow && regularLeft is not 0)
        {
            long? untilWindowEnd = hasWindow ? (long)(rule.WindowEnd!.Value - time).TotalSeconds : null;
            var regular = Min(regularLeft, untilWindowEnd);
            return new Evaluation
            {
                Source = TimeSource.Regular,
                RemainingSeconds = regular is null || bonusLeft is null ? null : regular + bonusLeft,
                Day = day,
            };
        }

        // Regular time not available: use bonus buckets, finite ones first so "unlimited" stays as a fallback.
        var pick = bonus.Where(b => b.Left is null or > 0).OrderBy(b => b.Left is null ? 1 : 0).FirstOrDefault();
        if (pick is not null)
        {
            return new Evaluation
            {
                Source = pick.Source,
                GrantId = pick.GrantId,
                CodeNonce = pick.Nonce,
                RemainingSeconds = bonusLeft,
                Day = day,
            };
        }

        return new Evaluation
        {
            Source = TimeSource.Blocked,
            Reason = inWindow ? BlockReason.LimitReached : BlockReason.OutsideWindow,
            RemainingSeconds = 0,
            Day = day,
        };
    }

    /// <summary>Books <paramref name="seconds"/> of use onto the bucket the evaluation chose.</summary>
    public static void Charge(DayUsage usage, Evaluation eval, long seconds)
    {
        switch (eval.Source)
        {
            case TimeSource.Regular:
            case TimeSource.Unrestricted:
                usage.RegularSeconds += seconds;
                break;
            case TimeSource.Grant:
                usage.GrantSeconds[eval.GrantId] = usage.GrantSeconds.GetValueOrDefault(eval.GrantId) + seconds;
                break;
            case TimeSource.Code:
                var code = usage.Codes.FirstOrDefault(c => c.Nonce == eval.CodeNonce);
                if (code is not null) code.ConsumedSeconds += seconds;
                break;
            case TimeSource.Blocked:
                break;
        }
    }

    private sealed record Bucket(TimeSource Source, Guid GrantId, int Nonce, long? Left);

    private static List<Bucket> BonusBuckets(ChildPolicy policy, DayUsage usage, DateOnly day)
    {
        var list = new List<Bucket>();
        foreach (var g in policy.Grants.Where(g => g.Day == day))
        {
            var used = usage.GrantSeconds.GetValueOrDefault(g.Id) + g.ConsumedSecondsElsewhere;
            list.Add(new Bucket(TimeSource.Grant, g.Id, 0, g.Minutes == 0 ? null : Math.Max(0, g.Minutes * 60L - used)));
        }
        foreach (var c in usage.Codes)
            list.Add(new Bucket(TimeSource.Code, Guid.Empty, c.Nonce, c.Minutes == 0 ? null : Math.Max(0, c.Minutes * 60L - c.ConsumedSeconds)));
        return list;
    }

    private static long? Min(long? a, long? b) => a is null ? b : b is null ? a : Math.Min(a.Value, b.Value);
}
