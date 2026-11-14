using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using PaCo.Client.Agent;
using PaCo.Client.Config;
using PaCo.Client.Service;
using PaCo.Client.Setup;
using PaCo.Shared;

namespace PaCo.Client;

/// <summary>
/// One exe, four roles:
///   PaCo.exe                      → installer wizard (double-click, admin)
///   PaCo.exe --service            → Windows service (LocalSystem)
///   PaCo.exe --agent {pipe}       → session agent (SYSTEM in a child's session, started by the service)
///   PaCo.exe --uninstall          → uninstall (from "Apps &amp; Features")
///   PaCo.exe --install [--server URL --code CODE] --child user=Name [--child …] [--make-standard user]
///                                 → unattended install (automated tests, scripted setups); server/code
///                                   default to the configuration embedded in a downloaded PaCo-Setup.exe
/// </summary>
internal static class Program
{
    [STAThread]
    private static int Main(string[] args)
    {
        try
        {
            return args.FirstOrDefault() switch
            {
                "--service" => RunService(),
                "--agent" when args.Length >= 2 => AgentApp.Run(args[1]),
                "--uninstall" => RunUninstall(),
                "--install" => RunSilentInstall(args),
                _ => RunSetup(),
            };
        }
        catch (Exception ex)
        {
            Log.Error($"Fataler Fehler ({string.Join(' ', args.Take(1))})", ex);
            return 1;
        }
    }

    private static int RunService()
    {
        SecureStore.EnsureDataDir();
        var device = DeviceSecrets.Load();
        if (device is null)
        {
            Log.Error("Dienst gestartet, aber Gerät ist nicht gekoppelt (device.bin fehlt).");
            return 2;
        }
        Log.Info($"Dienst startet, Version {ServerApi.Version}, Gerät {device.DeviceId}, Server {device.ServerUrl}");

        var builder = Host.CreateApplicationBuilder();
        builder.Services.AddWindowsService(o => o.ServiceName = Paths.ServiceName);
        var rt = new Runtime(device);
        lock (rt.Gate) rt.State.AddEvent(DeviceEventKind.ServiceStarted, $"PaCo-Dienst gestartet (Version {ServerApi.Version})");
        builder.Services.AddSingleton(rt);
        builder.Services.AddHostedService<Enforcer>();
        builder.Services.AddHostedService<SyncWorker>();
        builder.Services.Configure<HostOptions>(o =>
        {
            o.ShutdownTimeout = TimeSpan.FromSeconds(10);
            // A crashing background task must stop the process so Windows restarts the service.
            o.BackgroundServiceExceptionBehavior = BackgroundServiceExceptionBehavior.StopHost;
        });
        using var host = builder.Build();
        host.Run();
        lock (rt.Gate) rt.SaveIfNeeded(force: true);
        Log.Info("Dienst beendet");
        return 0;
    }

    private static int RunSetup()
    {
        ApplicationConfiguration.Initialize();
        SetupPayload? payload = null;
        try
        {
            using var self = File.OpenRead(Environment.ProcessPath!);
            payload = SetupPayload.TryRead(self);
        }
        catch (Exception ex)
        {
            Log.Warn($"Setup-Konfiguration nicht lesbar: {ex.Message}");
        }
        Application.Run(new SetupForm(payload));
        return 0;
    }

    private static int RunUninstall()
    {
        ApplicationConfiguration.Initialize();
        if (MessageBox.Show("PaCo Kinderschutz von diesem PC entfernen?\n\nDanach gelten hier keine Regeln mehr.", "PaCo",
                MessageBoxButtons.YesNo, MessageBoxIcon.Warning) != DialogResult.Yes)
            return 1;
        Installer.UninstallAsync(new Progress<string>(s => Log.Info(s))).GetAwaiter().GetResult();
        MessageBox.Show("PaCo wurde entfernt.", "PaCo", MessageBoxButtons.OK, MessageBoxIcon.Information);
        return 0;
    }

    /// <summary>Unattended install; result in C:\ProgramData\PaCo\paco.log and exit code (0 = ok).</summary>
    private static int RunSilentInstall(string[] args)
    {
        string? Arg(string name) => args.SkipWhile(a => a != name).Skip(1).FirstOrDefault();
        IEnumerable<string> All(string name) => args.Select((a, i) => (a, i)).Where(x => x.a == name && x.i + 1 < args.Length).Select(x => args[x.i + 1]);

        // Server and code from the command line, otherwise from the configuration embedded at download time.
        SetupPayload? payload = null;
        try
        {
            using var self = File.OpenRead(Environment.ProcessPath!);
            payload = SetupPayload.TryRead(self);
        }
        catch (IOException) { }
        var server = Arg("--server") ?? payload?.ServerUrl ?? throw new ArgumentException("--server fehlt (und keine eingebettete Konfiguration)");
        var code = Arg("--code") ?? payload?.PairingCode ?? throw new ArgumentException("--code fehlt (und keine eingebettete Konfiguration)");
        SecureStore.EnsureDataDir();
        Log.Info($"Unbeaufsichtigte Installation: {server}");

        var accounts = Service.LocalAccounts.List();
        var children = All("--child").Select(c => c.Split('=', 2)).ToDictionary(p => p[0], p => p.Length > 1 ? p[1] : p[0], StringComparer.OrdinalIgnoreCase);
        var demote = All("--make-standard").ToHashSet(StringComparer.OrdinalIgnoreCase);

        var pair = Installer.PairAsync(server, code).GetAwaiter().GetResult();
        var assignments = accounts.Select(a =>
        {
            if (!children.TryGetValue(a.UserName, out var name)) return new AccountAssignment { Sid = a.Sid };
            var existing = pair.Children.FirstOrDefault(c => string.Equals(c.Name, name, StringComparison.OrdinalIgnoreCase));
            return existing is not null ? new AccountAssignment { Sid = a.Sid, ChildId = existing.Id } : new AccountAssignment { Sid = a.Sid, NewChildName = name };
        }).ToList();
        var missing = children.Keys.Where(k => !accounts.Any(a => string.Equals(a.UserName, k, StringComparison.OrdinalIgnoreCase))).ToList();
        if (missing.Count > 0) throw new ArgumentException($"Windows-Konto nicht gefunden: {string.Join(", ", missing)}");

        var demoteSids = accounts.Where(a => demote.Contains(a.UserName)).Select(a => a.Sid).ToList();
        Installer.InstallAsync(server, pair, assignments, demoteSids, new Progress<string>(s => Log.Info(s))).GetAwaiter().GetResult();
        return 0;
    }
}
