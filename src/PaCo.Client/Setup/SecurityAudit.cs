using System.DirectoryServices.AccountManagement;
using System.Management;
using System.Runtime.InteropServices;
using System.Security.Principal;
using Microsoft.Win32;
using PaCo.Client.Config;
using PaCo.Client.Service;
using PaCo.Shared;

namespace PaCo.Client.Setup;

/// <summary>
/// Checks that the PC and the children's accounts are set up so a child cannot simply bypass PaCo or catch
/// malware easily: standard accounts, admin passwords, UAC, antivirus, firewall, SmartScreen, guest, auto-logon.
/// </summary>
internal static class SecurityAudit
{
    private const string UacKey = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Policies\System";

    public static List<SecurityFinding> Run(IReadOnlyCollection<string> childSids)
    {
        var f = new List<SecurityFinding>();
        Safe(f, "accounts", () => CheckAccounts(f, childSids));
        Safe(f, "uac", () => CheckUac(f));
        Safe(f, "antivirus", () => CheckAntivirus(f));
        Safe(f, "firewall", () => CheckFirewall(f));
        Safe(f, "smartscreen", () => CheckSmartScreen(f));
        Safe(f, "autologon", () => CheckAutoLogon(f));
        return f;
    }

    private static void Safe(List<SecurityFinding> f, string area, Action check)
    {
        try
        {
            check();
        }
        catch (Exception ex)
        {
            Log.Warn($"Sicherheits-Check {area}: {ex.Message}");
            f.Add(new SecurityFinding(area + "-error", SecuritySeverity.Info, $"Prüfung „{area}“ war nicht möglich: {ex.Message}"));
        }
    }

    private static void CheckAccounts(List<SecurityFinding> f, IReadOnlyCollection<string> childSids)
    {
        var accounts = LocalAccounts.List();
        foreach (var a in accounts.Where(a => childSids.Contains(a.Sid)))
        {
            f.Add(a.IsAdmin
                ? new SecurityFinding("child-admin:" + a.Sid, SecuritySeverity.Critical, $"Das Kinderkonto „{a.UserName}“ hat Administratorrechte – damit kann PaCo umgangen werden.", true)
                : new SecurityFinding("child-standard:" + a.Sid, SecuritySeverity.Ok, $"Kinderkonto „{a.UserName}“ ist ein Standardkonto."));
        }

        // Enabled admin accounts without password: a child could just click "Yes" at the admin prompt.
        using var ctx = new PrincipalContext(ContextType.Machine);
        using var admins = GroupPrincipal.FindByIdentity(ctx, IdentityType.Sid, new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null).Value);
        foreach (var m in admins?.GetMembers(true).OfType<UserPrincipal>() ?? [])
        {
            using (m)
            {
                if (m.Enabled != true || m.SamAccountName is null) continue;
                if (HasBlankPassword(m.SamAccountName))
                    f.Add(new SecurityFinding("admin-blank:" + m.SamAccountName, SecuritySeverity.Critical,
                        $"Das Administrator-Konto „{m.SamAccountName}“ hat KEIN Passwort. Bitte ein Passwort setzen, das die Kinder nicht kennen."));
            }
        }

