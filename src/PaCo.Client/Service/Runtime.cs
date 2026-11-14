using PaCo.Client.Config;
using PaCo.Shared;

namespace PaCo.Client.Service;

/// <summary>Shared service state. All access to <see cref="State"/> goes through <see cref="Gate"/>.</summary>
internal sealed class Runtime
{
    public Runtime(DeviceSecrets device)
    {
        Device = device;
        Policies = new PolicyStore(device);
        Policies.LoadFromDisk();
        State = ClientState.Load();
    }

    public DeviceSecrets Device { get; }

    public PolicyStore Policies { get; }

    public ClientState State { get; }

    public Lock Gate { get; } = new();

    /// <summary>Children currently using the PC / seeing the lock screen (reported to the website).</summary>
    public HashSet<Guid> ActiveChildren { get; } = [];

    public HashSet<Guid> LockedChildren { get; } = [];

    /// <summary>Live status per child of the last enforcement tick.</summary>
    public Dictionary<Guid, ChildLiveStatus> Live { get; } = [];

    private DateTime _lastSave = DateTime.MinValue;
    private bool _dirty;

    /// <summary>
    /// Trusted "now": local UTC corrected by the server offset measured at the last sync. Never goes backwards
    /// below the highest time seen before (clock set back → stays at the old value).
    /// </summary>
    public DateTimeOffset Now()
    {
        var raw = DateTimeOffset.UtcNow;
        // Only the Windows clock itself going backwards is suspicious (offset corrections from the server are not).
        // LastUtc is default (MinValue) on a fresh install – compare without arithmetic on it to avoid underflow.
        if (State.LastUtc != default && raw.AddMinutes(2) < State.LastUtc)
        {
            State.AddEvent(DeviceEventKind.ClockTampering, $"Windows-Uhr wurde zurückgestellt (von {State.LastUtc:u} auf {raw:u}).");
            State.LastUtc = raw; // report; already booked usage stays booked
            MarkDirty();
        }
        else if (raw > State.LastUtc)
        {
            State.LastUtc = raw;
        }
        return raw.AddSeconds(State.ServerOffsetSeconds);
    }

    public void MarkDirty() => _dirty = true;

    /// <summary>Saves at most every 10 s (or immediately when <paramref name="force"/>).</summary>
    public void SaveIfNeeded(bool force = false)
    {
        if (!_dirty && !force) return;
        if (!force && DateTime.UtcNow - _lastSave < TimeSpan.FromSeconds(10)) return;
        try
        {
            State.Save();
            _dirty = false;
            _lastSave = DateTime.UtcNow;
        }
        catch (Exception ex)
        {
            Log.Error("Zustand speichern", ex);
        }
    }
}
