using System.Net;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using PaCo.Server.Data;
using PaCo.Server.Services;
using PaCo.Shared;
using PaCo.Shared.I18n;

namespace PaCo.Tests;

/// <summary>Regression tests for the findings of the security review (pentest).</summary>
public partial class WebFlowTests
{
    private async Task<(HttpClient Parent, Guid Family, Guid Child, Guid Device, HttpClient DeviceApi)> FamilyWithDeviceAsync(string mail)
    {
        var (b, fam) = await ParentOfNewFamilyAsync(mail);
        var token = Secrets.DeviceToken();
        Guid child, device;
        using (var scope = _f.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<PaCoDb>();
            var c = new Child { FamilyId = fam, Name = "Kim" };
            var d = new Device { FamilyId = fam, Name = "PC", TokenHash = Secrets.Sha256Hex(token) };
            db.Children.Add(c);
            db.Devices.Add(d);
            db.DeviceAccounts.Add(new DeviceAccount { FamilyId = fam, DeviceId = d.Id, Sid = "S-1-5-21-4-4-" + Random.Shared.Next(1000, 9999) + "-1001", UserName = "kim", ChildId = c.Id });
            await db.SaveChangesAsync();
            (child, device) = (c.Id, d.Id);
        }
        var api = _f.Https();
        api.DefaultRequestHeaders.Authorization = new("Device", $"{device}:{token}");
        return (b, fam, child, device, api);
    }

    [Fact]
    public async Task ForgedFormatTemplatesFromADeviceCannotBreakOrBloatPages()
    {
        var (b, _, child, device, api) = await FamilyWithDeviceAsync("sec-templates@test.local");
        var evil = string.Concat(Enumerable.Repeat("{0,999999}", 50));
        var sync = await api.PostAsJsonAsync(ApiRoutes.Sync, new SyncRequest
        {
            Events =
            [
                new DeviceEvent { TimeUtc = DateTimeOffset.UtcNow, Kind = DeviceEventKind.ClockTampering, ChildId = child, Message = "a", Template = "Uhr {3}", Args = ["x"] },
                new DeviceEvent { TimeUtc = DateTimeOffset.UtcNow, Kind = DeviceEventKind.ClockTampering, ChildId = child, Message = "b", Template = evil, Args = ["x"] },
                // Known template, but alignment trick in place of the plain placeholder.
                new DeviceEvent { TimeUtc = DateTimeOffset.UtcNow, Kind = DeviceEventKind.ClockTampering, ChildId = child, Message = "c",
                    Template = "Windows-Uhr wurde um {0,999999} Minuten zurückgestellt – wird ignoriert.", Args = ["5"] },
            ],
            Security = [new SecurityFinding("x", SecuritySeverity.Critical, "kaputt") { Template = "{1}" + evil, Args = ["a"] }],
        }, PaCoJson.Options);
        Assert.Equal(HttpStatusCode.OK, sync.StatusCode);

        using (var scope = _f.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<PaCoDb>();
            Assert.All(await db.DeviceEvents.IgnoreQueryFilters().Where(e => e.DeviceId == device).ToListAsync(), e => Assert.Null(e.Template));
        }
        var dash = await b.GetAsync("/Dashboard");
        Assert.Equal(HttpStatusCode.OK, dash.StatusCode);
        Assert.True((await dash.Content.ReadAsStringAsync()).Length < 500_000);
        var log = await b.GetAsync($"/Devices/Log/{device}");
        Assert.Equal(HttpStatusCode.OK, log.StatusCode);
        Assert.True((await log.Content.ReadAsStringAsync()).Length < 500_000);

        // A genuine template is still translated.
        Assert.True(SharedText.IsSafeTemplate("Windows-Uhr wurde um {0} Minuten zurückgestellt – wird ignoriert.", 1));
        Assert.False(SharedText.IsSafeTemplate("Windows-Uhr wurde um {0} Minuten zurückgestellt – wird ignoriert.", 0));
    }

