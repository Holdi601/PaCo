using System.Buffers.Binary;
using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using PaCo.Server.Data;
using PaCo.Server.Services;
using PaCo.Shared;

namespace PaCo.Tests;

/// <summary>End-to-end through the real web UI: admin login with TOTP, invitation, onboarding, child rules, actions.</summary>
public partial class WebFlowTests : IClassFixture<PaCoFactory>
{
    private readonly PaCoFactory _f;

    public WebFlowTests(PaCoFactory f) => _f = f;

    private HttpClient Browser() => _f.CreateClient(new WebApplicationFactoryClientOptions
    {
        BaseAddress = new Uri("https://localhost"),
        AllowAutoRedirect = false,
        HandleCookies = true,
    });

    [Fact]
    public async Task AdminInvitesParentWhoManagesChild()
    {
        // --- Super admin exists (normally created via CLI) with 2FA ---
        const string adminMail = "admin@test.local", adminPw = "Admin-Passwort-123";
        string adminKey;
        using (var scope = _f.Services.CreateScope())
        {
            var users = scope.ServiceProvider.GetRequiredService<UserManager<ParentUser>>();
            var roles = scope.ServiceProvider.GetRequiredService<RoleManager<IdentityRole<Guid>>>();
            if (!await roles.RoleExistsAsync(PaCoRoles.SuperAdmin)) await roles.CreateAsync(new IdentityRole<Guid>(PaCoRoles.SuperAdmin));
            var admin = new ParentUser { UserName = adminMail, Email = adminMail, DisplayName = "Admin" };
            Assert.True((await users.CreateAsync(admin, adminPw)).Succeeded);
            await users.AddToRoleAsync(admin, PaCoRoles.SuperAdmin);
            await users.ResetAuthenticatorKeyAsync(admin);
            adminKey = (await users.GetAuthenticatorKeyAsync(admin))!;
            await users.SetTwoFactorEnabledAsync(admin, true);
        }

        var adminBrowser = Browser();
        await LoginAsync(adminBrowser, adminMail, adminPw, adminKey);
        Assert.Equal(HttpStatusCode.OK, (await adminBrowser.GetAsync("/Admin/Index")).StatusCode);
        // Admin has no family → no access to family pages.
        Assert.NotEqual(HttpStatusCode.OK, (await adminBrowser.GetAsync("/Dashboard")).StatusCode);

        // --- Invite a parent into a new family ---
        var adminPage = await adminBrowser.GetStringAsync("/Admin/Index");
        var invite = await PostFormAsync(adminBrowser, "/Admin/Index?handler=Invite", adminPage, new()
        {
            ["InviteName"] = "Anna", ["InviteEmail"] = "anna@test.local", ["InviteFamilyId"] = "", ["NewFamilyName"] = "Muster",
        });
        Assert.Equal(HttpStatusCode.Redirect, invite.StatusCode);
        adminPage = await adminBrowser.GetStringAsync("/Admin/Index");
        var link = WebUtility.HtmlDecode(InviteLinkRegex().Match(adminPage).Groups[1].Value);
        Assert.Contains("/Account/AcceptInvite?uid=", link);

        // --- Parent accepts invitation, sets password, sets up authenticator ---
        var parent = Browser();
        var path = new Uri(link).PathAndQuery;
        var acceptPage = await parent.GetStringAsync(path);
        Assert.Contains("Willkommen bei PaCo, Anna", acceptPage);
        var accept = await PostFormAsync(parent, path, acceptPage, new() { ["Password"] = "Anna-Passwort-123", ["ConfirmPassword"] = "Anna-Passwort-123" });
        Assert.Equal("/Account/SetupAuthenticator", accept.Headers.Location?.ToString());
        // Before 2FA: family pages are not reachable.
        var blocked = await parent.GetAsync("/Dashboard");
        Assert.Equal("/Account/SetupAuthenticator", blocked.Headers.Location?.ToString());

        var setup = await parent.GetStringAsync("/Account/SetupAuthenticator");
        var key = SharedKeyRegex().Match(setup).Groups[1].Value.Replace(" ", "").ToUpperInvariant();
        var confirm = await PostFormAsync(parent, "/Account/SetupAuthenticator", setup, new() { ["Code"] = Totp(key) });
        Assert.Equal(HttpStatusCode.Redirect, confirm.StatusCode);
        Assert.Contains("Notfall-Codes", await parent.GetStringAsync("/Account/SetupAuthenticator"));

        // --- Dashboard, create child, edit rules ---
        var dash = await parent.GetStringAsync("/Dashboard");
        Assert.Contains("Familie Muster", dash);
        var children = await parent.GetStringAsync("/Children/Index");
        var created = await PostFormAsync(parent, "/Children/Index", children, new() { ["NewName"] = "Lena" });
        var editUrl = created.Headers.Location!.ToString();
        Assert.StartsWith("/Children/Edit/", editUrl);
        var edit = await parent.GetStringAsync(editUrl);
        var form = new Dictionary<string, string>
        {
            ["Name"] = "Lena", ["ExpiryAction"] = "Logoff", ["Paused"] = "false", ["BlockedApps"] = "Discord.exe\nsteam",
            ["BlockOtherBrowsers"] = "true", ["BlockedSites"] = "https://www.TikTok.com/foo", ["ForceSafeSearch"] = "true",
            ["YouTubeRestricted"] = "true", ["BlockPrivateBrowsing"] = "true", ["AddPresets"] = "Glücksspiel",
        };
        for (var i = 0; i < 7; i++)
        {
            form[$"Days[{i}].LimitMinutes"] = "90";
            form[$"Days[{i}].Unlimited"] = "false";
            form[$"Days[{i}].AllDay"] = "false";
            form[$"Days[{i}].From"] = "14:00";
            form[$"Days[{i}].To"] = "19:30";
        }
        var saved = await PostFormAsync(parent, editUrl, edit, form);
        Assert.Equal(HttpStatusCode.Redirect, saved.StatusCode);

        Guid childId;
        using (var scope = _f.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<PaCoDb>();
            var lena = await db.Children.IgnoreQueryFilters().SingleAsync(c => c.Name == "Lena");
            childId = lena.Id;
            Assert.Equal(ExpiryAction.Logoff, lena.ExpiryAction);
            Assert.Equal(90, lena.GetDays()[1].LimitMinutes);
            Assert.Equal(new TimeOnly(19, 30), lena.GetDays()[1].WindowEnd);
            Assert.Contains("discord", lena.GetBlockedApps());
            Assert.Contains("brave", lena.GetBlockedApps());
            Assert.Contains("tiktok.com", lena.GetWeb().BlockedSites);
            Assert.Contains("bet365.com", lena.GetWeb().BlockedSites);
        }

        // Saving with empty optional fields (no blocked apps/sites) must work too.
        form["BlockedApps"] = "";
        form["BlockedSites"] = "";
        form.Remove("AddPresets");
        form["BlockOtherBrowsers"] = "false";
        var savedEmpty = await PostFormAsync(parent, editUrl, await parent.GetStringAsync(editUrl), form);
        Assert.Equal(HttpStatusCode.Redirect, savedEmpty.StatusCode);

        // --- Quick actions on the dashboard ---
        dash = await parent.GetStringAsync("/Dashboard");
        Assert.Contains("Lena", dash);
        Assert.Equal(HttpStatusCode.Redirect, (await PostFormAsync(parent, "/Dashboard?handler=Grant", dash, new() { ["child"] = childId.ToString(), ["minutes"] = "30" })).StatusCode);
        Assert.Equal(HttpStatusCode.Redirect, (await PostFormAsync(parent, "/Dashboard?handler=Code", dash, new() { ["child"] = childId.ToString(), ["minutes"] = "15" })).StatusCode);
        dash = await parent.GetStringAsync("/Dashboard");
        var code = CodeRegex().Match(dash).Groups[1].Value;
        byte[] unlockKey;
        using (var scope = _f.Services.CreateScope())
            unlockKey = (await scope.ServiceProvider.GetRequiredService<PaCoDb>().Families.SingleAsync(f => f.Name == "Muster")).UnlockKey;
        var today = FamilyClock.Day(DateTimeOffset.UtcNow, FamilyClock.Resolve("Europe/Berlin"));
        Assert.True(UnlockCodes.TryValidate(unlockKey, childId, today, code, out var decoded));
        Assert.Equal(15, decoded.Minutes);
        Assert.Contains("+30 Min", dash);

        Assert.Equal(HttpStatusCode.Redirect, (await PostFormAsync(parent, "/Dashboard?handler=Lock", dash, new() { ["child"] = childId.ToString(), ["locked"] = "true" })).StatusCode);
        Assert.Contains("Von Eltern gesperrt", await parent.GetStringAsync("/Dashboard"));

        // --- Every page renders ---
        foreach (var url in new[] { "/Dashboard", "/Children/Index", editUrl, $"/Children/Stats/{childId}", "/Devices/Index", "/Account/Manage" })
            Assert.Equal(HttpStatusCode.OK, (await parent.GetAsync(url)).StatusCode);

        // Parent cannot reach the admin area.
        Assert.NotEqual(HttpStatusCode.OK, (await parent.GetAsync("/Admin/Index")).StatusCode);

        // Pairing code for manual entry.
        var devices = await parent.GetStringAsync("/Devices/Index");
        Assert.Equal(HttpStatusCode.Redirect, (await PostFormAsync(parent, "/Devices/Index?handler=Code", devices, [])).StatusCode);
        Assert.Matches(@"[A-Z2-9]{4}-[A-Z2-9]{4}", await parent.GetStringAsync("/Devices/Index"));
    }

