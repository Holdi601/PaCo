using PaCo.Shared;
using PaCo.Shared.I18n;

namespace PaCo.Client.Config;

/// <summary>Everything the service must remember across reboots (usage, codes, throttles, pending events).</summary>
internal sealed class ClientState
{
    /// <summary>Highest raw Windows UTC time ever observed – detects clocks set back.</summary>
    public DateTimeOffset LastUtc { get; set; }

    /// <summary>Server clock minus local clock at the last successful sync.</summary>
    public double ServerOffsetSeconds { get; set; }

    /// <summary>Key: "{childId:N}|{yyyy-MM-dd}".</summary>
    public Dictionary<string, DayUsage> Usage { get; set; } = [];

    public Dictionary<Guid, UnlockThrottle> Throttles { get; set; } = [];

    public List<DeviceEvent> PendingEvents { get; set; } = [];

    /// <summary>Location fixes not yet delivered (kept offline, e.g. a stolen laptop without known Wi-Fi).</summary>
    public List<LocationPoint> PendingLocations { get; set; } = [];

    /// <summary>Last location mode received from the server (survives reboots while offline).</summary>
    public LocationMode LocationMode { get; set; }

    /// <summary>Alarm hits not yet delivered to the server (kept across reboots / offline).</summary>
    public List<AlertReport> PendingAlerts { get; set; } = [];

    /// <summary>Audio was muted by PaCo and must be restored the next time the child is allowed again.</summary>
    public bool AudioMutedByPaCo { get; set; }

    public static string Key(Guid child, DateOnly day) => $"{child:N}|{FamilyClock.DayKey(day)}";

    public DayUsage UsageFor(Guid child, DateOnly day)
    {
        var key = Key(child, day);
        if (!Usage.TryGetValue(key, out var u)) Usage[key] = u = new DayUsage();
        return u;
    }

    public void AddEvent(DeviceEventKind kind, LocText text, Guid? child = null) => AddEvent(kind, text.German, child, text);

    /// <summary>Untranslatable text (e.g. an exception message) – shown as is.</summary>
    public void AddEvent(DeviceEventKind kind, string message, Guid? child = null, LocText? text = null)
    {
        PendingEvents.Add(new DeviceEvent
        {
            TimeUtc = DateTimeOffset.UtcNow, Kind = kind, Message = message, ChildId = child,
            Template = text?.Template, Args = text?.Args ?? [],
        });
        if (PendingEvents.Count > 500) PendingEvents.RemoveRange(0, PendingEvents.Count - 500);
        Log.Info($"Ereignis {kind}: {message}");
    }

    public void Prune(DateOnly today)
    {
        foreach (var key in Usage.Keys.ToList())
        {
            var day = DateOnly.ParseExact(key[(key.IndexOf('|') + 1)..], "yyyy-MM-dd");
            if (day < today.AddDays(-14)) Usage.Remove(key);
        }
    }

    public static ClientState Load()
    {
        var state = SecureStore.LoadWithBackup(Paths.StateFile, raw => PaCoJson.Deserialize<ClientState>(raw));
        if (state is null && File.Exists(Paths.StateFile)) Log.Error("state.json und Sicherung beschädigt – starte mit leerem Zustand");
        return state ?? new ClientState();
    }

    public void Save() => SecureStore.WriteAtomic(Paths.StateFile, PaCoJson.SerializeToUtf8(this));
}

/// <summary>Holds the last verified policy. Only server-signed policies for this device are ever accepted.</summary>
internal sealed class PolicyStore(DeviceSecrets device)
{
    private readonly Lock _gate = new();

    public PolicyDocument? Current { get; private set; }

    public event Action? Changed;

    public void LoadFromDisk()
    {
        // Main file first, then the backup – each must parse AND carry a valid signature.
        foreach (var p in new[] { Paths.PolicyFile, Paths.PolicyFile + ".bak" })
        {
            try
            {
                if (!File.Exists(p)) continue;
                Accept(PaCoJson.Deserialize<SignedEnvelope>(File.ReadAllBytes(p)), persist: false);
                if (Current is not null) return;
            }
            catch (Exception ex)
            {
                Log.Warn($"{Path.GetFileName(p)} ungültig: {ex.Message}");
            }
        }
        var raw = (byte[]?)null;
        if (raw is null) return;
        try
        {
            var env = PaCoJson.Deserialize<SignedEnvelope>(raw);
            Accept(env, persist: false);
        }
        catch (Exception ex)
        {
            Log.Error("Gespeicherte Regeln ungültig", ex);
        }
    }

    private string? _contentKey;

    /// <summary>
    /// Verifies signature and device id; rejects policies older than the current one (replay).
    /// Returns true only if the rules actually changed (not just the issue timestamp).
    /// </summary>
    public bool Accept(SignedEnvelope env, bool persist = true)
    {
        var doc = PolicySigning.Verify<PolicyDocument>(env, device.PolicyPublicKeyPem);
        SignedKinds.Expect(doc.Kind, SignedKinds.Policy);
        if (doc.DeviceId != device.DeviceId) throw new PolicySignatureException("Regeln gehören zu einem anderen Gerät.");
        var key = PaCoJson.Serialize(doc with { IssuedUtc = default });
        bool changed;
        lock (_gate)
        {
            if (Current is not null && doc.IssuedUtc < Current.IssuedUtc) return false;
            Current = doc;
            changed = key != _contentKey;
            _contentKey = key;
        }
        if (persist) SecureStore.WriteAtomic(Paths.PolicyFile, PaCoJson.SerializeToUtf8(env));
        if (changed) Changed?.Invoke();
        return changed;
    }
}
