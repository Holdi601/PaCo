using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using PaCo.Server.Data;
using PaCo.Server.Services;
using PaCo.Shared;

namespace PaCo.Tests;

public sealed class PaCoFactory : WebApplicationFactory<Program>
{
    public string DataDir { get; } = Path.Combine(Path.GetTempPath(), "paco-test-" + Guid.NewGuid().ToString("N"));

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseSetting("PaCo:DataDir", DataDir);
        builder.UseSetting("PaCo:AccountRateLimit", "1000");
        builder.UseEnvironment("Development");
    }

    public HttpClient Https() => CreateClient(new WebApplicationFactoryClientOptions { BaseAddress = new Uri("https://localhost"), AllowAutoRedirect = false });

    protected override void Dispose(bool disposing)
    {
        base.Dispose(disposing);
        try { Directory.Delete(DataDir, true); } catch { /* best effort */ }
    }
}

file sealed class FixedFamily(Guid? id) : IFamilyContext
{
    public Guid? FamilyId => id;
}

public class TenantIsolationTests : IClassFixture<PaCoFactory>
{
    private readonly PaCoFactory _f;

    public TenantIsolationTests(PaCoFactory f) => _f = f;

    private PaCoDb Db(Guid? family) =>
        new(new DbContextOptionsBuilder<PaCoDb>().UseSqlite($"Data Source={Path.Combine(_f.DataDir, "paco.db")}").Options, new FixedFamily(family));

    [Fact]
    public async Task ParentOfOneFamilyCannotSeeOrChangeAnotherFamily()
    {
        _ = _f.Services; // ensure migrated
        Guid famA, famB, childB;
        await using (var admin = Db(null))
        {
            var a = new Family { Name = "A", UnlockKey = RandomNumberGenerator.GetBytes(32) };
            var b = new Family { Name = "B", UnlockKey = RandomNumberGenerator.GetBytes(32) };
            admin.Families.AddRange(a, b);
            var cb = new Child { FamilyId = b.Id, Name = "KindB" };
            admin.Children.AddRange(new Child { FamilyId = a.Id, Name = "KindA" }, cb);
            await admin.SaveChangesAsync();
            (famA, famB, childB) = (a.Id, b.Id, cb.Id);
        }

        await using (var asA = Db(famA))
        {
            var visible = await asA.Children.Select(c => c.Name).ToListAsync();
            Assert.Contains("KindA", visible);
            Assert.DoesNotContain("KindB", visible);
            Assert.Null(await asA.Children.FirstOrDefaultAsync(c => c.Id == childB));

            // Even a forged write is refused.
            asA.Children.Add(new Child { FamilyId = famB, Name = "Eingeschleust" });
            await Assert.ThrowsAsync<InvalidOperationException>(() => asA.SaveChangesAsync());
        }

        // Without a family context nothing family-scoped is visible (deny by default).
        await using var none = Db(null);
        Assert.Empty(await none.Children.ToListAsync());
    }

    [Fact]
    public async Task PagesRequireLogin()
    {
        var client = _f.Https();
        foreach (var url in new[] { "/Dashboard", "/Children/Index", "/Devices/Index", "/Admin/Index", "/Account/Manage" })
        {
            var r = await client.GetAsync(url);
            Assert.Equal(HttpStatusCode.Redirect, r.StatusCode);
            Assert.Contains("/Account/Login", r.Headers.Location!.ToString());
        }
        var login = await client.GetAsync("/Account/Login");
        Assert.Equal(HttpStatusCode.OK, login.StatusCode);
        Assert.Contains("default-src 'self'", login.Headers.GetValues("Content-Security-Policy").Single());
    }
}

public class DeviceApiTests : IClassFixture<PaCoFactory>
{
    private readonly PaCoFactory _f;

    public DeviceApiTests(PaCoFactory f) => _f = f;

    private async Task<(Guid Family, string Code)> NewFamilyWithPairingCodeAsync()
    {
        using var scope = _f.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<PaCoDb>();
        var fam = new Family { Name = "Test", UnlockKey = RandomNumberGenerator.GetBytes(32) };
        db.Families.Add(fam);
        var code = Secrets.PairingCode();
        db.PairingCodes.Add(new PairingCode { FamilyId = fam.Id, CodeHash = Secrets.Sha256Hex(Secrets.NormalizeCode(code)), ExpiresUtc = DateTimeOffset.UtcNow.AddHours(1) });
        await db.SaveChangesAsync();
        return (fam.Id, code);
    }

