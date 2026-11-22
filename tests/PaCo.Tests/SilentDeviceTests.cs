using System.Net;
using System.Net.Http.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using PaCo.Server.Data;
using PaCo.Server.Services;
using PaCo.Shared;

namespace PaCo.Tests;

public partial class WebFlowTests
{
    private async Task<Device> DeviceRowAsync(Guid id)
    {
        using var scope = _f.Services.CreateScope();
        return await scope.ServiceProvider.GetRequiredService<PaCoDb>().Devices.IgnoreQueryFilters().AsNoTracking().SingleAsync(d => d.Id == id);
    }

    private async Task<int> RunSilentCheckAsync(Guid device, TimeSpan silentFor)
    {
        using var scope = _f.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<PaCoDb>();
        var d = await db.Devices.IgnoreQueryFilters().SingleAsync(x => x.Id == device);
        var now = DateTimeOffset.UtcNow;
        d.LastSeenUtc = now - silentFor;
        await db.SaveChangesAsync();
        await SilentDevices.CheckAsync(db, scope.ServiceProvider.GetRequiredService<Notifier>(), null, now, default);
        return await db.DeviceEvents.IgnoreQueryFilters().CountAsync(e => e.DeviceId == device && e.Kind == DeviceEventKind.Silent);
    }

    [Fact]
    public async Task SilentDeviceIsReportedOnceAndAgainWhenBack()
    {
        var (b, _, _, device, api) = await FamilyWithDeviceAsync("silent@test.local");
        Assert.Equal(SilentDevices.DefaultDays, (await DeviceRowAsync(device)).SilentAlertDays);

        Assert.Equal(0, await RunSilentCheckAsync(device, TimeSpan.FromDays(6)));  // not yet
        Assert.Equal(1, await RunSilentCheckAsync(device, TimeSpan.FromDays(8)));  // warned …
        Assert.Equal(1, await RunSilentCheckAsync(device, TimeSpan.FromDays(9)));  // … only once
        Assert.NotNull((await DeviceRowAsync(device)).SilentAlertSentUtc);

        // The PC is back: flag cleared, entry in the device log.
        var sync = await api.PostAsJsonAsync(ApiRoutes.Sync, new SyncRequest(), PaCoJson.Options);
        Assert.Equal(HttpStatusCode.OK, sync.StatusCode);
        Assert.Null((await DeviceRowAsync(device)).SilentAlertSentUtc);
        using (var scope = _f.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<PaCoDb>();
            Assert.True(await db.DeviceEvents.IgnoreQueryFilters().AnyAsync(e => e.DeviceId == device && e.Template == SilentDevices.BackEvent));
        }

        // Parents choose the number of days (clamped), 0 switches the warning off.
        var page = await b.GetStringAsync("/Devices");
        await PostFormAsync(b, "/Devices?handler=Silent", page, new() { ["deviceId"] = device.ToString(), ["days"] = "9999" });
        Assert.Equal(SilentDevices.MaxDays, (await DeviceRowAsync(device)).SilentAlertDays);
        page = await b.GetStringAsync("/Devices");
        await PostFormAsync(b, "/Devices?handler=Silent", page, new() { ["deviceId"] = device.ToString(), ["days"] = "0" });
        Assert.Equal(0, (await DeviceRowAsync(device)).SilentAlertDays);
        Assert.Equal(1, await RunSilentCheckAsync(device, TimeSpan.FromDays(400)));
    }

    [Fact]
    public async Task UninstalledDeviceIsNotReportedAsSilent()
    {
        var (_, _, _, device, api) = await FamilyWithDeviceAsync("silent-uninstall@test.local");
        var sync = await api.PostAsJsonAsync(ApiRoutes.Sync, new SyncRequest
        {
            Events = [new DeviceEvent { TimeUtc = DateTimeOffset.UtcNow, Kind = DeviceEventKind.Uninstalled, Message = "weg" }],
        }, PaCoJson.Options);
        Assert.Equal(HttpStatusCode.OK, sync.StatusCode);
        Assert.Equal(0, await RunSilentCheckAsync(device, TimeSpan.FromDays(30)));
    }

    [Fact]
    public async Task OtherFamiliesCannotChangeTheSilentSetting()
    {
        var (_, _, _, device, _) = await FamilyWithDeviceAsync("silent-owner@test.local");
        var (other, _) = await ParentOfNewFamilyAsync("silent-other@test.local");
        var page = await other.GetStringAsync("/Devices");
        var r = await PostFormAsync(other, "/Devices?handler=Silent", page, new() { ["deviceId"] = device.ToString(), ["days"] = "0" });
        Assert.Equal(HttpStatusCode.NotFound, r.StatusCode);
        Assert.Equal(SilentDevices.DefaultDays, (await DeviceRowAsync(device)).SilentAlertDays);
    }
}
