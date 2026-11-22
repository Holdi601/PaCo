using System.ServiceProcess;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Hosting.WindowsServices;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using PaCo.Client.Config;

namespace PaCo.Client.Service;

/// <summary>
/// Windows service lifetime that also receives power events: before the PC goes to sleep, PaCo tells the server
/// (best effort, 2 s). Some network cards keep answering in the network while the PC sleeps – without this notice the
/// server's LAN watcher would take a sleeping PC for "on, but PaCo silent".
/// </summary>
internal sealed class PowerAwareServiceLifetime : WindowsServiceLifetime
{
    private readonly Runtime _rt;

    public PowerAwareServiceLifetime(IHostEnvironment environment, IHostApplicationLifetime applicationLifetime, ILoggerFactory loggerFactory,
        IOptions<HostOptions> optionsAccessor, IOptions<WindowsServiceLifetimeOptions> windowsServiceOptionsAccessor, Runtime rt)
        : base(environment, applicationLifetime, loggerFactory, optionsAccessor, windowsServiceOptionsAccessor)
    {
        _rt = rt;
        CanHandlePowerEvent = true;
    }

    protected override bool OnPowerEvent(PowerBroadcastStatus powerStatus)
    {
        if (powerStatus == PowerBroadcastStatus.Suspend)
        {
            try
            {
                using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(2));
                using var api = new ServerApi(_rt.Device.ServerUrl, _rt.Device);
                Task.Run(() => api.SuspendAsync(cts.Token)).GetAwaiter().GetResult();
                Log.Info("Ruhezustand – Server informiert");
            }
            catch (Exception ex)
            {
                Log.Warn($"Ruhezustand – Server nicht erreicht: {ex.Message}");
            }
        }
        return base.OnPowerEvent(powerStatus);
    }
}
