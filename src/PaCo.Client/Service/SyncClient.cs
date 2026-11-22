using System.Net;
using System.Net.Http.Json;
using System.Reflection;
using Microsoft.Extensions.Hosting;
using PaCo.Client.Config;
using PaCo.Shared;
using PaCo.Shared.I18n;

namespace PaCo.Client.Service;

/// <summary>HTTP client for the PaCo server (pairing, account assignment, sync).</summary>
internal sealed class ServerApi : IDisposable
{
    private readonly HttpClient _http;

    public ServerApi(string serverUrl, DeviceSecrets? device = null)
    {
        if (!serverUrl.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException(L.T("Die Serveradresse muss mit https:// beginnen."));
        _http = new HttpClient(new SocketsHttpHandler { PooledConnectionLifetime = TimeSpan.FromMinutes(10), ConnectTimeout = TimeSpan.FromSeconds(10) })
        {
            BaseAddress = new Uri(serverUrl.TrimEnd('/') + "/"),
            Timeout = TimeSpan.FromSeconds(30),
        };
        _http.DefaultRequestHeaders.UserAgent.ParseAdd($"PaCo-Client/{Version}");
        _http.DefaultRequestHeaders.AcceptLanguage.ParseAdd(L.Lang);
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

    /// <summary>Uploads a screenshot (JPEG; empty = child not available) for a parent's request.</summary>
    public async Task UploadScreenshotAsync(Guid requestId, byte[] jpeg, CancellationToken ct = default)
    {
        using var content = new ByteArrayContent(jpeg);
        content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("image/jpeg");
        using var resp = await _http.PostAsync($"{ApiRoutes.Screenshot.TrimStart('/')}/{requestId}", content, ct);
        await EnsureAsync(resp, ct);
    }

    public async Task UploadDiagnosticsAsync(string text, CancellationToken ct = default)
    {
        using var content = new StringContent(text, System.Text.Encoding.UTF8, "text/plain");
        using var resp = await _http.PostAsync(ApiRoutes.Diagnostics.TrimStart('/'), content, ct);
        await EnsureAsync(resp, ct);
    }

    /// <summary>Downloads the plain client exe offered by the server into <paramref name="target"/>.</summary>
    /// <summary>Downloads the client; stops as soon as more than <paramref name="maxBytes"/> arrive (no disk filling).</summary>
    public async Task DownloadClientAsync(string target, long maxBytes, CancellationToken ct = default)
    {
        using var resp = await _http.GetAsync(ApiRoutes.ClientDownload.TrimStart('/'), HttpCompletionOption.ResponseHeadersRead, ct);
        await EnsureAsync(resp, ct);
        await using var src = await resp.Content.ReadAsStreamAsync(ct);
        await using var dst = new FileStream(target, FileMode.Create, FileAccess.Write, FileShare.None);
        var buffer = new byte[81920];
        long total = 0;
        int n;
        while ((n = await src.ReadAsync(buffer, ct)) > 0)
        {
            total += n;
            if (total > maxBytes) throw new InvalidOperationException("Update-Datei ist größer als angekündigt.");
            await dst.WriteAsync(buffer.AsMemory(0, n), ct);
        }
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
        throw new ServerException(resp.StatusCode, detail ?? L.F("Server antwortet mit {0} {1}", (int)resp.StatusCode, resp.ReasonPhrase));
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
            SignedKinds.Expect(m.Kind, SignedKinds.Update);
        }
        catch (PolicySignatureException ex)
        {
            lock (rt.Gate) rt.State.AddEvent(DeviceEventKind.PolicyRejected, LocText.Of("Update abgelehnt: {0}", ex.Message));
            return;
        }
        if (m.DeviceId != rt.Device.DeviceId || !UpdateManifest.IsNewer(m.Version, ServerApi.Version)) return;
        // At most one attempt per version every 30 minutes.
        if (_lastUpdateVersion == m.Version && Mono.Since(_lastUpdateAttempt) < TimeSpan.FromMinutes(30)) return;
        _lastUpdateVersion = m.Version;
        _lastUpdateAttempt = Mono.Now;

        var dir = new DirectoryInfo(Paths.UpdateDir);
        if (dir.Exists && dir.Attributes.HasFlag(FileAttributes.ReparsePoint)) dir.Delete(recursive: false); // never follow a link
        dir.Create();
        foreach (var old in dir.EnumerateFiles("PaCo-*.exe")) { try { old.Delete(); } catch (IOException) { } }
        var file = Path.Combine(dir.FullName, $"PaCo-{m.Version}.exe");
        Log.Info($"Update {ServerApi.Version} → {m.Version}: lade herunter …");
        await api.DownloadClientAsync(file, m.Size, ct);

        // Keep the file open (others may only read) from the hash check until the new version has started, so it
        // cannot be swapped in between.
        using var locked = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.Read);
        var sha = Convert.ToHexStringLower(await System.Security.Cryptography.SHA256.HashDataAsync(locked, ct));
        if (sha != m.Sha256 || locked.Length != m.Size)
        {
            locked.Dispose();
            File.Delete(file);
            lock (rt.Gate) rt.State.AddEvent(DeviceEventKind.PolicyRejected, LocText.Of("Update {0} verworfen: Prüfsumme stimmt nicht.", m.Version));
            return;
        }

        lock (rt.Gate)
        {
            rt.State.AddEvent(DeviceEventKind.Info, LocText.Of("Update auf Version {0} wird installiert.", m.Version));
            rt.SaveIfNeeded(force: true);
        }
        Log.Info($"Update {m.Version} geprüft (SHA-256 {sha[..12]}…) – starte Installation");
        System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(file, "--update")
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            WorkingDirectory = dir.FullName,
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
                if (failures > 0)
                {
                    Log.Info($"Server wieder erreichbar nach {failures} Fehlversuch(en)");
                    ReportOfflineUsage();
                }
                failures = 0;
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                if (failures == 0) _offlineSince = (Mono.Now, UsedSecondsToday());
                failures++;
                // Log every failure at first, later only every 10th to keep the log readable while offline.
                if (failures <= 3 || failures % 10 == 0) Log.Warn($"Sync fehlgeschlagen ({failures}x): {ex.GetType().Name}: {ex.Message}");
            }
            bool anyoneActive;
            lock (rt.Gate) anyoneActive = rt.ActiveChildren.Count > 0 || rt.LockedChildren.Count > 0;
            // Faster while a child uses the PC: live view on the website and quick reaction to "+15 Min".
            var delay = failures == 0 ? (anyoneActive ? ActiveInterval : Interval)
                : TimeSpan.FromSeconds(Math.Min(300, 30 * Math.Pow(2, Math.Min(failures - 1, 4))));
            // Sleep until the interval is over or something urgent (alarm, screenshot) wakes us up.
            try { await rt.WaitForSyncSignalAsync(delay, ct); } catch (OperationCanceledException) { break; }
        }
    }

    private (TimeSpan Since, long Used)? _offlineSince;

    private long UsedSecondsToday()
    {
        lock (rt.Gate)
        {
            var tz = FamilyClock.Resolve(rt.Policies.Current?.TimeZoneId ?? "Europe/Berlin");
            var key = FamilyClock.DayKey(FamilyClock.Day(rt.Now(), tz));
            return rt.State.Usage.Where(u => u.Key.EndsWith(key)).Sum(u => u.Value.TotalSeconds);
        }
    }

    /// <summary>
    /// After a connection loss: if the PC was used noticeably while offline, tell the parents – disconnecting
    /// the Wi-Fi is a typical attempt to escape "Jetzt sperren" or extra-time limits.
    /// </summary>
    private void ReportOfflineUsage()
    {
        if (_offlineSince is not { } off) return;
        _offlineSince = null;
        var minutesOffline = (int)Mono.Since(off.Since).TotalMinutes;
        var usedMinutes = (int)Math.Max(0, (UsedSecondsToday() - off.Used) / 60);
        if (minutesOffline < 10 || usedMinutes < 5) return;
        lock (rt.Gate)
            rt.State.AddEvent(DeviceEventKind.OfflineUsage, LocText.Of("Der PC war {0} Min ohne Verbindung zum Server, davon wurde er {1} Min von Kindern genutzt.", minutesOffline, usedMinutes));
    }

    private readonly HashSet<Guid> _handledScreenshots = [];
    private TimeSpan _lastAudit = Mono.Never;
    private readonly TimeSpan _started = Mono.Now;

    public async Task SyncOnceAsync(ServerApi api, CancellationToken ct)
    {
        // Screenshots first – a parent is waiting for them.
        while (rt.ScreenshotUploads.TryPeek(out var shot))
        {
            await api.UploadScreenshotAsync(shot.RequestId, shot.Jpeg, ct);
            rt.ScreenshotUploads.TryDequeue(out _);
        }

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

        // Security check of the PC at start and then daily.
        List<SecurityFinding>? security = null;
        // First check 10 minutes after start: right after boot Windows security services are not ready yet.
        if (_installUpdates && Mono.Since(_lastAudit) > TimeSpan.FromHours(24) && Mono.Since(_started) > TimeSpan.FromMinutes(10))
        {
            try
            {
                var sids = rt.Policies.Current?.Accounts.Select(a => a.Sid).ToList() ?? [];
                security = await Task.Run(() => Setup.SecurityAudit.Run(sids), ct);
                foreach (var x in security.Where(x => x.Severity >= SecuritySeverity.Warning)) Log.Warn($"Sicherheits-Check: {x.Text}");
            }
            catch (Exception ex)
            {
                Log.Error("Sicherheits-Check", ex);
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
                    Groups = new Dictionary<Guid, long>(u.Groups),
                });
            }

            // Kinderkonto mit Admin-Rechten → einmal täglich melden.
            if (policy is not null && _adminWarnDay != today)
            {
                foreach (var acc in _accounts.Where(a => a.IsAdmin))
                {
                    var child = policy.ChildForSid(acc.Sid);
                    if (child is null) continue;
                    rt.State.AddEvent(DeviceEventKind.ChildIsAdmin, LocText.Of("Windows-Konto „{0}“ von {1} hat Administratorrechte.", acc.UserName, child.Name), child.ChildId);
                }
                _adminWarnDay = today;
            }

            req = new SyncRequest
            {
                TimeNonce = Convert.ToBase64String(System.Security.Cryptography.RandomNumberGenerator.GetBytes(24)),
                ClientVersion = ServerApi.Version,
                OsVersion = ServerApi.OsVersion,
                ClientUtcNow = DateTimeOffset.UtcNow,
                Accounts = _accounts,
                ActiveChildren = rt.ActiveChildren.ToList(),
                LockedChildren = rt.LockedChildren.ToList(),
                Live = rt.Live.Values.ToList(),
                Usage = usage,
                Events = rt.State.PendingEvents.Take(200).ToList(),
                Alerts = rt.State.PendingAlerts.Take(100).ToList(),
                Locations = rt.State.PendingLocations.Take(200).ToList(),
                Security = security,
            };
        }

        var sentAt = Mono.Now; // monotonic: immune to clock changes during the request
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
        var receivedAt = Mono.Now;

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

        foreach (var s in resp.Screenshots.Take(10))
        {
            if (!_handledScreenshots.Add(s.RequestId)) continue; // already being processed
            rt.ScreenshotRequests.Enqueue(s);
        }
        if (_handledScreenshots.Count > 500) _handledScreenshots.Clear();

        if (resp.SendDiagnostics && _installUpdates)
        {
            Log.Info("Diagnose-Log angefordert – wird hochgeladen");
            await api.UploadDiagnosticsAsync(Log.Tail(), ct);
        }

        if (resp.Update is { } update && _installUpdates)
            await TryUpdateAsync(api, update, ct);

        lock (rt.Gate)
        {
            var sent = req.Events.Select(e => e.Id).ToHashSet();
            rt.State.PendingEvents.RemoveAll(e => sent.Contains(e.Id));
            if (security is not null) _lastAudit = Mono.Now;
            var sentAlerts = req.Alerts.Select(a => a.Id).ToHashSet();
            rt.State.PendingAlerts.RemoveAll(a => sentAlerts.Contains(a.Id));
            var sentLocations = req.Locations.Select(l => l.Id).ToHashSet();
            rt.State.PendingLocations.RemoveAll(l => sentLocations.Contains(l.Id));
            if (rt.State.LocationMode != resp.LocationMode)
            {
                Log.Info($"Standort-Modus: {resp.LocationMode}");
                rt.State.LocationMode = resp.LocationMode;
            }
            if (resp.LocateNow) rt.RequestLocate();
            // Server time now ≈ signed server timestamp + half the round trip. Unsigned or foreign time is ignored.
            if (VerifiedServerTime(resp, req.TimeNonce!) is { } serverUtc)
                rt.ConfirmServerTime(serverUtc + (receivedAt - sentAt) / 2);
            var tz = FamilyClock.Resolve(rt.Policies.Current?.TimeZoneId ?? "Europe/Berlin");
            rt.State.Prune(FamilyClock.Day(rt.Now(), tz));
            rt.MarkDirty();
            rt.SaveIfNeeded();
        }
    }

    private DateTimeOffset _lastTimeWarning = DateTimeOffset.MinValue;

    /// <summary>
    /// Server time only counts if it is signed with the pinned server key, for this device and for the nonce of this
    /// very request. Otherwise the internal clock keeps running unchanged (no extra time from a fake server).
    /// </summary>
    private DateTimeOffset? VerifiedServerTime(SyncResponse resp, string nonce)
    {
        try
        {
            if (resp.SignedTime is null) throw new PolicySignatureException("Serverzeit ohne Signatur.");
            var t = PolicySigning.Verify<ServerTime>(resp.SignedTime, rt.Device.PolicyPublicKeyPem);
            SignedKinds.Expect(t.Kind, SignedKinds.Time);
            if (t.DeviceId != rt.Device.DeviceId || t.Nonce != nonce)
                throw new PolicySignatureException("Serverzeit gehört nicht zu dieser Anfrage.");
            return t.ServerUtc;
        }
        catch (PolicySignatureException ex)
        {
            // Log every time, report to the parents at most hourly.
            Log.Warn($"Serverzeit verworfen: {ex.Message}");
            if (DateTimeOffset.UtcNow - _lastTimeWarning > TimeSpan.FromHours(1))
            {
                _lastTimeWarning = DateTimeOffset.UtcNow;
                rt.State.AddEvent(DeviceEventKind.ClockTampering, LocText.Of("Serverzeit ohne gültige Signatur verworfen – möglicherweise ein gefälschter Server. PaCo rechnet mit der eigenen Uhr weiter."));
            }
            return null;
        }
    }
}
