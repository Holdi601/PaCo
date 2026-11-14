using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Threading.Channels;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using PaCo.Server.Data;
using PaCo.Shared;

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
        if (!Configured) return (false, "Benachrichtigungen sind auf dem Server nicht eingerichtet (PaCo__NtfyUrl fehlt).");
        try
        {
            await SendAsync(m, ct);
            return (true, "Test-Benachrichtigung gesendet.");
        }
        catch (Exception ex)
        {
            log.LogError(ex, "ntfy-Test fehlgeschlagen");
            return (false, $"Senden fehlgeschlagen: {ex.Message}");
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

/// <summary>Hourly housekeeping: expire screenshot requests, delete old screenshots (7 days) and alerts (90 days).</summary>
public sealed class Housekeeping(IServiceScopeFactory scopes, TimeProvider clock, ILogger<Housekeeping> log) : BackgroundService
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
                await db.SaveChangesAsync(ct);
                if (oldShots.Count > 0) log.LogInformation("{Count} alte Bildschirmfotos gelöscht", oldShots.Count);
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

    public static PushMessage ForAlert(string topic, string childName, AlertEntity a, string? baseUrl) => new(
        topic,
        $"PaCo-Alarm: {childName}",
        $"{AlertKeywords.Label(a.Category)}: „{a.Keyword}“ in {a.App}\n{a.Snippet}",
        Priority(a.Category),
        baseUrl is null ? null : $"{baseUrl}/Alerts",
        a.Category == AlertCategory.SelfHarm ? "rotating_light,warning" : "warning");
}
