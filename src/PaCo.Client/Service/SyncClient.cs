using System.Net;
using System.Net.Http.Json;
using System.Reflection;
using Microsoft.Extensions.Hosting;
using PaCo.Client.Config;
using PaCo.Shared;

namespace PaCo.Client.Service;

/// <summary>HTTP client for the PaCo server (pairing, account assignment, sync).</summary>
internal sealed class ServerApi : IDisposable
{
    private readonly HttpClient _http;

    public ServerApi(string serverUrl, DeviceSecrets? device = null)
    {
        _http = new HttpClient(new SocketsHttpHandler { PooledConnectionLifetime = TimeSpan.FromMinutes(10), ConnectTimeout = TimeSpan.FromSeconds(10) })
        {
            BaseAddress = new Uri(serverUrl.TrimEnd('/') + "/"),
            Timeout = TimeSpan.FromSeconds(30),
        };
        _http.DefaultRequestHeaders.UserAgent.ParseAdd($"PaCo-Client/{Version}");
        if (device is not null) _http.DefaultRequestHeaders.Authorization = new("Device", $"{device.DeviceId}:{device.DeviceToken}");
    }

    public static string Version => Assembly.GetExecutingAssembly().GetName().Version?.ToString(3) ?? "0.0.0";

    public static string OsVersion => $"{Environment.OSVersion.VersionString}";

    public async Task<PairResponse> PairAsync(PairRequest req, CancellationToken ct = default)
    {
        using var resp = await _http.PostAsJsonAsync(ApiRoutes.Pair.TrimStart('/'), req, PaCoJson.Options, ct);
        await EnsureAsync(resp, ct);
        return (await resp.Content.ReadFromJsonAsync<PairResponse>(PaCoJson.Options, ct))!;
    }

    public async Task<List<ChildRef>> AssignAsync(AssignRequest req, CancellationToken ct = default)
    {
        using var resp = await _http.PostAsJsonAsync(ApiRoutes.Assign.TrimStart('/'), req, PaCoJson.Options, ct);
        await EnsureAsync(resp, ct);
        return (await resp.Content.ReadFromJsonAsync<List<ChildRef>>(PaCoJson.Options, ct))!;
    }

    public async Task<SyncResponse> SyncAsync(SyncRequest req, CancellationToken ct = default)
    {
        using var resp = await _http.PostAsJsonAsync(ApiRoutes.Sync.TrimStart('/'), req, PaCoJson.Options, ct);
        await EnsureAsync(resp, ct);
        return (await resp.Content.ReadFromJsonAsync<SyncResponse>(PaCoJson.Options, ct))!;
    }

    /// <summary>Downloads the plain client exe offered by the server into <paramref name="target"/>.</summary>
    public async Task DownloadClientAsync(string target, CancellationToken ct = default)
    {
        using var resp = await _http.GetAsync(ApiRoutes.ClientDownload.TrimStart('/'), HttpCompletionOption.ResponseHeadersRead, ct);
        await EnsureAsync(resp, ct);
        await using var src = await resp.Content.ReadAsStreamAsync(ct);
        await using var dst = File.Create(target);
        await src.CopyToAsync(dst, ct);
    }

    private static async Task EnsureAsync(HttpResponseMessage resp, CancellationToken ct)
    {
        if (resp.IsSuccessStatusCode) return;
        var body = await resp.Content.ReadAsStringAsync(ct);
        string? detail = null;
        try
        {
            using var doc = System.Text.Json.JsonDocument.Parse(body);
            if (doc.RootElement.TryGetProperty("detail", out var d)) detail = d.GetString();
        }
        catch (System.Text.Json.JsonException) { }
        throw new ServerException(resp.StatusCode, detail ?? $"Server antwortet mit {(int)resp.StatusCode} {resp.ReasonPhrase}");
    }

    public void Dispose() => _http.Dispose();
}

internal sealed class ServerException(HttpStatusCode status, string message) : Exception(message)
{
    public HttpStatusCode Status { get; } = status;
}

/// <summary>Exchanges usage/events for fresh signed rules every 30 s.</summary>
internal sealed class SyncWorker(Runtime rt, bool installUpdates = true) : BackgroundService
{
    private readonly bool _installUpdates = installUpdates;
    private string? _lastUpdateVersion;
    private TimeSpan _lastUpdateAttempt = Mono.Never;