    [Fact]
    public async Task WrongTotpIsRejectedAndLockoutAfterFiveFailures()
    {
        const string mail = "lock@test.local", pw = "Lock-Passwort-123";
        using (var scope = _f.Services.CreateScope())
        {
            var users = scope.ServiceProvider.GetRequiredService<UserManager<ParentUser>>();
            var u = new ParentUser { UserName = mail, Email = mail };
            await users.CreateAsync(u, pw);
        }
        var b = Browser();
        for (var i = 0; i < 5; i++)
        {
            var page = await b.GetStringAsync("/Account/Login");
            await PostFormAsync(b, "/Account/Login", page, new() { ["Email"] = mail, ["Password"] = "falsch" + i });
        }
        var login = await b.GetStringAsync("/Account/Login");
        var r = await PostFormAsync(b, "/Account/Login", login, new() { ["Email"] = mail, ["Password"] = pw });
        Assert.Equal(HttpStatusCode.OK, r.StatusCode); // stays on login page
        Assert.Contains("gesperrt", await r.Content.ReadAsStringAsync());
    }

    /// <summary>Creates a family with one fully onboarded parent and returns a logged-in browser.</summary>
    private async Task<(HttpClient Browser, Guid FamilyId)> ParentOfNewFamilyAsync(string mail)
    {
        const string pw = "Eltern-Passwort-123";
        Guid fid;
        string key;
        using (var scope = _f.Services.CreateScope())
        {
            var admin = scope.ServiceProvider.GetRequiredService<AccountAdmin>();
            var users = scope.ServiceProvider.GetRequiredService<UserManager<ParentUser>>();
            fid = (await admin.CreateFamilyAsync("Fam-" + mail)).Id;
            var u = new ParentUser { UserName = mail, Email = mail, DisplayName = mail, FamilyId = fid };
            Assert.True((await users.CreateAsync(u, pw)).Succeeded);
            await users.ResetAuthenticatorKeyAsync(u);
            key = (await users.GetAuthenticatorKeyAsync(u))!;
            await users.SetTwoFactorEnabledAsync(u, true);
        }
        var b = Browser();
        await LoginAsync(b, mail, pw, key);
        return (b, fid);
    }