    [Fact]
    public async Task MalformedDeviceDataDoesNotBreakSyncOrPages()
    {
        var (b, _, child, device, api) = await FamilyWithDeviceAsync("sec-malformed@test.local");
        var id = Guid.NewGuid();
        var sync = await api.PostAsJsonAsync(ApiRoutes.Sync, new SyncRequest
        {
            Events =
            [
                new DeviceEvent { Id = id, TimeUtc = DateTimeOffset.UtcNow, Kind = DeviceEventKind.Info, Message = "1" },
                new DeviceEvent { Id = id, TimeUtc = DateTimeOffset.UtcNow.AddYears(-5), Kind = DeviceEventKind.Info, Message = "dup" },
            ],
            Usage =
            [
                new UsageReport
                {
                    ChildId = child, Day = FamilyClock.Day(DateTimeOffset.UtcNow, FamilyClock.Resolve("Europe/Berlin")), RegularSeconds = 60,
                    Apps = new Dictionary<string, AppUsage> { ["Game"] = new() { DisplayName = "G", Seconds = 10 }, ["game"] = new() { DisplayName = "g", Seconds = 20 } },
                },
            ],
        }, PaCoJson.Options);
        Assert.Equal(HttpStatusCode.OK, sync.StatusCode);
        foreach (var url in new[] { "/Dashboard", $"/Children/Stats/{child}", $"/Children/Edit/{child}", $"/Devices/Log/{device}" })
            Assert.Equal(HttpStatusCode.OK, (await b.GetAsync(url)).StatusCode);
        using var scope = _f.Services.CreateScope();
        var ev = await scope.ServiceProvider.GetRequiredService<PaCoDb>().DeviceEvents.IgnoreQueryFilters().SingleAsync(e => e.Id == id);
        Assert.True(ev.TimeUtc > DateTimeOffset.UtcNow.AddDays(-15));
    }

    [Fact]
    public async Task DeviceApiRejectsStrangersBeforeReadingTheBody()
    {
        var anon = _f.Https();
        var r = await anon.PostAsync(ApiRoutes.Sync, new StringContent(new string('x', 400_000), System.Text.Encoding.UTF8, "application/json"));
        Assert.Equal(HttpStatusCode.Unauthorized, r.StatusCode);
    }

    [Fact]
    public async Task UnlockKeysArePerChildAndNeverTheFamilyKey()
    {
        var (_, fam, child, device, api) = await FamilyWithDeviceAsync("sec-keys@test.local");
        var resp = await (await api.PostAsJsonAsync(ApiRoutes.Sync, new SyncRequest(), PaCoJson.Options)).Content.ReadFromJsonAsync<SyncResponse>(PaCoJson.Options);
        byte[] familyKey;
        string publicKey;
        using (var scope = _f.Services.CreateScope())
        {
            familyKey = (await scope.ServiceProvider.GetRequiredService<PaCoDb>().Families.SingleAsync(f => f.Id == fam)).UnlockKey;
            publicKey = scope.ServiceProvider.GetRequiredService<SigningKeyStore>().PublicKeyPem;
        }
        var policy = PolicySigning.Verify<PolicyDocument>(resp!.Policy, publicKey);
        var childPolicy = Assert.Single(policy.Children);
        Assert.Equal(Convert.ToBase64String(UnlockCodes.ChildKey(familyKey, child)), childPolicy.UnlockKey);
        Assert.NotEqual(Convert.ToBase64String(familyKey), childPolicy.UnlockKey);
        Assert.Equal(SignedKinds.Policy, policy.Kind);
    }

