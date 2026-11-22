using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using PaCo.Server.Data;
using PaCo.Server.Services;
using PaCo.Shared;

namespace PaCo.Tests;

public partial class WebFlowTests
{
    private async Task<(HttpStatusCode Status, int Matched, int Alerts)> LanSeenAsync(string? token, params string[] macs)
    {
        var c = _f.Https();
        if (token is not null) c.DefaultRequestHeaders.Authorization = new("Bearer", token);
        using var r = await c.PostAsJsonAsync(ApiRoutes.LanSeen, new { macs });
        if (r.StatusCode != HttpStatusCode.OK) return (r.StatusCode, 0, 0);
        var j = await r.Content.ReadFromJsonAsync<JsonElement>();
        return (r.StatusCode, j.GetProperty("matched").GetInt32(), j.GetProperty("alerts").GetInt32());
    }

    /// <summary>Moves the device's times into the past as if <paramref name="ago"/> had passed.</summary>
    private async Task BackdateAsync(Guid device, TimeSpan ago, bool suspended = false)
    {
        using var scope = _f.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<PaCoDb>();
        var d = await db.Devices.IgnoreQueryFilters().SingleAsync(x => x.Id == device);
        var t = DateTimeOffset.UtcNow - ago;
        d.LastSeenUtc = t;
        d.LanSinceUtc = t;
        d.LanSeenUtc = DateTimeOffset.UtcNow.AddMinutes(-1);
        d.SuspendedUtc = suspended ? t : null;
        await db.SaveChangesAsync();
    }

    [Fact]
    public async Task LanReportNeedsTheWatcherToken()
    {
        Assert.Equal(HttpStatusCode.Unauthorized, (await LanSeenAsync(null, "AABBCC001122")).Status);
        Assert.Equal(HttpStatusCode.Unauthorized, (await LanSeenAsync(new string('x', 64), "AABBCC001122")).Status);
        Assert.Equal(HttpStatusCode.OK, (await LanSeenAsync(PaCoFactory.LanToken)).Status);
    }

    [Theory]
    [InlineData("aa:bb:cc:00:11:22", "AABBCC001122")]
    [InlineData("AA-BB-CC-00-11-22", "AABBCC001122")]
    [InlineData("AABBCC001122", "AABBCC001122")]
    [InlineData("01:00:5e:00:00:01", null)] // multicast
    [InlineData("ff:ff:ff:ff:ff:ff", null)]
    [InlineData("00:00:00:00:00:00", null)]
    [InlineData("aa:bb:cc", null)]
    [InlineData("aa:bb:cc:00:11:22:33:44:55", null)]
    public void MacAddressesAreNormalized(string input, string? expected) => Assert.Equal(expected, LanPresence.Normalize(input));

    [Fact]
    public async Task OnInTheNetworkButSilentWarnsTheParentsOnce()
    {
        var (b, _, _, device, api) = await FamilyWithDeviceAsync("lan@test.local");
        const string mac = "AA:BB:CC:10:20:30";
        var sync = await api.PostAsJsonAsync(ApiRoutes.Sync, new SyncRequest { NetworkMacs = [mac.Replace(":", ""), "kaputt", "01005E000001"] }, PaCoJson.Options);
        Assert.Equal(HttpStatusCode.OK, sync.StatusCode);
        Assert.Equal(["AABBCC102030"], LanPresence.Macs(await DeviceRowAsync(device)).Keys);

        // Just seen, PaCo just synced → nothing.
        Assert.Equal((HttpStatusCode.OK, 1, 0), await LanSeenAsync(PaCoFactory.LanToken, mac.ToLowerInvariant(), "11:22:33:44:55:66"));

        // On in the network for 20 minutes, PaCo silent for 20 minutes → one warning.
        await BackdateAsync(device, TimeSpan.FromMinutes(20));
        Assert.Equal((HttpStatusCode.OK, 1, 1), await LanSeenAsync(PaCoFactory.LanToken, mac));
        Assert.Equal((HttpStatusCode.OK, 1, 0), await LanSeenAsync(PaCoFactory.LanToken, mac));
        using (var scope = _f.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<PaCoDb>();
            Assert.Equal(1, await db.DeviceEvents.IgnoreQueryFilters().CountAsync(e => e.DeviceId == device && e.Template == LanPresence.LanEvent));
        }

        // PaCo reports again → warning cleared ("back" entry).
        await api.PostAsJsonAsync(ApiRoutes.Sync, new SyncRequest(), PaCoJson.Options);
        Assert.Null((await DeviceRowAsync(device)).LanAlertSentUtc);

        // Last message was "going to sleep" (network card still answers) → no warning.
        Assert.Equal(HttpStatusCode.NoContent, (await api.PostAsync(ApiRoutes.Suspend, new StringContent(""))).StatusCode);
        Assert.NotNull((await DeviceRowAsync(device)).SuspendedUtc);
        await BackdateAsync(device, TimeSpan.FromMinutes(60), suspended: true);
        Assert.Equal((HttpStatusCode.OK, 1, 0), await LanSeenAsync(PaCoFactory.LanToken, mac));

        // Parents can switch it off per device.
        await BackdateAsync(device, TimeSpan.FromMinutes(20));
        var page = await b.GetStringAsync("/Devices");
        Assert.Contains("handler=Lan", page);
        await PostFormAsync(b, "/Devices?handler=Lan", page, new() { ["deviceId"] = device.ToString(), ["enabled"] = "false" });
        Assert.False((await DeviceRowAsync(device)).LanAlert);
        Assert.Equal((HttpStatusCode.OK, 1, 0), await LanSeenAsync(PaCoFactory.LanToken, mac));
    }

    [Fact]
    public async Task ShortOrInterruptedPresenceIsNoAlarm()
    {
        var (_, _, _, device, api) = await FamilyWithDeviceAsync("lan-gap@test.local");
        await api.PostAsJsonAsync(ApiRoutes.Sync, new SyncRequest { NetworkMacs = ["AABBCC405060"] }, PaCoJson.Options);
        await BackdateAsync(device, TimeSpan.FromMinutes(20));
        using (var scope = _f.Services.CreateScope())
        {
            // Last sighting 10 minutes ago (gap > 5 min): presence starts anew now.
            var db = scope.ServiceProvider.GetRequiredService<PaCoDb>();
            var d = await db.Devices.IgnoreQueryFilters().SingleAsync(x => x.Id == device);
            d.LanSeenUtc = DateTimeOffset.UtcNow.AddMinutes(-10);
            await db.SaveChangesAsync();
        }
        Assert.Equal((HttpStatusCode.OK, 1, 0), await LanSeenAsync(PaCoFactory.LanToken, "AA:BB:CC:40:50:60"));
        Assert.True(DateTimeOffset.UtcNow - (await DeviceRowAsync(device)).LanSinceUtc!.Value < TimeSpan.FromMinutes(1));
    }
}
