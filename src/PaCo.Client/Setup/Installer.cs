using System.Diagnostics;
using System.Runtime.InteropServices;
using System.ServiceProcess;
using Microsoft.Win32;
using Microsoft.Win32.SafeHandles;
using PaCo.Client.Config;
using PaCo.Client.Service;
using PaCo.Shared;

namespace PaCo.Client.Setup;

/// <summary>Install, update and uninstall logic (runs elevated as the parent's admin account).</summary>
internal static class Installer
{
    private const string UninstallKey = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\PaCo";

    public static bool IsInstalled => ServiceController.GetServices().Any(s => s.ServiceName == Paths.ServiceName);

    public static async Task<PairResponse> PairAsync(string serverUrl, string code, CancellationToken ct = default)
    {
        using var api = new ServerApi(serverUrl);
        return await api.PairAsync(new PairRequest
        {
            PairingCode = code,
            MachineName = Environment.MachineName,
            OsVersion = ServerApi.OsVersion,
            ClientVersion = ServerApi.Version,
            Accounts = LocalAccounts.List(),
        }, ct);
    }

    /// <summary>Full installation after successful pairing.</summary>
    public static async Task InstallAsync(string serverUrl, PairResponse pair, IReadOnlyList<AccountAssignment> assignments,
        IReadOnlyList<string> makeStandardSids, IProgress<string> progress, CancellationToken ct = default)
    {
        progress.Report(L.T("Daten-Ordner wird geschützt …"));
        SecureStore.EnsureDataDir();
        Log.Info($"Installation {ServerApi.Version} gestartet, Server {serverUrl}, Gerät {pair.DeviceId}");

        var device = new DeviceSecrets
        {
            ServerUrl = serverUrl,
            DeviceId = pair.DeviceId,
            DeviceToken = pair.DeviceToken,
            PolicyPublicKeyPem = pair.PolicyPublicKeyPem,
            UnlockKey = pair.UnlockKey,
            FamilyName = pair.FamilyName,
        };
        device.Save();

        foreach (var sid in makeStandardSids)
        {
            progress.Report(L.T("Kinderkonto wird in ein Standardkonto umgewandelt …"));
            LocalAccounts.MakeStandardUser(sid);
            Log.Info($"Konto {sid} ist jetzt Standardbenutzer");
        }

        progress.Report(L.T("Kinderkonten werden zugeordnet …"));
        using (var api = new ServerApi(serverUrl, device))
        {
            await api.AssignAsync(new AssignRequest { Assignments = assignments }, ct);
            progress.Report(L.T("Regeln werden geladen …"));
            var rt = new Runtime(device);
            await new SyncWorker(rt, installUpdates: false).SyncOnceAsync(api, ct);
            if (rt.Policies.Current is null) throw new InvalidOperationException(L.T("Es konnten keine Regeln vom Server geladen werden."));
        }

        await InstallBinariesAndServiceAsync(progress);
        progress.Report(L.T("Fertig."));
        Log.Info("Installation abgeschlossen");
    }