    [Fact]
    public void SignedDocumentsCannotBeSwapped()
    {
        using var key = PolicySigning.CreateKey();
        var pem = PolicySigning.ExportPublicPem(key);
        var manifest = PolicySigning.Sign(new UpdateManifest { DeviceId = Guid.NewGuid(), Version = "9.9.9", IssuedUtc = DateTimeOffset.UtcNow }, key);
        // Valid signature – but as rules it has the wrong type.
        var asPolicy = PolicySigning.Verify<PolicyDocument>(manifest, pem);
        Assert.Throws<PolicySignatureException>(() => SignedKinds.Expect(asPolicy.Kind, SignedKinds.Policy));
        var time = PolicySigning.Sign(new ServerTime { DeviceId = Guid.NewGuid(), Nonce = "n", ServerUtc = DateTimeOffset.UtcNow }, key);
        Assert.Throws<PolicySignatureException>(() => SignedKinds.Expect(PolicySigning.Verify<UpdateManifest>(time, pem).Kind, SignedKinds.Update));
    }

    [Fact]
    public async Task AuthenticatorCodeWorksOnlyOnceAndLogoutEndsTheSession()
    {
        const string mail = "sec-totp@test.local", pw = "Eltern-Passwort-123";
        string key;
        using (var scope = _f.Services.CreateScope())
        {
            var admin = scope.ServiceProvider.GetRequiredService<AccountAdmin>();
            var users = scope.ServiceProvider.GetRequiredService<UserManager<ParentUser>>();
            var fid = (await admin.CreateFamilyAsync("Fam-totp")).Id;
            var u = new ParentUser { UserName = mail, Email = mail, DisplayName = mail, FamilyId = fid };
            Assert.True((await users.CreateAsync(u, pw)).Succeeded);
            await users.ResetAuthenticatorKeyAsync(u);
            key = (await users.GetAuthenticatorKeyAsync(u))!;
            await users.SetTwoFactorEnabledAsync(u, true);
        }
        var a = Browser();
        await LoginAsync(a, mail, pw, key);
        Assert.Equal(HttpStatusCode.OK, (await a.GetAsync("/Dashboard")).StatusCode);

        // Same code again from another browser (shoulder surfing): rejected.
        var b = Browser();
        var page = await b.GetStringAsync("/Account/Login");
        var r = await PostFormAsync(b, "/Account/Login", page, new() { ["Email"] = mail, ["Password"] = pw });
        var twoFa = await b.GetStringAsync(r.Headers.Location!.ToString());
        var r2 = await PostFormAsync(b, "/Account/LoginWith2fa", twoFa, new() { ["Code"] = Totp(key), ["RememberMe"] = "false" });
        Assert.NotEqual("/", r2.Headers.Location?.ToString());

        // Sign out: the session is deleted on the server, so even a copied cookie no longer works.
        var dash = await a.GetStringAsync("/Dashboard");
        await PostFormAsync(a, "/Account/Logout", dash, new());
        Assert.NotEqual(HttpStatusCode.OK, (await a.GetAsync("/Dashboard")).StatusCode);
        using (var scope = _f.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<PaCoDb>();
            var users = scope.ServiceProvider.GetRequiredService<UserManager<ParentUser>>();
            var uid = (await users.FindByEmailAsync(mail))!.Id;
            Assert.False(await db.AuthSessions.AnyAsync(s => s.UserId == uid)); // server-side session gone
        }
    }

    [Fact]
    public async Task LoginErrorsDoNotRevealWhichAccountsExist()
    {
        var b = Browser();
        var page = await b.GetStringAsync("/Account/Login");
        var unknown = await (await PostFormAsync(b, "/Account/Login", page, new() { ["Email"] = "nobody@test.local", ["Password"] = "Falsch-Passwort-1" })).Content.ReadAsStringAsync();
        page = await b.GetStringAsync("/Account/Login");
        var known = await (await PostFormAsync(b, "/Account/Login", page, new() { ["Email"] = "sec-totp@test.local", ["Password"] = "Falsch-Passwort-1" })).Content.ReadAsStringAsync();
        Assert.Contains("Anmeldung fehlgeschlagen", unknown);
        Assert.Contains("Anmeldung fehlgeschlagen", known);
    }
}
