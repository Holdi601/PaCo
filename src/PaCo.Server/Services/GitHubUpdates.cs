using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.Extensions.Options;
using PaCo.Shared;

namespace PaCo.Server.Services;

/// <summary>
/// Fetches the newest client build from the GitHub releases of the PaCo repository. The release must contain
/// "PaCo-Setup.exe" and "PaCo-Setup.exe.sha256" (both produced by the release workflow).
/// Devices never talk to GitHub: they only install builds this server has verified and signed.
/// </summary>
public sealed class GitHubUpdates(IHttpClientFactory http, ClientPackage client, IOptions<ServerOptions> options, ILogger<GitHubUpdates> log)
{
    private const long MaxSize = 150 * 1024 * 1024;

    public sealed record Result(bool Ok, string Message, string? Version = null);

    public async Task<Result> CheckAndDownloadAsync(CancellationToken ct)
    {
        var repo = options.Value.UpdateRepository;
        using var gh = http.CreateClient("github");
        gh.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("PaCo-Server", "1.0"));
        gh.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));

        JsonElement release;
        try
        {
            using var resp = await gh.GetAsync($"https://api.github.com/repos/{repo}/releases/latest", ct);
            if (resp.StatusCode == System.Net.HttpStatusCode.NotFound) return new(false, Ui.F("Im Repository {0} gibt es noch kein Release.", repo));
            resp.EnsureSuccessStatusCode();
            release = (await JsonDocument.ParseAsync(await resp.Content.ReadAsStreamAsync(ct), cancellationToken: ct)).RootElement.Clone();
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            return new(false, Ui.F("GitHub nicht erreichbar: {0}", ex.Message));
        }

        var tag = release.GetProperty("tag_name").GetString() ?? "";
        var version = tag.TrimStart('v', 'V');
        if (!Version.TryParse(version, out _)) return new(false, Ui.F("Release-Tag „{0}“ ist keine gültige Version.", tag));
        if (!UpdateManifest.IsNewer(version, client.Version))
            return new(true, Ui.F("Kein Update nötig: Version {0} ist aktuell (neuestes Release: {1}).", client.Version, version), client.Version);

        string? exeUrl = null, shaUrl = null, sigUrl = null;
        foreach (var a in release.GetProperty("assets").EnumerateArray())
        {
            var name = a.GetProperty("name").GetString();
            var url = a.GetProperty("browser_download_url").GetString();
            if (name == "PaCo-Setup.exe") exeUrl = url;
            if (name == "PaCo-Setup.exe.sha256") shaUrl = url;
            if (name == "PaCo-Setup.exe.sig") sigUrl = url;
        }
        // Only releases signed with the offline key (not stored on GitHub) are accepted.
        if (sigUrl is null) return new(false, Ui.F("Release {0} ist (noch) nicht signiert – Update wird nicht übernommen.", tag));
        if (exeUrl is null || shaUrl is null) return new(false, Ui.F("Release {0} enthält nicht PaCo-Setup.exe und PaCo-Setup.exe.sha256.", tag));

        var expected = (await gh.GetStringAsync(shaUrl, ct)).Trim().Split(' ', 2)[0].ToLowerInvariant();
        if (expected.Length != 64) return new(false, Ui.T("Prüfsummen-Datei ist ungültig."));

        Directory.CreateDirectory(client.UpdatesDir);
        var tmp = Path.Combine(client.UpdatesDir, $"download-{Guid.NewGuid():N}.tmp");
        try
        {
            using (var resp = await gh.GetAsync(exeUrl, HttpCompletionOption.ResponseHeadersRead, ct))
            {
                resp.EnsureSuccessStatusCode();
                if (resp.Content.Headers.ContentLength > MaxSize) return new(false, Ui.T("Datei ist zu groß."));
                await using var src = await resp.Content.ReadAsStreamAsync(ct);
                await using var dst = File.Create(tmp);
                var buffer = new byte[81920];
                long total = 0;
                int n;
                while ((n = await src.ReadAsync(buffer, ct)) > 0)
                {
                    total += n;
                    if (total > MaxSize) return new(false, Ui.T("Datei ist zu groß."));
                    await dst.WriteAsync(buffer.AsMemory(0, n), ct);
                }
            }
            string actual;
            await using (var fs = File.OpenRead(tmp)) actual = Convert.ToHexStringLower(await SHA256.HashDataAsync(fs, ct));
            if (actual != expected)
            {
                log.LogError("Update {Version}: Prüfsumme falsch (erwartet {Expected}, erhalten {Actual})", version, expected, actual);
                return new(false, Ui.T("Die Prüfsumme der heruntergeladenen Datei stimmt nicht – Update verworfen."));
            }
            var signature = await gh.GetStringAsync(sigUrl, ct);
            var publicKey = string.IsNullOrWhiteSpace(options.Value.ReleasePublicKey) ? ServerOptions.BuiltInReleasePublicKey : options.Value.ReleasePublicKey;
            if (!ReleaseSignature.Verify(publicKey, version, actual, signature))
            {
                log.LogError("Update {Version}: Signatur ungültig – verworfen", version);
                return new(false, Ui.T("Die Signatur des Updates ist ungültig – Update verworfen."));
            }
            File.Move(tmp, Path.Combine(client.UpdatesDir, $"PaCo-Setup-{version}.exe"), overwrite: true);
            log.LogInformation("Client-Update {Version} von GitHub geladen und geprüft ({Sha})", version, actual);
            return new(true, Ui.F("Version {0} von GitHub geladen und geprüft. Sie kann jetzt an die Geräte verteilt werden.", version), version);
        }
        finally
        {
            if (File.Exists(tmp)) File.Delete(tmp);
        }
    }
}
