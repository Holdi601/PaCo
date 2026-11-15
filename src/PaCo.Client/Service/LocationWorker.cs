using System.Diagnostics;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Hosting;
using Microsoft.Win32;
using PaCo.Client.Config;
using PaCo.Shared;
using Windows.Devices.Geolocation;

namespace PaCo.Client.Service;

/// <summary>
/// Records the device location (Windows location service: Wi-Fi positioning / GPS) when the parents enabled it for
/// this device – continuously every 15 minutes or once on request ("Jetzt orten"). Runs in the service, so it also
/// works on the login screen (e.g. a stolen laptop that was only switched on).
/// </summary>
internal sealed partial class LocationWorker(Runtime rt) : BackgroundService
{
    private static readonly TimeSpan ContinuousInterval = TimeSpan.FromMinutes(15);
    private TimeSpan _lastFix = Mono.Never;
    private bool _consentDone;

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        await Task.Delay(TimeSpan.FromSeconds(20), ct);
        while (!ct.IsCancellationRequested)
        {
            try
            {
                var mode = rt.LocationMode;
                var requested = rt.TakeLocateRequest();
                var due = mode == LocationMode.Continuous && Mono.Since(_lastFix) > ContinuousInterval;
                if (mode != LocationMode.Off && (requested || due))
                {
                    var point = await LocateAsync(ct);
                    _lastFix = Mono.Now;
                    lock (rt.Gate)
                    {
                        rt.State.PendingLocations.Add(point);
                        if (rt.State.PendingLocations.Count > 500) rt.State.PendingLocations.RemoveRange(0, rt.State.PendingLocations.Count - 500);
                        rt.MarkDirty();
                    }
                    if (requested) rt.RequestSyncNow();
                }
            }
            catch (Exception ex) when (!ct.IsCancellationRequested)
            {
                Log.Warn($"Standort: {ex.GetType().Name}: {ex.Message}");
            }
            try { await Task.Delay(TimeSpan.FromSeconds(5), ct); } catch (OperationCanceledException) { break; }
        }
    }

    private async Task<LocationPoint> LocateAsync(CancellationToken ct)
    {
        EnsureLocationAllowed();
        var ssid = CurrentSsid();
        try
        {
            var locator = new Geolocator { DesiredAccuracy = PositionAccuracy.High };
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(TimeSpan.FromSeconds(45));
            var pos = await locator.GetGeopositionAsync(TimeSpan.FromMinutes(1), TimeSpan.FromSeconds(40)).AsTask(timeout.Token);
            var c = pos.Coordinate;
            Log.Info($"Standort ermittelt (±{c.Accuracy:F0} m, Quelle {c.PositionSource})");
            return new LocationPoint
            {
                TimeUtc = DateTimeOffset.UtcNow, Latitude = c.Point.Position.Latitude, Longitude = c.Point.Position.Longitude,
                AccuracyMeters = c.Accuracy, Source = c.PositionSource.ToString(), Ssid = ssid,
            };
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            // No fix (location service off, no Wi-Fi around …): still report SSID – the server adds the public IP.
            Log.Warn($"Standort nicht ermittelbar: {ex.Message}");
            return new LocationPoint { TimeUtc = DateTimeOffset.UtcNow, Ssid = ssid, Error = Trim(ex.Message, 200) };
        }
    }

    /// <summary>Allows the location service device-wide and for this service (SYSTEM). Children cannot undo this.</summary>
    private void EnsureLocationAllowed()
    {
        if (_consentDone) return;
        try
        {
            const string store = @"SOFTWARE\Microsoft\Windows\CurrentVersion\CapabilityAccessManager\ConsentStore\location";
            using (var k = Registry.LocalMachine.CreateSubKey(store)) k.SetValue("Value", "Allow");
            using (var k = Registry.Users.CreateSubKey(@"S-1-5-18\" + store)) k.SetValue("Value", "Allow");
            using (var k = Registry.Users.CreateSubKey(@"S-1-5-18\" + store + @"\NonPackaged")) k.SetValue("Value", "Allow");
            using (var k = Registry.LocalMachine.CreateSubKey(@"SYSTEM\CurrentControlSet\Services\lfsvc\Service\Configuration"))
                k.SetValue("Status", 1, RegistryValueKind.DWord);
            using var sc = new System.ServiceProcess.ServiceController("lfsvc");
            if (sc.Status != System.ServiceProcess.ServiceControllerStatus.Running)
            {
                sc.Start();
                sc.WaitForStatus(System.ServiceProcess.ServiceControllerStatus.Running, TimeSpan.FromSeconds(15));
            }
            _consentDone = true;
        }
        catch (Exception ex)
        {
            Log.Warn($"Ortungsdienst freischalten: {ex.Message}");
        }
    }

    private static string? CurrentSsid()
    {
        try
        {
            using var p = Process.Start(new ProcessStartInfo("netsh.exe", "wlan show interfaces")
            {
                RedirectStandardOutput = true, UseShellExecute = false, CreateNoWindow = true,
                StandardOutputEncoding = System.Text.Encoding.UTF8,
            })!;
            var output = p.StandardOutput.ReadToEnd();
            p.WaitForExit(5000);
            var m = SsidRegex().Match(output);
            return m.Success ? Trim(m.Groups[1].Value.Trim(), 64) : null;
        }
        catch
        {
            return null;
        }
    }

    [GeneratedRegex(@"^\s*SSID\s*:\s*(.+)$", RegexOptions.Multiline)]
    private static partial Regex SsidRegex();

    private static string Trim(string s, int max) => s.Length <= max ? s : s[..max];
}