    [Fact]
    public async Task PairAssignSyncRoundtrip()
    {
        var (_, code) = await NewFamilyWithPairingCodeAsync();
        var http = _f.Https();
        var sid = "S-1-5-21-1-2-3-1001";

        var pair = await http.PostAsJsonAsync(ApiRoutes.Pair, new PairRequest
        {
            PairingCode = code.ToLowerInvariant().Replace("-", " "),
            MachineName = "LAPTOP-LENA",
            Accounts = [new WindowsAccount { Sid = sid, UserName = "lena" }, new WindowsAccount { Sid = "S-1-5-21-1-2-3-500", UserName = "mama", IsAdmin = true }],
        }, PaCoJson.Options);
        Assert.Equal(HttpStatusCode.OK, pair.StatusCode);
        var paired = (await pair.Content.ReadFromJsonAsync<PairResponse>(PaCoJson.Options))!;

        // Code is single use.
        var again = await http.PostAsJsonAsync(ApiRoutes.Pair, new PairRequest { PairingCode = code }, PaCoJson.Options);
        Assert.Equal(HttpStatusCode.BadRequest, again.StatusCode);

        http.DefaultRequestHeaders.Authorization = new("Device", $"{paired.DeviceId}:{paired.DeviceToken}");
        var assign = await http.PostAsJsonAsync(ApiRoutes.Assign, new AssignRequest
        {
            Assignments = [new AccountAssignment { Sid = sid, NewChildName = "Lena" }],
        }, PaCoJson.Options);
        Assert.Equal(HttpStatusCode.OK, assign.StatusCode);
        var children = (await assign.Content.ReadFromJsonAsync<List<ChildRef>>(PaCoJson.Options))!;
        var lena = Assert.Single(children);

        var sync = await http.PostAsJsonAsync(ApiRoutes.Sync, new SyncRequest
        {
            ClientUtcNow = DateTimeOffset.UtcNow,
            ActiveChildren = [lena.Id],
            Events = [new DeviceEvent { TimeUtc = DateTimeOffset.UtcNow, Kind = DeviceEventKind.ForcedLogoff, ChildId = lena.Id, Message = "Test" }],
            Live = [new ChildLiveStatus { ChildId = lena.Id, Allowed = true, RemainingSeconds = 1234, Active = true, CurrentApp = "Minecraft" }],
            Usage = [new UsageReport
            {
                ChildId = lena.Id, Day = DateOnly.FromDateTime(DateTime.Today), RegularSeconds = 600,
                Apps = new Dictionary<string, AppUsage> { ["minecraft"] = new() { DisplayName = "Minecraft", Seconds = 600 } },
            }],
        }, PaCoJson.Options);
        Assert.Equal(HttpStatusCode.OK, sync.StatusCode);
        var resp = (await sync.Content.ReadFromJsonAsync<SyncResponse>(PaCoJson.Options))!;

        var policy = PolicySigning.Verify<PolicyDocument>(resp.Policy, paired.PolicyPublicKeyPem);
        Assert.Equal(paired.DeviceId, policy.DeviceId);
        Assert.Equal(lena.Id, policy.ChildForSid(sid)!.ChildId);
        Assert.Null(policy.ChildForSid("S-1-5-21-1-2-3-500"));
        Assert.Equal(7, policy.Children[0].Days.Count);

        using var scope = _f.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<PaCoDb>();
        var usage = await db.UsageDays.IgnoreQueryFilters().SingleAsync(u => u.ChildId == lena.Id);
        Assert.Equal(600, usage.RegularSeconds);
        var dev = await db.Devices.IgnoreQueryFilters().SingleAsync(d => d.Id == paired.DeviceId);
        var live = Assert.Single(dev.GetLive());
        Assert.Equal(1234, live.RemainingSeconds);
        Assert.Equal("Minecraft", live.CurrentApp);
        Assert.Equal(DeviceEventKind.ForcedLogoff, (await db.DeviceEvents.IgnoreQueryFilters().SingleAsync(e => e.DeviceId == paired.DeviceId)).Kind);
    }