    /// <summary>
    /// Installs a server-signed update: verify signature (pinned server key) → download → verify SHA-256 and size
    /// → start the new exe with --update (it replaces the program atomically and rolls back on failure).
    /// </summary>
    private async Task TryUpdateAsync(ServerApi api, SignedEnvelope envelope, CancellationToken ct)
    {
        UpdateManifest m;
        try
        {
            m = PolicySigning.Verify<UpdateManifest>(envelope, rt.Device.PolicyPublicKeyPem);
        }
        catch (PolicySignatureException ex)
        {
            lock (rt.Gate) rt.State.AddEvent(DeviceEventKind.PolicyRejected, "Update abgelehnt: " + ex.Message);
            return;
        }
        if (m.DeviceId != rt.Device.DeviceId || !UpdateManifest.IsNewer(m.Version, ServerApi.Version)) return;
        // At most one attempt per version every 30 minutes.
        if (_lastUpdateVersion == m.Version && Mono.Since(_lastUpdateAttempt) < TimeSpan.FromMinutes(30)) return;
        _lastUpdateVersion = m.Version;
        _lastUpdateAttempt = Mono.Now;

        var dir = Path.Combine(Paths.DataDir, "update");
        Directory.CreateDirectory(dir);
        var file = Path.Combine(dir, $"PaCo-{m.Version}.exe");
        Log.Info($"Update {ServerApi.Version} → {m.Version}: lade herunter …");
        await api.DownloadClientAsync(file, ct);

        string sha;
        await using (var fs = File.OpenRead(file)) sha = Convert.ToHexStringLower(await System.Security.Cryptography.SHA256.HashDataAsync(fs, ct));
        var size = new FileInfo(file).Length;
        if (sha != m.Sha256 || size != m.Size)
        {
            File.Delete(file);
            lock (rt.Gate) rt.State.AddEvent(DeviceEventKind.PolicyRejected, $"Update {m.Version} verworfen: Prüfsumme stimmt nicht.");
            return;
        }

        lock (rt.Gate)
        {
            rt.State.AddEvent(DeviceEventKind.Info, $"Update auf Version {m.Version} wird installiert.");
            rt.SaveIfNeeded(force: true);
        }
        Log.Info($"Update {m.Version} geprüft (SHA-256 {sha[..12]}…) – starte Installation");
        System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(file, "--update")
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            WorkingDirectory = dir,
        });
    }

    private static readonly TimeSpan Interval = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan ActiveInterval = TimeSpan.FromSeconds(15);
    private List<WindowsAccount> _accounts = [];
    private TimeSpan _accountsAt = Mono.Never;
    private DateOnly _adminWarnDay;

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        using var api = new ServerApi(rt.Device.ServerUrl, rt.Device);
        var failures = 0;
        await Task.Delay(TimeSpan.FromSeconds(2), ct);
        while (!ct.IsCancellationRequested)
        {
            try
            {
                await SyncOnceAsync(api, ct);
                if (failures > 0) Log.Info($"Server wieder erreichbar nach {failures} Fehlversuch(en)");
                failures = 0;
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                failures++;
                // Log every failure at first, later only every 10th to keep the log readable while offline.
                if (failures <= 3 || failures % 10 == 0) Log.Warn($"Sync fehlgeschlagen ({failures}x): {ex.GetType().Name}: {ex.Message}");
            }
            bool anyoneActive;
            lock (rt.Gate) anyoneActive = rt.ActiveChildren.Count > 0 || rt.LockedChildren.Count > 0;
            // Faster while a child uses the PC: live view on the website and quick reaction to "+15 Min".
            var delay = failures == 0 ? (anyoneActive ? ActiveInterval : Interval)
                : TimeSpan.FromSeconds(Math.Min(300, 30 * Math.Pow(2, Math.Min(failures - 1, 4))));
            try { await Task.Delay(delay, ct); } catch (OperationCanceledException) { break; }
        }
    }

    public async Task SyncOnceAsync(ServerApi api, CancellationToken ct)
    {
        if (Mono.Now - _accountsAt > TimeSpan.FromMinutes(10))
        {
            try
            {
                _accounts = LocalAccounts.List();
                _accountsAt = Mono.Now;
            }
            catch (Exception ex)
            {
                Log.Error("Konten auflisten", ex);
            }
        }

        SyncRequest req;
        lock (rt.Gate)
        {
            var now = rt.Now();
            var policy = rt.Policies.Current;
            var tz = FamilyClock.Resolve(policy?.TimeZoneId ?? "Europe/Berlin");
            var today = FamilyClock.Day(now, tz);
            var usage = new List<UsageReport>();
            foreach (var (key, u) in rt.State.Usage)
            {
                var parts = key.Split('|');
                var day = DateOnly.ParseExact(parts[1], "yyyy-MM-dd");
                if (day < today.AddDays(-1)) continue;
                usage.Add(new UsageReport
                {
                    ChildId = Guid.ParseExact(parts[0], "N"),
                    Day = day,
                    RegularSeconds = u.RegularSeconds,
                    GrantSeconds = new Dictionary<Guid, long>(u.GrantSeconds),
                    Codes = u.Codes.Select(c => new RedeemedCode { Nonce = c.Nonce, Minutes = c.Minutes, ConsumedSeconds = c.ConsumedSeconds, RedeemedUtc = c.RedeemedUtc }).ToList(),
                    Apps = u.Apps.ToDictionary(a => a.Key, a => new AppUsage { DisplayName = a.Value.DisplayName, Seconds = a.Value.Seconds }),
                });
            }

            // Kinderkonto mit Admin-Rechten → einmal täglich melden.
            if (policy is not null && _adminWarnDay != today)
            {
                foreach (var acc in _accounts.Where(a => a.IsAdmin))
                {
                    var child = policy.ChildForSid(acc.Sid);
                    if (child is null) continue;
                    rt.State.AddEvent(DeviceEventKind.ChildIsAdmin, $"Windows-Konto „{acc.UserName}“ von {child.Name} hat Administratorrechte.", child.ChildId);
                }
                _adminWarnDay = today;
            }

            req = new SyncRequest
            {
                ClientVersion = ServerApi.Version,
                OsVersion = ServerApi.OsVersion,
                ClientUtcNow = DateTimeOffset.UtcNow,
                Accounts = _accounts,
                ActiveChildren = rt.ActiveChildren.ToList(),
                LockedChildren = rt.LockedChildren.ToList(),
                Live = rt.Live.Values.ToList(),
                Usage = usage,
                Events = rt.State.PendingEvents.Take(200).ToList(),
            };
        }

        var sentAt = DateTimeOffset.UtcNow;
        SyncResponse resp;
        try
        {
            resp = await api.SyncAsync(req, ct);
        }
        catch (ServerException ex) when (ex.Status == HttpStatusCode.Unauthorized)
        {
            Log.Error("Server lehnt das Gerät ab (401) – wurde es auf der Website gelöscht? Letzte Regeln bleiben aktiv.");
            throw;
        }
        var receivedAt = DateTimeOffset.UtcNow;

        try
        {
            if (rt.Policies.Accept(resp.Policy))
                Log.Info("Neue Regeln übernommen");
        }
        catch (PolicySignatureException ex)
        {
            lock (rt.Gate) rt.State.AddEvent(DeviceEventKind.PolicyRejected, ex.Message);
            Log.Error("Regeln abgelehnt", ex);
        }

        if (resp.Update is { } update && _installUpdates)
            await TryUpdateAsync(api, update, ct);

        lock (rt.Gate)
        {
            var sent = req.Events.Select(e => e.Id).ToHashSet();
            rt.State.PendingEvents.RemoveAll(e => sent.Contains(e.Id));
            // Server time now ≈ server timestamp + half the round trip.
            rt.ConfirmServerTime(resp.ServerUtcNow + (receivedAt - sentAt) / 2);
            var tz = FamilyClock.Resolve(rt.Policies.Current?.TimeZoneId ?? "Europe/Berlin");
            rt.State.Prune(FamilyClock.Day(rt.Now(), tz));
            rt.MarkDirty();
            rt.SaveIfNeeded();
        }
    }
}
