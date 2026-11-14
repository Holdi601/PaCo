using PaCo.Shared;

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

    /// <summary>Audio was muted by PaCo and must be restored the next time the child is allowed again.</summary>
    public bool AudioMutedByPaCo { get; set; }

    public static string Key(Guid child, DateOnly day) => $"{child:N}|{FamilyClock.DayKey(day)}";

    public DayUsage UsageFor(Guid child, DateOnly day)
    {
        var key = Key(child, day);
        if (!Usage.TryGetValue(key, out var u)) Usage[key] = u = new DayUsage();
        return u;
    }

    public void AddEvent(DeviceEventKind kind, string message, Guid? child = null)
    {
        PendingEvents.Add(new DeviceEvent { TimeUtc = DateTimeOffset.UtcNow, Kind = kind, Message = message, ChildId = child });
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
        var raw = SecureStore.ReadWithBackup(Paths.StateFile);
        if (raw is null) return new ClientState();
        try
        {
            return PaCoJson.Deserialize<ClientState>(raw);
        }
        catch (Exception ex)
        {
            Log.Error("state.json beschädigt – starte mit leerem Zustand", ex);
            return new ClientState();
        }
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
        var raw = SecureStore.ReadWithBackup(Paths.PolicyFile);
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

    /// <summary>Verifies signature and device id; rejects policies older than the current one (replay).</summary>
    public bool Accept(SignedEnvelope env, bool persist = true)
    {
        var doc = PolicySigning.Verify<PolicyDocument>(env, device.PolicyPublicKeyPem);
        if (doc.DeviceId != device.DeviceId) throw new PolicySignatureException("Regeln gehören zu einem anderen Gerät.");
        lock (_gate)
        {
            if (Current is not null && doc.IssuedUtc < Current.IssuedUtc) return false;
            Current = doc;
        }
        if (persist) SecureStore.WriteAtomic(Paths.PolicyFile, PaCoJson.SerializeToUtf8(env));
        Changed?.Invoke();
        return true;
    }
}