    /// <summary>Copies the exe to Program Files and (re)registers + starts the service. Also used for updates.</summary>
    public static async Task InstallBinariesAndServiceAsync(IProgress<string> progress)
    {
        if (IsInstalled)
        {
            progress.Report(L.T("Laufender Dienst wird angehalten …"));
            await StopServiceAsync();
        }

        progress.Report(L.T("Programm wird installiert …"));
        Directory.CreateDirectory(Paths.InstallDir);
        var self = Environment.ProcessPath!;
        var backup = Paths.InstalledExe + ".bak";
        var replaced = false;
        if (!string.Equals(Path.GetFullPath(self), Path.GetFullPath(Paths.InstalledExe), StringComparison.OrdinalIgnoreCase))
        {
            // Copy next to the target first, then swap atomically – a crash never leaves a half-written exe.
            var staged = Paths.InstalledExe + ".new";
            File.Copy(self, staged, overwrite: true);
            for (var i = 0; ; i++)
            {
                try
                {
                    if (File.Exists(Paths.InstalledExe))
                    {
                        File.Replace(staged, Paths.InstalledExe, backup, ignoreMetadataErrors: true);
                        replaced = true;
                    }
                    else
                    {
                        File.Move(staged, Paths.InstalledExe);
                    }
                    break;
                }
                catch (IOException) when (i < 20)
                {
                    // The service may still hold the file for a moment after stopping.
                    await Task.Delay(500);
                }
            }
        }

        progress.Report(L.T("Dienst wird eingerichtet …"));
        if (!IsInstalled)
        {
            Sc($"create {Paths.ServiceName} binPath= \"\\\"{Paths.InstalledExe}\\\" --service\" start= auto obj= LocalSystem DisplayName= \"{Paths.DisplayName}\"");
        }
        else
        {
            Sc($"config {Paths.ServiceName} binPath= \"\\\"{Paths.InstalledExe}\\\" --service\" start= auto obj= LocalSystem");
        }
        Sc($"description {Paths.ServiceName} \"Bildschirmzeit und Jugendschutz (PaCo). Nicht beenden.\"");
        // Restart after 5 s on every crash, counter reset after 1 day; also on non-crash failures.
        Sc($"failure {Paths.ServiceName} reset= 86400 actions= restart/5000/restart/5000/restart/5000");
        Sc($"failureflag {Paths.ServiceName} 1");

        // Also run in safe mode – otherwise "Abgesicherter Modus" would be a way around PaCo.
        foreach (var mode in new[] { "Minimal", "Network" })
        {
            using var k = Registry.LocalMachine.CreateSubKey($@"SYSTEM\CurrentControlSet\Control\SafeBoot\{mode}\{Paths.ServiceName}");
            k.SetValue("", "Service");
        }

        using (var u = Registry.LocalMachine.CreateSubKey(UninstallKey))
        {
            u.SetValue("DisplayName", Paths.DisplayName);
            u.SetValue("DisplayVersion", ServerApi.Version);
            u.SetValue("Publisher", "PaCo");
            u.SetValue("DisplayIcon", Paths.InstalledExe);
            u.SetValue("InstallLocation", Paths.InstallDir);
            u.SetValue("UninstallString", $"\"{Paths.InstalledExe}\" --uninstall");
            u.SetValue("NoModify", 1, RegistryValueKind.DWord);
            u.SetValue("NoRepair", 1, RegistryValueKind.DWord);
        }

        progress.Report(L.T("Dienst wird gestartet …"));
        try
        {
            await StartAndVerifyAsync();
            Log.Info($"Dienst läuft (Version {ServerApi.Version})");
        }
        catch (Exception ex) when (replaced && File.Exists(backup))
        {
            // New version does not start: roll back to the previous exe so the child's PC stays protected.
            Log.Error("Neue Version startet nicht – stelle vorherige Version wieder her", ex);
            await StopServiceAsync();
            File.Copy(backup, Paths.InstalledExe, overwrite: true);
            await StartAndVerifyAsync();
            Log.Warn("Vorherige Version wiederhergestellt");
            throw;
        }
    }

    /// <summary>Starts the service and checks it is still running a few seconds later (no crash loop).</summary>
    private static async Task StartAndVerifyAsync()
    {
        using var sc = new ServiceController(Paths.ServiceName);
        if (sc.Status != ServiceControllerStatus.Running) sc.Start();
        sc.WaitForStatus(ServiceControllerStatus.Running, TimeSpan.FromSeconds(30));
        await Task.Delay(TimeSpan.FromSeconds(5));
        sc.Refresh();
        if (sc.Status != ServiceControllerStatus.Running)
            throw new InvalidOperationException(L.F("Dienst läuft nach dem Start nicht mehr (Status {0}).", sc.Status));
    }