    [Fact]
    public async Task UpdateIsOfferedSignedOnlyWhenRequested()
    {
        var (_, code) = await NewFamilyWithPairingCodeAsync();
        var http = _f.Https();
        var paired = (await (await http.PostAsJsonAsync(ApiRoutes.Pair, new PairRequest { PairingCode = code, ClientVersion = "1.0.0" }, PaCoJson.Options))
            .Content.ReadFromJsonAsync<PairResponse>(PaCoJson.Options))!;
        http.DefaultRequestHeaders.Authorization = new("Device", $"{paired.DeviceId}:{paired.DeviceToken}");

        // A newer build "downloaded from GitHub".
        var dir = Path.Combine(_f.DataDir, "client-updates");
        Directory.CreateDirectory(dir);
        var bytes = RandomNumberGenerator.GetBytes(4096);
        await File.WriteAllBytesAsync(Path.Combine(dir, "PaCo-Setup-9.9.9.exe"), bytes);

        async Task<SyncResponse> Sync(string version) =>
            (await (await http.PostAsJsonAsync(ApiRoutes.Sync, new SyncRequest { ClientVersion = version }, PaCoJson.Options))
                .Content.ReadFromJsonAsync<SyncResponse>(PaCoJson.Options))!;

        Assert.Null((await Sync("1.0.0")).Update); // not requested yet

        using (var scope = _f.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<PaCoDb>();
            (await db.Devices.IgnoreQueryFilters().SingleAsync(d => d.Id == paired.DeviceId)).UpdateRequested = true;
            await db.SaveChangesAsync();
        }

        var resp = await Sync("1.0.0");
        Assert.NotNull(resp.Update);
        var manifest = PolicySigning.Verify<UpdateManifest>(resp.Update!, paired.PolicyPublicKeyPem);
        Assert.Equal("9.9.9", manifest.Version);
        Assert.Equal(paired.DeviceId, manifest.DeviceId);
        Assert.Equal(Convert.ToHexStringLower(SHA256.HashData(bytes)), manifest.Sha256);
        Assert.Equal(bytes, await http.GetByteArrayAsync(ApiRoutes.ClientDownload));

        // After the update the device reports the new version → nothing offered any more.
        Assert.Null((await Sync("9.9.9")).Update);
        File.Delete(Path.Combine(dir, "PaCo-Setup-9.9.9.exe"));
    }

    [Fact]
    public async Task RejectsWrongTokenAndForeignChild()
    {
        var (_, codeA) = await NewFamilyWithPairingCodeAsync();
        var (famB, _) = await NewFamilyWithPairingCodeAsync();
        Guid foreignChild;
        using (var scope = _f.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<PaCoDb>();
            var c = new Child { FamilyId = famB, Name = "Fremd" };
            db.Children.Add(c);
            await db.SaveChangesAsync();
            foreignChild = c.Id;
        }

        var http = _f.Https();
        var pair = await http.PostAsJsonAsync(ApiRoutes.Pair, new PairRequest { PairingCode = codeA, Accounts = [new WindowsAccount { Sid = "S-1-5-21-9-9-9-1001", UserName = "kid" }] }, PaCoJson.Options);
        var paired = (await pair.Content.ReadFromJsonAsync<PairResponse>(PaCoJson.Options))!;

        http.DefaultRequestHeaders.Authorization = new("Device", $"{paired.DeviceId}:{new string('x', 43)}");
        Assert.Equal(HttpStatusCode.Unauthorized, (await http.PostAsJsonAsync(ApiRoutes.Sync, new SyncRequest(), PaCoJson.Options)).StatusCode);

        http.DefaultRequestHeaders.Authorization = new("Device", $"{paired.DeviceId}:{paired.DeviceToken}");
        await http.PostAsJsonAsync(ApiRoutes.Assign, new AssignRequest { Assignments = [new AccountAssignment { Sid = "S-1-5-21-9-9-9-1001", ChildId = foreignChild }] }, PaCoJson.Options);
        var resp = (await (await http.PostAsJsonAsync(ApiRoutes.Sync, new SyncRequest(), PaCoJson.Options)).Content.ReadFromJsonAsync<SyncResponse>(PaCoJson.Options))!;
        var policy = PolicySigning.Verify<PolicyDocument>(resp.Policy, paired.PolicyPublicKeyPem);
        Assert.Empty(policy.Accounts);
        Assert.Empty(policy.Children);
    }
}
