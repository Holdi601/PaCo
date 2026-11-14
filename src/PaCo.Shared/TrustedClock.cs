namespace PaCo.Shared;

/// <summary>
/// Tamper-resistant clock for the child's PC.
/// <list type="bullet">
/// <item>While running, time advances only with a monotonic counter – changing the Windows clock has no effect.</item>
/// <item>It never runs backwards: setting the Windows/BIOS clock back cannot give back used time or allowed hours.</item>
/// <item>Forward jumps beyond the monotonic counter (sleep, hibernate, power-off, or a clock set forward) are accepted
///   but the clock becomes <see cref="Verified"/> = false until the server confirms the real time. If the local time
///   was ahead, usage booked on "future" days is moved back to the real day (<see cref="MergeFutureDays"/>).</item>
/// </list>
/// </summary>
public sealed class TrustedClock
{
    public static readonly TimeSpan JumpTolerance = TimeSpan.FromSeconds(90);

    private readonly Func<DateTimeOffset> _raw;
    private readonly Func<TimeSpan> _mono;
    private DateTimeOffset _anchor;
    private TimeSpan _anchorMono;
    private DateTimeOffset _lastRaw;
    private TimeSpan _lastMono;

    /// <summary>Forward jumps accepted since the last server confirmation (may be undone by a later backward jump).</summary>
    private TimeSpan _unverifiedForward;

    /// <param name="raw">Windows UTC clock.</param>
    /// <param name="mono">Monotonic elapsed time (e.g. Stopwatch).</param>
    /// <param name="lastTrusted">Highest trusted time persisted before (default on first start).</param>
    /// <param name="offsetSeconds">Server minus Windows clock at the last confirmation.</param>
    public TrustedClock(Func<DateTimeOffset> raw, Func<TimeSpan> mono, DateTimeOffset lastTrusted, double offsetSeconds, ICollection<string>? notices = null)
    {
        _raw = raw;
        _mono = mono;
        OffsetSeconds = offsetSeconds;
        LastTrusted = lastTrusted;

        var candidate = raw().AddSeconds(offsetSeconds);
        var start = candidate;
        if (lastTrusted != default && candidate < lastTrusted)
        {
            if (lastTrusted - candidate > JumpTolerance)
                notices?.Add($"Die Uhr steht vor dem letzten bekannten Zeitpunkt (Uhr zurückgestellt?) – PaCo rechnet ab {lastTrusted:u} weiter.");
            start = lastTrusted;
        }
        _anchor = start;
        _anchorMono = mono();
        _lastRaw = raw();
        _lastMono = _anchorMono;
        Verified = false;
    }

    /// <summary>True once the server confirmed the time since this clock was created (and no forward jump happened since).</summary>
    public bool Verified { get; private set; }

    /// <summary>Highest trusted time ever returned – persist it.</summary>
    public DateTimeOffset LastTrusted { get; private set; }

    public double OffsetSeconds { get; private set; }

    public DateTimeOffset Now(ICollection<string>? notices = null)
    {
        var r = _raw();
        var m = _mono();
        var jump = (r - _lastRaw) - (m - _lastMono);
        var undone = false;
        if (jump > JumpTolerance)
        {
            // Sleep/hibernate or clock set forward: accept, but only provisionally.
            _anchor += jump;
            _unverifiedForward += jump;
            Verified = false;
        }
        else if (jump < -JumpTolerance)
        {
            // A backward jump may only revert a previous *unverified* forward jump (e.g. clock set forward and
            // back again, or time sync correcting it). Anything beyond that is ignored – time never goes back.
            var back = -jump;
            var undo = back < _unverifiedForward ? back : _unverifiedForward;
            if (undo > TimeSpan.Zero)
            {
                _anchor -= undo;
                _unverifiedForward -= undo;
                undone = true;
            }
            if (back - undo > JumpTolerance)
                notices?.Add($"Windows-Uhr wurde um {(int)(back - undo).TotalMinutes} Minuten zurückgestellt – wird ignoriert.");
        }
        _lastRaw = r;
        _lastMono = m;

        var t = _anchor + (m - _anchorMono);
        if (undone || t > LastTrusted) LastTrusted = t;
        else t = LastTrusted; // never backwards
        return t;
    }

    /// <summary>Adopts the server time. Returns server minus local trusted time (positive = local was behind).</summary>
    public TimeSpan Confirm(DateTimeOffset serverUtc)
    {
        var local = Now();
        var m = _mono();
        _anchor = serverUtc;
        _anchorMono = m;
        _lastRaw = _raw();
        _lastMono = m;
        OffsetSeconds = (serverUtc - _lastRaw).TotalSeconds;
        LastTrusted = serverUtc; // may move back if the local clock was ahead – the server is the authority
        _unverifiedForward = TimeSpan.Zero;
        Verified = true;
        return serverUtc - local;
    }

    /// <summary>
    /// After a confirmation: usage booked on days after the real day can only come from a clock that was ahead.
    /// Moves it onto the real day so nothing is gained. Returns the number of merged buckets.
    /// </summary>
    public static int MergeFutureDays(IDictionary<string, DayUsage> usage, DateOnly realDay, Func<string, (Guid Child, DateOnly Day)> parseKey,
        Func<Guid, DateOnly, string> makeKey)
    {
        var merged = 0;
        foreach (var key in usage.Keys.ToList())
        {
            var (child, day) = parseKey(key);
            if (day <= realDay) continue;
            var future = usage[key];
            var realKey = makeKey(child, realDay);
            if (!usage.TryGetValue(realKey, out var target)) usage[realKey] = target = new DayUsage();
            target.RegularSeconds += future.RegularSeconds;
            foreach (var (id, s) in future.GrantSeconds) target.GrantSeconds[id] = target.GrantSeconds.GetValueOrDefault(id) + s;
            // Codes of a wrong day were never valid for the real day; their consumed time counts as regular time.
            target.RegularSeconds += future.Codes.Sum(c => c.ConsumedSeconds);
            foreach (var (proc, app) in future.Apps)
            {
                if (!target.Apps.TryGetValue(proc, out var t)) target.Apps[proc] = t = new AppUsage { DisplayName = app.DisplayName };
                t.Seconds += app.Seconds;
            }
            usage.Remove(key);
            merged++;
        }
        return merged;
    }
}
