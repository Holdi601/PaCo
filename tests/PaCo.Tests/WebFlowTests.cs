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

    /// <summary>A German-speaking parent's browser (the assertions check German page texts).</summary>
    private HttpClient Browser(string lang = "de")
    {
        var c = _f.CreateClient(new WebApplicationFactoryClientOptions
        {
            BaseAddress = new Uri("https://localhost"),
            AllowAutoRedirect = false,
            HandleCookies = true,
        });
        c.DefaultRequestHeaders.AcceptLanguage.ParseAdd(lang);
        return c;
    }

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
        Assert.Equal(HttpStatusCode.OK, (await adminBrowser.GetAsync("/Admin/Health")).StatusCode);
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

        // Location: only recorded when the parents enabled it; strictly per family.
        await devA.PostAsJsonAsync(ApiRoutes.Sync, new SyncRequest { Locations = [new LocationPoint { TimeUtc = DateTimeOffset.UtcNow, Latitude = 52.52, Longitude = 13.405, AccuracyMeters = 30 }] }, PaCoJson.Options);
        using (var scope = _f.Services.CreateScope())
            Assert.Equal(0, await scope.ServiceProvider.GetRequiredService<PaCoDb>().Locations.IgnoreQueryFilters().CountAsync(l => l.DeviceId == deviceA));
        var locUrl = $"/Devices/Location/{deviceA}";
        var locPage = await a.GetStringAsync(locUrl);
        Assert.Equal(HttpStatusCode.Redirect, (await PostFormAsync(a, $"{locUrl}?handler=Locate", locPage, [])).StatusCode);
        var resp = await (await devA.PostAsJsonAsync(ApiRoutes.Sync, new SyncRequest(), PaCoJson.Options)).Content.ReadFromJsonAsync<SyncResponse>(PaCoJson.Options);
        Assert.True(resp!.LocateNow);
        Assert.Equal(LocationMode.OnRequest, resp.LocationMode);
        await devA.PostAsJsonAsync(ApiRoutes.Sync, new SyncRequest { Locations = [new LocationPoint { TimeUtc = DateTimeOffset.UtcNow, Latitude = 52.52, Longitude = 13.405, AccuracyMeters = 30, Ssid = "Heimnetz" }] }, PaCoJson.Options);
        Assert.Contains("52.52000", await a.GetStringAsync(locUrl));
        Assert.Equal(HttpStatusCode.NotFound, (await b.GetAsync(locUrl)).StatusCode);
        var bDevices = await b.GetStringAsync("/Devices/Index");
        Assert.Equal(HttpStatusCode.NotFound, (await PostFormAsync(b, $"{locUrl}?handler=Locate", bDevices, [])).StatusCode);

        // Diagnostics: only uploaded on request, readable only by the own family.
        Assert.Equal(HttpStatusCode.NotFound, (await devA.PostAsync(ApiRoutes.Diagnostics, new StringContent("unaufgefordert"))).StatusCode);
        var logUrl = $"/Devices/Log/{deviceA}";
        Assert.Equal(HttpStatusCode.Redirect, (await PostFormAsync(a, $"{logUrl}?handler=Request", await a.GetStringAsync(logUrl), [])).StatusCode);
        var r2 = await (await devA.PostAsJsonAsync(ApiRoutes.Sync, new SyncRequest
        {
            Events = [new DeviceEvent { TimeUtc = DateTimeOffset.UtcNow, Kind = DeviceEventKind.SafeMode, Message = "Abgesicherter Modus erkannt" }],
        }, PaCoJson.Options)).Content.ReadFromJsonAsync<SyncResponse>(PaCoJson.Options);
        Assert.True(r2!.SendDiagnostics);
        Assert.Equal(HttpStatusCode.OK, (await devA.PostAsync(ApiRoutes.Diagnostics, new StringContent("2026-11-15 [INFO] Testzeile Diagnose"))).StatusCode);
        var logPage = await a.GetStringAsync(logUrl);
        Assert.Contains("Testzeile Diagnose", logPage);
        Assert.Contains("Abgesicherter Modus", logPage);
        Assert.Equal(HttpStatusCode.NotFound, (await b.GetAsync(logUrl)).StatusCode);
        // Parents cannot open the admin health page.
        Assert.NotEqual(HttpStatusCode.OK, (await a.GetAsync("/Admin/Health")).StatusCode);
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

    [Fact]
    public async Task ParentsCanBlockAndAllowProgramsAndWindowsToolsAgain()
    {
        var (b, fam) = await ParentOfNewFamilyAsync("toggle-parent@test.local");
        Guid child;
        using (var scope = _f.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<PaCoDb>();
            var c = new Child { FamilyId = fam, Name = "Tom" };
            db.Children.Add(c);
            var d = new Device { FamilyId = fam, Name = "PC", TokenHash = Secrets.Sha256Hex(Secrets.DeviceToken()) };
            db.Devices.Add(d);
            db.DeviceAccounts.Add(new DeviceAccount { FamilyId = fam, DeviceId = d.Id, Sid = "S-1-5-21-5-5-5-1001", UserName = "tom", ChildId = c.Id });
            db.UsageDays.Add(new UsageDay
            {
                FamilyId = fam, DeviceId = d.Id, ChildId = c.Id, Day = DateOnly.FromDateTime(DateTime.Today), RegularSeconds = 600,
                AppsJson = PaCoJson.Serialize(new Dictionary<string, AppUsage> { ["roblox"] = new() { DisplayName = "Roblox", Seconds = 600 } }),
            });
            await db.SaveChangesAsync();
            child = c.Id;
        }
        PolicyDocument Policy()
        {
            using var scope = _f.Services.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<PaCoDb>();
            var dev = db.Devices.IgnoreQueryFilters().Single(x => x.FamilyId == fam);
            return scope.ServiceProvider.GetRequiredService<PolicyBuilder>().BuildAsync(dev, default).GetAwaiter().GetResult();
        }

        // Default: all Windows tools + Store blocked.
        var p = Policy().Children.Single();
        Assert.Contains("regedit", p.BlockedApps);
        Assert.Contains(Child.StoreApp, p.BlockedApps);

        // One tap on the statistics page blocks a program, a second tap allows it again.
        var stats = await b.GetStringAsync($"/Children/Stats/{child}");
        Assert.Contains("roblox", stats);
        await PostFormAsync(b, $"/Children/Stats/{child}?handler=ToggleApp", stats, new() { ["app"] = "roblox" });
        Assert.Contains("roblox", Policy().Children.Single().BlockedApps);
        stats = await b.GetStringAsync($"/Children/Stats/{child}");
        await PostFormAsync(b, $"/Children/Stats/{child}?handler=ToggleApp", stats, new() { ["app"] = "roblox" });
        Assert.DoesNotContain("roblox", Policy().Children.Single().BlockedApps);

        // Rules page: the used program appears as a switch; tools can be allowed one by one, the Store too.
        var edit = await b.GetStringAsync($"/Children/Edit/{child}");
        Assert.Contains("name=\"BlockedKnownApps\" value=\"roblox\"", edit);
        var token = TokenRegex().Match(edit).Groups[1].Value;
        var form = new List<KeyValuePair<string, string>>
        {
            new("__RequestVerificationToken", token), new("Name", "Tom"), new("ExpiryAction", "LockScreen"),
            new("ToolsForm", "true"), new("BlockedTools", "powershell"), new("BlockedTools", "regedit"),
            new("KnownAppsForm", "true"), new("BlockedKnownApps", "roblox"), new("BlockStore", "false"),
        };
        for (var i = 0; i < 7; i++)
        {
            form.Add(new($"Days[{i}].LimitMinutes", "120"));
            form.Add(new($"Days[{i}].AllDay", "true"));
        }
        var r = await b.PostAsync($"/Children/Edit/{child}", new FormUrlEncodedContent(form));
        Assert.Equal(HttpStatusCode.Redirect, r.StatusCode);
        p = Policy().Children.Single();
        Assert.Contains("powershell", p.BlockedApps);
        Assert.Contains("regedit", p.BlockedApps);
        Assert.Contains("roblox", p.BlockedApps);
        Assert.DoesNotContain("cmd", p.BlockedApps);       // allowed again
        Assert.DoesNotContain("winget", p.BlockedApps);    // allowed again
        Assert.DoesNotContain(Child.StoreApp, p.BlockedApps);
    }

    [Fact]
    public async Task TimeLimitSharedAcrossPcsPerPcOrNotOnSomePcs()
    {
        var (b, fam) = await ParentOfNewFamilyAsync("budget-parent@test.local");
        Guid child, gaming, school;
        var today = DateOnly.FromDateTime(DateTime.Today);
        using (var scope = _f.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<PaCoDb>();
            var c = new Child { FamilyId = fam, Name = "Lea" };
            var g = new Device { FamilyId = fam, Name = "Gaming-PC", TokenHash = Secrets.Sha256Hex(Secrets.DeviceToken()) };
            var l = new Device { FamilyId = fam, Name = "Schul-Laptop", TokenHash = Secrets.Sha256Hex(Secrets.DeviceToken()) };
            db.Children.Add(c);
            db.Devices.AddRange(g, l);
            db.DeviceAccounts.Add(new DeviceAccount { FamilyId = fam, DeviceId = g.Id, Sid = "S-1-5-21-8-8-8-1001", UserName = "lea", ChildId = c.Id });
            db.DeviceAccounts.Add(new DeviceAccount { FamilyId = fam, DeviceId = l.Id, Sid = "S-1-5-21-9-8-8-1001", UserName = "lea", ChildId = c.Id });
            db.UsageDays.Add(new UsageDay { FamilyId = fam, DeviceId = l.Id, ChildId = c.Id, Day = today, RegularSeconds = 3600 });
            await db.SaveChangesAsync();
            (child, gaming, school) = (c.Id, g.Id, l.Id);
        }
        ChildPolicy PolicyOn(Guid deviceId)
        {
            using var scope = _f.Services.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<PaCoDb>();
            var dev = db.Devices.IgnoreQueryFilters().Single(x => x.Id == deviceId);
            return scope.ServiceProvider.GetRequiredService<PolicyBuilder>().BuildAsync(dev, default).GetAwaiter().GetResult().Children.Single();
        }
        async Task SaveAsync(bool perDevice, params Guid[] limited)
        {
            var edit = await b.GetStringAsync($"/Children/Edit/{child}");
            Assert.Contains("name=\"TimeLimitDevices\"", edit);
            var form = new List<KeyValuePair<string, string>>
            {
                new("__RequestVerificationToken", TokenRegex().Match(edit).Groups[1].Value), new("Name", "Lea"), new("ExpiryAction", "LockScreen"),
                new("TimeDevicesForm", "true"), new("TimePerDevice", perDevice ? "true" : "false"),
            };
            form.AddRange(limited.Select(id => new KeyValuePair<string, string>("TimeLimitDevices", id.ToString())));
            for (var i = 0; i < 7; i++) { form.Add(new($"Days[{i}].LimitMinutes", "120")); form.Add(new($"Days[{i}].AllDay", "true")); }
            Assert.Equal(HttpStatusCode.Redirect, (await b.PostAsync($"/Children/Edit/{child}", new FormUrlEncodedContent(form))).StatusCode);
        }

        // Default: shared – the hour on the laptop counts on the gaming PC.
        Assert.Equal(3600, PolicyOn(gaming).RegularSecondsElsewhere[today]);

        // Every PC has the full time.
        await SaveAsync(perDevice: true, gaming, school);
        Assert.Empty(PolicyOn(gaming).RegularSecondsElsewhere);
        Assert.Equal(120, PolicyOn(gaming).Days[0].LimitMinutes);

        // Shared again, but no time limit on the school laptop: unrestricted there, its hour no longer counts elsewhere.
        await SaveAsync(perDevice: false, gaming);
        Assert.Empty(PolicyOn(gaming).RegularSecondsElsewhere);
        Assert.Equal(120, PolicyOn(gaming).Days[3].LimitMinutes);
        Assert.All(PolicyOn(school).Days, d => Assert.Null(d.LimitMinutes));
        var dash = await b.GetStringAsync("/Dashboard");
        Assert.Contains("ohne Zeitlimit", dash);
    }

    [Fact]
    public async Task EveryPageRendersInEveryLanguage()
    {
        var (b, fam) = await ParentOfNewFamilyAsync("lang-parent@test.local");
        Guid child, device;
        using (var scope = _f.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<PaCoDb>();
            var c = new Child { FamilyId = fam, Name = "Mia" };
            var d = new Device { FamilyId = fam, Name = "Laptop", TokenHash = Secrets.Sha256Hex(Secrets.DeviceToken()), LastSeenUtc = DateTimeOffset.UtcNow };
            db.Children.Add(c);
            db.Devices.Add(d);
            db.DeviceAccounts.Add(new DeviceAccount { FamilyId = fam, DeviceId = d.Id, Sid = "S-1-5-21-9-9-9-1001", UserName = "mia", ChildId = c.Id });
            var ev = PaCo.Shared.I18n.LocText.Of("Freischaltcode eingelöst: für {0} Minuten", 30);
            db.DeviceEvents.Add(new DeviceEventEntity { FamilyId = fam, DeviceId = d.Id, ChildId = c.Id, TimeUtc = DateTimeOffset.UtcNow, Kind = DeviceEventKind.Unlocked, Message = ev.German, Template = ev.Template, ArgsJson = PaCoJson.Serialize(ev.Args) });
            db.Alerts.Add(new AlertEntity { FamilyId = fam, ChildId = c.Id, DeviceId = d.Id, TimeUtc = DateTimeOffset.UtcNow, Category = AlertCategory.SelfHarm, Keyword = "suizid", App = "chrome", Snippet = "…" });
            await db.SaveChangesAsync();
            (child, device) = (c.Id, d.Id);
        }

        string[] pages = ["/Dashboard", "/Children", $"/Children/Edit/{child}", $"/Children/Stats/{child}", "/Devices",
            $"/Devices/Log/{device}", $"/Devices/Location/{device}", "/Alerts", "/Family", "/Notifications", "/Account/Manage"];
        foreach (var lang in PaCo.Shared.I18n.Languages.All)
        {
            var page = await b.GetStringAsync("/Account/Manage");
            var set = await PostFormAsync(b, "/Language", page, new() { ["lang"] = lang.Code, ["returnUrl"] = "/Dashboard" });
            Assert.Equal(HttpStatusCode.Redirect, set.StatusCode);
            foreach (var p in pages)
            {
                var r = await b.GetAsync(p);
                Assert.True(r.StatusCode == HttpStatusCode.OK, $"{lang.Code} {p}: {(int)r.StatusCode}");
                var html = await r.Content.ReadAsStringAsync();
                Assert.Contains($"<html lang=\"{lang.Code}\"", html);
            }
        }

        // The choice is stored in the account (used again at the next sign-in).
        using (var scope = _f.Services.CreateScope())
        {
            var users = scope.ServiceProvider.GetRequiredService<UserManager<ParentUser>>();
            Assert.Equal(PaCo.Shared.I18n.Languages.All[^1].Code, (await users.FindByEmailAsync("lang-parent@test.local"))!.Language);
        }
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