    [Fact]
    public async Task ScreenshotsAndAlertsAreStrictlyPerFamily()
    {
        var (a, famA) = await ParentOfNewFamilyAsync("a-parent@test.local");
        var (b, famB) = await ParentOfNewFamilyAsync("b-parent@test.local");

        // Family A: child + paired device.
        Guid childA;
        string tokenA;
        Guid deviceA;
        Guid deviceB;
        string tokenB;
        using (var scope = _f.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<PaCoDb>();
            var c = new Child { FamilyId = famA, Name = "KindA" };
            db.Children.Add(c);
            childA = c.Id;
            tokenA = Secrets.DeviceToken();
            tokenB = Secrets.DeviceToken();
            var dA = new Device { FamilyId = famA, Name = "PC-A", TokenHash = Secrets.Sha256Hex(tokenA) };
            var dB = new Device { FamilyId = famB, Name = "PC-B", TokenHash = Secrets.Sha256Hex(tokenB) };
            db.Devices.AddRange(dA, dB);
            db.DeviceAccounts.Add(new DeviceAccount { FamilyId = famA, DeviceId = dA.Id, Sid = "S-1-5-21-7-7-7-1001", UserName = "kinda", ChildId = c.Id });
            (deviceA, deviceB) = (dA.Id, dB.Id);
            await db.SaveChangesAsync();
        }

        // Parent A requests a screenshot via the dashboard.
        var dash = await a.GetStringAsync("/Dashboard");
        var req = await PostFormAsync(a, "/Dashboard?handler=Screenshot", dash, new() { ["child"] = childA.ToString() });
        var shotUrl = req.Headers.Location!.ToString();
        var shotId = Guid.Parse(shotUrl.Split('/').Last());

        var jpeg = new byte[] { 0xFF, 0xD8, 0xFF, 0xE0, 1, 2, 3, 4, 5, 0xFF, 0xD9 };
        var devB = _f.Https();
        devB.DefaultRequestHeaders.Authorization = new("Device", $"{deviceB}:{tokenB}");
        Assert.Equal(HttpStatusCode.NotFound, (await devB.PostAsync($"{ApiRoutes.Screenshot}/{shotId}", new ByteArrayContent(jpeg))).StatusCode);

        var devA = _f.Https();
        devA.DefaultRequestHeaders.Authorization = new("Device", $"{deviceA}:{tokenA}");
        var sync = await (await devA.PostAsJsonAsync(ApiRoutes.Sync, new SyncRequest
        {
            Alerts = [new AlertReport { ChildId = childA, TimeUtc = DateTimeOffset.UtcNow, Category = AlertCategory.SelfHarm, Keyword = "suizid", App = "chrome", Snippet = "wie suizid" }],
        }, PaCoJson.Options)).Content.ReadFromJsonAsync<SyncResponse>(PaCoJson.Options);
        Assert.Contains(sync!.Screenshots, s => s.RequestId == shotId);
        Assert.Equal(HttpStatusCode.OK, (await devA.PostAsync($"{ApiRoutes.Screenshot}/{shotId}", new ByteArrayContent(jpeg))).StatusCode);

        // Parent A sees page and image; stored file is encrypted (not a JPEG on disk).
        Assert.Equal(HttpStatusCode.OK, (await a.GetAsync(shotUrl)).StatusCode);
        var img = await a.GetAsync($"{shotUrl}?handler=Image");
        Assert.Equal("image/jpeg", img.Content.Headers.ContentType?.MediaType);
        Assert.Equal(jpeg, await img.Content.ReadAsByteArrayAsync());
        var onDisk = await File.ReadAllBytesAsync(Path.Combine(_f.DataDir, "screenshots", famA.ToString("N"), shotId.ToString("N") + ".bin"));
        Assert.NotEqual(0xFF, onDisk[0]);

        // Parent B (other family) gets "not found" for both – even knowing the id.
        Assert.Equal(HttpStatusCode.NotFound, (await b.GetAsync(shotUrl)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await b.GetAsync($"{shotUrl}?handler=Image")).StatusCode);
        // Random ids are simply not found.
        Assert.Equal(HttpStatusCode.NotFound, (await a.GetAsync($"/Children/Screenshot/{Guid.NewGuid()}?handler=Image")).StatusCode);

        // Alerts: A sees its alarm, B does not.
        Assert.Contains("wie suizid", await a.GetStringAsync("/Alerts"));
        Assert.DoesNotContain("wie suizid", await b.GetStringAsync("/Alerts"));
        // B cannot delete A's alert.
        Guid alertId;
        using (var scope = _f.Services.CreateScope())
            alertId = (await scope.ServiceProvider.GetRequiredService<PaCoDb>().Alerts.IgnoreQueryFilters().SingleAsync(x => x.ChildId == childA)).Id;
        var bAlerts = await b.GetStringAsync("/Alerts");
        Assert.Equal(HttpStatusCode.NotFound, (await PostFormAsync(b, $"/Alerts?handler=Delete&id={alertId}", bAlerts, [])).StatusCode);

        // Family page lists only own parents; B cannot remove A's parent.
        var famPage = await b.GetStringAsync("/Family");
        Assert.DoesNotContain("a-parent@test.local", famPage);
        Guid parentA;
        using (var scope = _f.Services.CreateScope())
            parentA = (await scope.ServiceProvider.GetRequiredService<UserManager<ParentUser>>().FindByEmailAsync("a-parent@test.local"))!.Id;
        Assert.Equal(HttpStatusCode.NotFound, (await PostFormAsync(b, "/Family?handler=Remove", famPage, new() { ["userId"] = parentA.ToString() })).StatusCode);

        // Notifications page renders and creates a secret topic.
        Assert.Contains("paco-", await a.GetStringAsync("/Notifications"));
    }

