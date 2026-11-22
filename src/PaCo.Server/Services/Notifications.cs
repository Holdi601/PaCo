using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Threading.Channels;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using PaCo.Server.Data;
using PaCo.Shared;
using PaCo.Shared.I18n;

namespace PaCo.Server.Services;

public sealed record PushMessage(string Topic, string Title, string Body, int Priority, string? ClickUrl, string Tags = "warning");

/// <summary>
/// Sends push notifications through the self-hosted ntfy server. Queued so that a slow/unreachable ntfy never
/// delays the device API. Topics are secret random names per family; only PaCo holds the publish token.
/// </summary>
public sealed class Notifier(IHttpClientFactory http, IOptions<ServerOptions> options, ILogger<Notifier> log) : BackgroundService
{
    private readonly Channel<PushMessage> _queue = Channel.CreateBounded<PushMessage>(new BoundedChannelOptions(500) { FullMode = BoundedChannelFullMode.DropOldest });

    public bool Configured => !string.IsNullOrWhiteSpace(options.Value.NtfyUrl);

    public string? PublicUrl => (options.Value.NtfyPublicUrl ?? options.Value.NtfyUrl)?.TrimEnd('/');

    public static string NewTopic() => "paco-" + Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(16));

    public void Enqueue(PushMessage m)
    {
        if (!Configured) return;
        _queue.Writer.TryWrite(m);
    }

    /// <summary>Sends immediately and reports success (used by the "Test senden" button).</summary>
    public async Task<(bool Ok, string Message)> SendNowAsync(PushMessage m, CancellationToken ct)
    {
        if (!Configured) return (false, Ui.T("Benachrichtigungen sind auf dem Server nicht eingerichtet (PaCo__NtfyUrl fehlt)."));
        try
        {
            await SendAsync(m, ct);
            return (true, Ui.T("Test-Benachrichtigung gesendet."));
        }
        catch (Exception ex)
        {
            log.LogError(ex, "ntfy-Test fehlgeschlagen");
            return (false, Ui.F("Senden fehlgeschlagen: {0}", ex.Message));
        }
    }

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        await foreach (var m in _queue.Reader.ReadAllAsync(ct))
        {
            for (var attempt = 1; attempt <= 3; attempt++)
            {
                try
                {
                    await SendAsync(m, ct);
                    break;
                }
                catch (Exception ex) when (!ct.IsCancellationRequested)
                {
                    log.LogWarning("ntfy-Versand fehlgeschlagen (Versuch {Attempt}): {Message}", attempt, ex.Message);
                    await Task.Delay(TimeSpan.FromSeconds(5 * attempt), ct);
                }
            }
        }
    }

    private async Task SendAsync(PushMessage m, CancellationToken ct)
    {
        using var client = http.CreateClient("ntfy");
        using var req = new HttpRequestMessage(HttpMethod.Post, $"{options.Value.NtfyUrl!.TrimEnd('/')}/{m.Topic}")
        {
            Content = new StringContent(m.Body, System.Text.Encoding.UTF8, "text/plain"),
        };
        // Non-ASCII header values must be RFC 2047 encoded for ntfy.
        req.Headers.TryAddWithoutValidation("Title", $"=?UTF-8?B?{Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes(m.Title))}?=");
        req.Headers.TryAddWithoutValidation("Priority", m.Priority.ToString());
        req.Headers.TryAddWithoutValidation("Tags", m.Tags);
        if (m.ClickUrl is not null) req.Headers.TryAddWithoutValidation("Click", m.ClickUrl);
        if (!string.IsNullOrWhiteSpace(options.Value.NtfyToken))
            req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", options.Value.NtfyToken);
        using var resp = await client.SendAsync(req, ct);
        resp.EnsureSuccessStatusCode();
    }
}

/// <summary>Screenshot files: encrypted with ASP.NET Data Protection, stored per family outside wwwroot.</summary>
public sealed class ScreenshotStore(IOptions<ServerOptions> options, IDataProtectionProvider dp)
{
    private readonly IDataProtector _protector = dp.CreateProtector("PaCo.Screenshots.v1");

    private string PathFor(Guid familyId, Guid id) =>
        Path.Combine(options.Value.DataDir, "screenshots", familyId.ToString("N"), id.ToString("N") + ".bin");

    public async Task SaveAsync(Guid familyId, Guid id, byte[] jpeg, CancellationToken ct)
    {
        var path = PathFor(familyId, id);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        await File.WriteAllBytesAsync(path, _protector.Protect(jpeg), ct);
    }

    public async Task<byte[]?> LoadAsync(Guid familyId, Guid id, CancellationToken ct)
    {
        var path = PathFor(familyId, id);
        return File.Exists(path) ? _protector.Unprotect(await File.ReadAllBytesAsync(path, ct)) : null;
    }