        using var guest = UserPrincipal.FindByIdentity(ctx, IdentityType.Sid, new SecurityIdentifier(WellKnownSidType.AccountGuestSid, LocalDomainSid(ctx)).Value);
        if (guest?.Enabled == true)
            f.Add(new SecurityFinding("guest-enabled", SecuritySeverity.Warning, "Das Gastkonto ist aktiv – darüber könnte das Kind ohne Regeln am PC sein.", true));
    }

    private static SecurityIdentifier? LocalDomainSid(PrincipalContext ctx)
    {
        using var any = new UserPrincipal(ctx);
        using var search = new PrincipalSearcher(any);
        var first = search.FindAll().OfType<UserPrincipal>().FirstOrDefault(u => u.Sid is not null);
        return first?.Sid?.AccountDomainSid;
    }

    private static void CheckUac(List<SecurityFinding> f)
    {
        using var k = Registry.LocalMachine.OpenSubKey(UacKey);
        var lua = k?.GetValue("EnableLUA") as int? ?? 1;
        if (lua == 0)
            f.Add(new SecurityFinding("uac-off", SecuritySeverity.Critical, "Die Benutzerkontensteuerung (UAC) ist ausgeschaltet.", true));
        var user = k?.GetValue("ConsentPromptBehaviorUser") as int? ?? 3;
        f.Add(user == 0
            ? new SecurityFinding("uac-user-deny", SecuritySeverity.Ok, "Standardkonten bekommen keine Admin-Passwortabfrage (sicherste Einstellung).")
            : new SecurityFinding("uac-user-prompt", SecuritySeverity.Info, "Kinder können bei Admin-Aktionen ein Admin-Passwort eingeben. Empfehlung: Abfrage für Standardkonten ganz abschalten.", true));
    }

    private static void CheckAntivirus(List<SecurityFinding> f)
    {
        bool defenderOn = false;
        int sigAge = -1;
        try
        {
            using var s = new ManagementObjectSearcher(@"root\Microsoft\Windows\Defender", "SELECT RealTimeProtectionEnabled, AntivirusEnabled, AntivirusSignatureAge FROM MSFT_MpComputerStatus");
            foreach (ManagementObject o in s.Get())
            {
                defenderOn = (bool)(o["RealTimeProtectionEnabled"] ?? false) && (bool)(o["AntivirusEnabled"] ?? false);
                sigAge = Convert.ToInt32(o["AntivirusSignatureAge"] ?? -1);
            }
        }
        catch (ManagementException) { /* Defender not available (third-party AV) */ }

        var others = new List<string>();
        try
        {
            using var s = new ManagementObjectSearcher(@"root\SecurityCenter2", "SELECT displayName, productState FROM AntiVirusProduct");
            foreach (ManagementObject o in s.Get())
            {
                var state = Convert.ToInt32(o["productState"] ?? 0);
                var enabled = ((state >> 12) & 0xF) == 1;
                var name = o["displayName"]?.ToString() ?? "?";
                if (enabled && !name.Contains("Defender", StringComparison.OrdinalIgnoreCase)) others.Add(name);
            }
        }
        catch (ManagementException) { }

        if (defenderOn)
            f.Add(sigAge is > 7
                ? new SecurityFinding("av-outdated", SecuritySeverity.Warning, $"Microsoft Defender ist aktiv, aber die Virensignaturen sind {sigAge} Tage alt – bitte Windows-Updates installieren.")
                : new SecurityFinding("av-ok", SecuritySeverity.Ok, "Virenschutz (Microsoft Defender) ist aktiv und aktuell."));
        else if (others.Count > 0)
            f.Add(new SecurityFinding("av-other", SecuritySeverity.Ok, $"Virenschutz aktiv: {string.Join(", ", others)}."));
        else
            f.Add(new SecurityFinding("av-off", SecuritySeverity.Critical, "Kein aktiver Virenschutz gefunden! Bitte Microsoft Defender (Windows-Sicherheit) einschalten."));
    }

    private static void CheckFirewall(List<SecurityFinding> f)
    {
        var off = new List<string>();
        using var s = new ManagementObjectSearcher(@"root\StandardCimv2", "SELECT Name, Enabled FROM MSFT_NetFirewallProfile");
        foreach (ManagementObject o in s.Get())
            if (Convert.ToInt32(o["Enabled"] ?? 1) == 0) off.Add(o["Name"]?.ToString() ?? "?");
        f.Add(off.Count == 0
            ? new SecurityFinding("fw-ok", SecuritySeverity.Ok, "Windows-Firewall ist eingeschaltet.")
            : new SecurityFinding("fw-off", SecuritySeverity.Warning, $"Windows-Firewall ist aus für: {string.Join(", ", off)}.", true));
    }

    private static void CheckSmartScreen(List<SecurityFinding> f)
    {
        using var pol = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Policies\Microsoft\Windows\System");
        using var exp = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\Windows\CurrentVersion\Explorer");
        var off = pol?.GetValue("EnableSmartScreen") as int? == 0 ||
                  string.Equals(exp?.GetValue("SmartScreenEnabled") as string, "Off", StringComparison.OrdinalIgnoreCase);
        f.Add(off
            ? new SecurityFinding("smartscreen-off", SecuritySeverity.Warning, "SmartScreen (Warnung vor gefährlichen Downloads) ist ausgeschaltet.", true)
            : new SecurityFinding("smartscreen-ok", SecuritySeverity.Ok, "SmartScreen warnt vor gefährlichen Downloads."));
    }

    private static void CheckAutoLogon(List<SecurityFinding> f)
    {
        using var k = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\Windows NT\CurrentVersion\Winlogon");
        if (k?.GetValue("AutoAdminLogon") as string != "1") return;
        var user = k.GetValue("DefaultUserName") as string ?? "";
        var isAdmin = LocalAccounts.List().Any(a => a.IsAdmin && string.Equals(a.UserName, user, StringComparison.OrdinalIgnoreCase));
        if (isAdmin)
            f.Add(new SecurityFinding("autologon-admin", SecuritySeverity.Critical, $"Der PC meldet sich beim Start automatisch als Administrator „{user}“ an – das Kind hätte dann volle Rechte."));
    }

    /// <summary>Applies the automatic fixes for the given finding codes. Returns texts of what was done.</summary>
    public static List<string> Fix(IEnumerable<SecurityFinding> findings)
    {
        var done = new List<string>();
        foreach (var x in findings.Where(x => x.Fixable && x.Severity != SecuritySeverity.Ok))
        {
            try
            {
                if (x.Code.StartsWith("child-admin:"))
                {
                    LocalAccounts.MakeStandardUser(x.Code["child-admin:".Length..]);
                    done.Add("Kinderkonto in Standardkonto umgewandelt");
                }
                else if (x.Code == "guest-enabled")
                {
                    using var ctx = new PrincipalContext(ContextType.Machine);
                    using var g = UserPrincipal.FindByIdentity(ctx, "Guest") ?? UserPrincipal.FindByIdentity(ctx, "Gast");
                    if (g is not null) { g.Enabled = false; g.Save(); done.Add("Gastkonto deaktiviert"); }
                }
                else if (x.Code == "uac-off")
                {
                    using var k = Registry.LocalMachine.CreateSubKey(UacKey);
                    k.SetValue("EnableLUA", 1, RegistryValueKind.DWord);
                    done.Add("Benutzerkontensteuerung eingeschaltet (wirkt nach Neustart)");
                }
                else if (x.Code == "uac-user-prompt")
                {
                    using var k = Registry.LocalMachine.CreateSubKey(UacKey);
                    k.SetValue("ConsentPromptBehaviorUser", 0, RegistryValueKind.DWord);
                    done.Add("Admin-Abfrage für Standardkonten abgeschaltet");
                }
                else if (x.Code == "fw-off")
                {
                    RunHidden("netsh.exe", "advfirewall set allprofiles state on");
                    done.Add("Windows-Firewall eingeschaltet");
                }
                else if (x.Code == "smartscreen-off")
                {
                    using var k = Registry.LocalMachine.CreateSubKey(@"SOFTWARE\Microsoft\Windows\CurrentVersion\Explorer");
                    k.SetValue("SmartScreenEnabled", "Warn", RegistryValueKind.String);
                    done.Add("SmartScreen eingeschaltet");
                }
            }
            catch (Exception ex)
            {
                Log.Warn($"Korrektur {x.Code} fehlgeschlagen: {ex.Message}");
            }
        }
        foreach (var d in done) Log.Info("Sicherheits-Korrektur: " + d);
        return done;
    }

    private static void RunHidden(string exe, string args)
    {
        using var p = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(exe, args) { CreateNoWindow = true, UseShellExecute = false })!;
        p.WaitForExit(15000);
    }

    // ---------- blank password probe ----------
    private const int Logon32LogonNetwork = 3, Logon32ProviderDefault = 0;
    private const int ErrorAccountRestriction = 1327; // blank password, restricted to console logon

    [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool LogonUser(string user, string domain, string password, int type, int provider, out IntPtr token);

    [DllImport("kernel32.dll")]
    private static extern bool CloseHandle(IntPtr h);

    /// <summary>True if the local account accepts an empty password (error 1327 also means "blank, but restricted").</summary>
    private static bool HasBlankPassword(string user)
    {
        if (LogonUser(user, ".", "", Logon32LogonNetwork, Logon32ProviderDefault, out var token))
        {
            CloseHandle(token);
            return true;
        }
        return Marshal.GetLastWin32Error() == ErrorAccountRestriction;
    }
}
