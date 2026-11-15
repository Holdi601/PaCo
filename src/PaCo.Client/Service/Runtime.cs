using PaCo.Client.Config;
using PaCo.Shared;

namespace PaCo.Client.Service;

/// <summary>Shared service state. All access to <see cref="State"/> goes through <see cref="Gate"/>.</summary>
internal sealed class Runtime
{
    private readonly System.Diagnostics.Stopwatch _mono = System.Diagnostics.Stopwatch.StartNew();

    public Runtime(DeviceSecrets device)
    {
        Device = device;
        Policies = new PolicyStore(device);
        Policies.LoadFromDisk();
        State = ClientState.Load();
        var notes = new List<string>();
        Clock = new TrustedClock(() => DateTimeOffset.UtcNow, () => _mono.Elapsed, State.LastUtc, State.ServerOffsetSeconds, notes);
        foreach (var n in notes) State.AddEvent(DeviceEventKind.ClockTampering, n);
    }

    /// <summary>Tamper-resistant clock (monotonic while running, never backwards, server-confirmed).</summary>
    public TrustedClock Clock { get; }

    public DeviceSecrets Device { get; }

    public PolicyStore Policies { get; }

    public ClientState State { get; }

    public Lock Gate { get; } = new();

    /// <summary>Children currently using the PC / seeing the lock screen (reported to the website).</summary>
    public HashSet<Guid> ActiveChildren { get; } = [];

    public HashSet<Guid> LockedChildren { get; } = [];

    /// <summary>Live status per child of the last enforcement tick.</summary>
    public Dictionary<Guid, ChildLiveStatus> Live { get; } = [];

    /// <summary>Screenshot requests from the server, consumed by the enforcer.</summary>
    public System.Collections.Concurrent.ConcurrentQueue<ScreenshotRequest> ScreenshotRequests { get; } = new();

    /// <summary>Captured screenshots (empty = child not available), uploaded by the sync worker.</summary>
    public System.Collections.Concurrent.ConcurrentQueue<(Guid RequestId, byte[] Jpeg)> ScreenshotUploads { get; } = new();

    private readonly SemaphoreSlim _syncSignal = new(0, 1);
    private volatile bool _locateRequested;

    public LocationMode LocationMode
    {
        get { lock (Gate) return State.LocationMode; }
    }

    public void RequestLocate() => _locateRequested = true;

    public bool TakeLocateRequest()
    {
        var r = _locateRequested;
        _locateRequested = false;
        return r;
    }

    /// <summary>Wakes the sync worker immediately (alarms, screenshots).</summary>
    public void RequestSyncNow()
    {
        try
        {
            if (_syncSignal.CurrentCount == 0) _syncSignal.Release();
        }
        catch (SemaphoreFullException) { }
    }

    public Task<bool> WaitForSyncSignalAsync(TimeSpan timeout, CancellationToken ct) => _syncSignal.WaitAsync(timeout, ct);

    private TimeSpan _lastSave = Mono.Never;
    private bool _dirty;

    /// <summary>Trusted "now" (see <see cref="TrustedClock"/>). Call under <see cref="Gate"/>.</summary>
    public DateTimeOffset Now()
    {
        var notes = new List<string>();
        var t = Clock.Now(notes);
        foreach (var n in notes)
        {
            State.AddEvent(DeviceEventKind.ClockTampering, n);
            MarkDirty();
        }
        State.LastUtc = Clock.LastTrusted;
        return t;
    }

    /// <summary>
    /// Adopts the server time after a sync. If the local clock was ahead (e.g. BIOS clock set forward to get a
    /// "new day"), usage booked on future days is moved to the real day and the parents are warned.
    /// </summary>
    public void ConfirmServerTime(DateTimeOffset serverUtc)
    {
        var wasVerified = Clock.Verified;
        var diff = Clock.Confirm(serverUtc);
        State.ServerOffsetSeconds = Clock.OffsetSeconds;
        State.LastUtc = Clock.LastTrusted;
        if (Math.Abs(diff.TotalMinutes) >= 2)
        {
            var text = diff < TimeSpan.Zero
                ? $"Die Uhr des PCs ging {FormatSpan(-diff)} vor – korrigiert mit der Serverzeit."
                : $"Die Uhr des PCs ging {FormatSpan(diff)} nach – korrigiert mit der Serverzeit.";
            if (diff < -TimeSpan.FromMinutes(10)) State.AddEvent(DeviceEventKind.ClockTampering, text);
            else Log.Info(text);
        }

        var tz = FamilyClock.Resolve(Policies.Current?.TimeZoneId ?? "Europe/Berlin");
        var merged = TrustedClock.MergeFutureDays(State.Usage, FamilyClock.Day(serverUtc, tz),
            key => (Guid.ParseExact(key[..key.IndexOf('|')], "N"), DateOnly.ParseExact(key[(key.IndexOf('|') + 1)..], "yyyy-MM-dd")),
            ClientState.Key);
        if (merged > 0)
            State.AddEvent(DeviceEventKind.ClockTampering, "Nutzung an einem „zukünftigen“ Tag (vorgestellte Uhr) wurde dem echten Tag angerechnet.");
        if (!wasVerified) Log.Info($"Zeit vom Server bestätigt: {serverUtc:u}");
        MarkDirty();
    }

    private static string FormatSpan(TimeSpan t) =>
        t.TotalHours >= 1 ? $"{(int)t.TotalHours} Std {t.Minutes} Min" : $"{(int)t.TotalMinutes} Min";

    public void MarkDirty() => _dirty = true;

    /// <summary>Saves at most every 10 s (or immediately when <paramref name="force"/>).</summary>
    public void SaveIfNeeded(bool force = false)
    {
        if (!_dirty && !force) return;
        if (!force && Mono.Now - _lastSave < TimeSpan.FromSeconds(10)) return;
        try
        {
            State.Save();
            _dirty = false;
            _lastSave = Mono.Now;
        }
        catch (Exception ex)
        {
            Log.Error("Zustand speichern", ex);
        }
    }
}