    public void Delete(Guid familyId, Guid id)
    {
        var path = PathFor(familyId, id);
        if (File.Exists(path)) File.Delete(path);
    }
}

/// <summary>
/// Housekeeping (every minute): expire screenshot requests, delete old screenshots (7 days) and alerts (90 days),
/// warn the parents about PCs that stopped contacting the server.
/// </summary>
public sealed class Housekeeping(IServiceScopeFactory scopes, TimeProvider clock, Notifier notifier, IOptions<ServerOptions> options, ILogger<Housekeeping> log) : BackgroundService
{
    public static readonly TimeSpan ScreenshotRequestTimeout = TimeSpan.FromMinutes(2);

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                using var scope = scopes.CreateScope();
                var db = scope.ServiceProvider.GetRequiredService<PaCoDb>();
                var store = scope.ServiceProvider.GetRequiredService<ScreenshotStore>();
                var now = clock.GetUtcNow();

                var stale = now - ScreenshotRequestTimeout;
                foreach (var s in await db.Screenshots.IgnoreQueryFilters().Where(s => s.Status == ScreenshotStatus.Pending && s.RequestedUtc < stale).ToListAsync(ct))
                    s.Status = ScreenshotStatus.Expired;

                var old = now.AddDays(-7);
                var oldShots = await db.Screenshots.IgnoreQueryFilters().Where(s => s.RequestedUtc < old).ToListAsync(ct);
                foreach (var s in oldShots) store.Delete(s.FamilyId, s.Id);
                db.Screenshots.RemoveRange(oldShots);

                var oldAlerts = now.AddDays(-90);
                db.Alerts.RemoveRange(await db.Alerts.IgnoreQueryFilters().Where(a => a.TimeUtc < oldAlerts).ToListAsync(ct));

                var oldLocations = now.AddDays(-30);
                db.Locations.RemoveRange(await db.Locations.IgnoreQueryFilters().Where(l => l.TimeUtc < oldLocations).ToListAsync(ct));
                db.DeviceIps.RemoveRange(await db.DeviceIps.IgnoreQueryFilters().Where(i => i.LastSeenUtc < oldAlerts).ToListAsync(ct));
                await db.AuthSessions.Where(a => a.ExpiresUtc < now).ExecuteDeleteAsync(ct);
                // Device log: 90 days; usage statistics: 400 days.
                await db.DeviceEvents.IgnoreQueryFilters().Where(e => e.TimeUtc < oldAlerts).ExecuteDeleteAsync(ct);
                var oldUsage = DateOnly.FromDateTime(now.UtcDateTime).AddDays(-400);
                await db.UsageDays.IgnoreQueryFilters().Where(u => u.Day < oldUsage).ExecuteDeleteAsync(ct);
                await db.SaveChangesAsync(ct);
                if (oldShots.Count > 0) log.LogInformation("{Count} alte Bildschirmfotos gelöscht", oldShots.Count);

                var silent = await SilentDevices.CheckAsync(db, notifier, options.Value.PublicUrl?.TrimEnd('/'), now, ct);
                if (silent > 0) log.LogWarning("{Count} Gerät(e) ohne Kontakt – Eltern benachrichtigt", silent);
            }
            catch (Exception ex) when (!ct.IsCancellationRequested)
            {
                log.LogError(ex, "Aufräumen fehlgeschlagen");
            }
            await Task.Delay(TimeSpan.FromMinutes(1), ct);
        }
    }
}

public static class AlertTexts
{
    public static int Priority(AlertCategory c) => c is AlertCategory.SelfHarm or AlertCategory.Grooming ? 5 : 4;

    /// <summary>Push language of a family: the language most of its parents chose (one shared ntfy topic per family).</summary>
    public static async Task<string> PushLanguageAsync(PaCoDb db, Guid familyId, CancellationToken ct)
    {
        var langs = await db.Users.Where(u => u.FamilyId == familyId && u.Language != null).Select(u => u.Language!).ToListAsync(ct);
        return langs.GroupBy(l => l).OrderByDescending(g => g.Count()).Select(g => g.Key).FirstOrDefault() ?? Languages.Source;
    }

    public static PushMessage ForAlert(string topic, string childName, AlertEntity a, string? baseUrl, string lang) => new(
        topic,
        Ui.In(lang, "PaCo-Alarm: {0}", childName),
        Ui.In(lang, "{0}: „{1}“ in {2}", SharedText.T(lang, AlertKeywords.Label(a.Category)), a.Keyword, a.App) + "\n" + a.Snippet,
        Priority(a.Category),
        baseUrl is null ? null : $"{baseUrl}/Alerts",
        a.Category == AlertCategory.SelfHarm ? "rotating_light,warning" : "warning");
}