    private async Task LoginAsync(HttpClient b, string mail, string pw, string totpKey)
    {
        var page = await b.GetStringAsync("/Account/Login");
        var r = await PostFormAsync(b, "/Account/Login", page, new() { ["Email"] = mail, ["Password"] = pw });
        Assert.StartsWith("/Account/LoginWith2fa", r.Headers.Location?.ToString());
        var twoFa = await b.GetStringAsync(r.Headers.Location!.ToString());
        var r2 = await PostFormAsync(b, "/Account/LoginWith2fa", twoFa, new() { ["Code"] = Totp(totpKey), ["RememberMe"] = "false" });
        Assert.Equal("/", r2.Headers.Location?.ToString());
    }

    private static async Task<HttpResponseMessage> PostFormAsync(HttpClient b, string url, string pageHtml, Dictionary<string, string> fields)
    {
        var token = TokenRegex().Match(pageHtml).Groups[1].Value;
        var all = new List<KeyValuePair<string, string>>(fields) { new("__RequestVerificationToken", token) };
        // Expand multi-value fields given as "a\nb" only for AddPresets-style lists: keep simple.
        return await b.PostAsync(url, new FormUrlEncodedContent(all));
    }

    /// <summary>RFC 6238 TOTP (SHA-1, 30 s, 6 digits) – what Google Authenticator computes.</summary>
    private static string Totp(string base32)
    {
        var key = Base32(base32);
        var counter = DateTimeOffset.UtcNow.ToUnixTimeSeconds() / 30;
        Span<byte> msg = stackalloc byte[8];
        BinaryPrimitives.WriteInt64BigEndian(msg, counter);
        var hash = HMACSHA1.HashData(key, msg);
        var offset = hash[^1] & 0xF;
        var bin = ((hash[offset] & 0x7F) << 24) | (hash[offset + 1] << 16) | (hash[offset + 2] << 8) | hash[offset + 3];
        return (bin % 1_000_000).ToString("D6");
    }

    private static byte[] Base32(string s)
    {
        const string alphabet = "ABCDEFGHIJKLMNOPQRSTUVWXYZ234567";
        var bits = 0;
        var value = 0;
        var output = new List<byte>();
        foreach (var c in s.TrimEnd('='))
        {
            value = (value << 5) | alphabet.IndexOf(c);
            bits += 5;
            if (bits >= 8)
            {
                output.Add((byte)(value >> (bits - 8)));
                bits -= 8;
            }
        }
        return output.ToArray();
    }

    [GeneratedRegex("name=\"__RequestVerificationToken\" type=\"hidden\" value=\"([^\"]+)\"")]
    private static partial Regex TokenRegex();

    [GeneratedRegex("id=\"invite-link\"[^>]*value=\"([^\"]+)\"")]
    private static partial Regex InviteLinkRegex();

    [GeneratedRegex("manuell eingeben: <code>([a-z0-9 ]+)</code>")]
    private static partial Regex SharedKeyRegex();

    [GeneratedRegex("class=\"code-box my-2\">(\\d{5}-\\d{5})<")]
    private static partial Regex CodeRegex();
}