    public static async Task UninstallAsync(IProgress<string> progress)
    {
        Log.Info("Deinstallation gestartet");
        var device = DeviceSecrets.Load();
        var policy = new PolicyStore(device ?? new DeviceSecrets());
        if (device is not null) policy.LoadFromDisk();

        if (device is not null)
        {
            progress.Report(L.T("Server wird informiert …"));
            try
            {
                using var api = new ServerApi(device.ServerUrl, device);
                using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
                await api.SyncAsync(new SyncRequest
                {
                    ClientVersion = ServerApi.Version,
                    ClientUtcNow = DateTimeOffset.UtcNow,
                    Events = [new DeviceEvent { TimeUtc = DateTimeOffset.UtcNow, Kind = DeviceEventKind.Uninstalled, Message = $"PaCo wurde auf {Environment.MachineName} deinstalliert." }],
                }, cts.Token);
            }
            catch (Exception ex)
            {
                Log.Warn($"Server nicht informiert: {ex.Message}");
            }
        }

        progress.Report(L.T("Dienst wird entfernt …"));
        if (IsInstalled)
        {
            await StopServiceAsync();
            Sc($"delete {Paths.ServiceName}", throwOnError: false);
        }
        foreach (var mode in new[] { "Minimal", "Network" })
            Registry.LocalMachine.DeleteSubKeyTree($@"SYSTEM\CurrentControlSet\Control\SafeBoot\{mode}\{Paths.ServiceName}", throwOnMissingSubKey: false);

        progress.Report(L.T("Browser-Regeln werden entfernt …"));
        foreach (var sid in policy.Current?.Accounts.Select(a => a.Sid) ?? [])
            RemoveBrowserPolicies(sid);

        Registry.LocalMachine.DeleteSubKeyTree(UninstallKey, throwOnMissingSubKey: false);
        try
        {
            Directory.Delete(Paths.DataDir, recursive: true);
        }
        catch (Exception ex)
        {
            Log.Warn($"Datenordner nicht gelöscht: {ex.Message}");
        }

        // The running exe cannot delete itself – a detached cmd removes the folder after we exit.
        Process.Start(new ProcessStartInfo("cmd.exe", $"/c ping 127.0.0.1 -n 4 > nul & rmdir /s /q \"{Paths.InstallDir}\"")
        {
            CreateNoWindow = true,
            UseShellExecute = false,
            WorkingDirectory = Path.GetTempPath(),
        });
        progress.Report(L.T("PaCo wurde entfernt."));
    }

    private static void RemoveBrowserPolicies(string sid)
    {
        try
        {
            using var loaded = Registry.Users.OpenSubKey(sid, writable: true);
            if (loaded is not null)
            {
                BrowserPolicies.RemoveFrom(loaded);
                return;
            }
            // User is logged off: open the profile hive file directly.
            using var profile = Registry.LocalMachine.OpenSubKey($@"SOFTWARE\Microsoft\Windows NT\CurrentVersion\ProfileList\{sid}");
            var dir = profile?.GetValue("ProfileImagePath") as string;
            if (dir is null) return;
            var hivePath = Path.Combine(Environment.ExpandEnvironmentVariables(dir), "NTUSER.DAT");
            if (RegLoadAppKey(hivePath, out var handle, 0xF003F, 0, 0) != 0) return;
            using var hive = RegistryKey.FromHandle(handle);
            BrowserPolicies.RemoveFrom(hive);
        }
        catch (Exception ex)
        {
            Log.Warn($"Browser-Regeln für {sid} nicht entfernt: {ex.Message}");
        }
    }

    private static async Task StopServiceAsync()
    {
        using var sc = new ServiceController(Paths.ServiceName);
        if (sc.Status is ServiceControllerStatus.Stopped) return;
        try
        {
            sc.Stop();
            sc.WaitForStatus(ServiceControllerStatus.Stopped, TimeSpan.FromSeconds(30));
        }
        catch (Exception ex)
        {
            Log.Warn($"Dienst stoppen: {ex.Message}");
        }
        // Make sure agents are gone too (they exit when the pipe closes).
        foreach (var p in Process.GetProcessesByName("PaCo").Where(p => p.Id != Environment.ProcessId))
        {
            try { p.Kill(); } catch { /* ignore */ }
        }
        await Task.Delay(500);
    }

    private static void Sc(string args, bool throwOnError = true)
    {
        using var p = Process.Start(new ProcessStartInfo("sc.exe", args)
        {
            CreateNoWindow = true,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        })!;
        var output = p.StandardOutput.ReadToEnd() + p.StandardError.ReadToEnd();
        p.WaitForExit();
        if (p.ExitCode != 0)
        {
            Log.Warn($"sc {args} → {p.ExitCode}: {output.Trim()}");
            if (throwOnError) throw new InvalidOperationException($"Dienst-Einrichtung fehlgeschlagen (sc {args.Split(' ')[0]}: {p.ExitCode}).");
        }
    }

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode)]
    private static extern int RegLoadAppKey(string file, out SafeRegistryHandle key, int desired, int options, int reserved);
}
