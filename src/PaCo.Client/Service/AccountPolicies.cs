using System.Security.AccessControl;
using System.Security.Principal;
using Microsoft.Win32;
using PaCo.Client.Config;
using PaCo.Shared;

namespace PaCo.Client.Service;

/// <summary>
/// Windows user policies for the child's account (HKEY_USERS\{SID}), the same values the Group Policy editor writes:
/// <list type="bullet">
/// <item>Blocked programs as "Don't run specified Windows applications" (DisallowRun) – Windows refuses to start them;
///   <see cref="AppBlocker"/> stays as second line for programs started any other way.</item>
/// <item>Per blocked tool: no Registry editor ("regedit"), no command prompt ("cmd", batch files still run),
///   no PowerShell scripts ("powershell"), no Windows Script Host ("wscript"/"cscript").</item>
/// </list>
/// Deliberately NOT restricted, so the PC stays usable: Task Manager, Control Panel/Settings, Run dialog, installing
/// games into the user folder. All keys are read-only for the child; every value PaCo sets is recorded and removed
/// again when the account is no longer a child account or PaCo is uninstalled.
/// </summary>
internal static class AccountPolicies
{
    private const string Ledger = @"Software\PaCo\ManagedPolicies";
    private const string ExplorerPolicies = @"Software\Microsoft\Windows\CurrentVersion\Policies\Explorer";
    private static readonly Dictionary<string, string> Applied = new(StringComparer.OrdinalIgnoreCase);

    private sealed record Value(string Key, string Name, object Data, RegistryValueKind Kind);

    private static List<Value> Desired(ChildPolicy child)
    {
        var v = new List<Value>();
        // Each Windows policy follows its tool: parents can block or allow every tool on its own.
        var blocked = child.BlockedApps.ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (blocked.Contains("regedit"))
            v.Add(new(@"Software\Microsoft\Windows\CurrentVersion\Policies\System", "DisableRegistryTools", 1, RegistryValueKind.DWord));
        if (blocked.Contains("cmd"))
            v.Add(new(@"Software\Policies\Microsoft\Windows\System", "DisableCMD", 2, RegistryValueKind.DWord)); // 2 = prompt off, .bat still runs
        if (blocked.Contains("powershell"))
            v.Add(new(@"Software\Policies\Microsoft\Windows\PowerShell", "EnableScripts", 0, RegistryValueKind.DWord));
        if (blocked.Contains("wscript") || blocked.Contains("cscript"))
            v.Add(new(@"Software\Microsoft\Windows Script Host\Settings", "Enabled", 0, RegistryValueKind.DWord));
        var apps = child.BlockedApps
            .Select(a => a.Trim()).Where(a => a.Length > 0 && a.IndexOfAny(['\\', '/']) < 0)
            .Select(a => a.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ? a : a + ".exe")
            .Distinct(StringComparer.OrdinalIgnoreCase).Take(200).ToList();
        if (apps.Count > 0)
        {
            v.Add(new(ExplorerPolicies, "DisallowRun", 1, RegistryValueKind.DWord));
            for (var i = 0; i < apps.Count; i++)
                v.Add(new(ExplorerPolicies + @"\DisallowRun", (i + 1).ToString(), apps[i], RegistryValueKind.String));
        }
        return v;
    }

    /// <summary>Applies the policies if the user's hive is loaded (i.e. the child is signed in).</summary>
    public static void Apply(string sid, ChildPolicy child, bool force)
    {
        var desired = Desired(child);
        var hash = string.Join("|", desired.Select(d => $"{d.Key}\\{d.Name}={d.Data}"));
        if (!force && Applied.TryGetValue(sid, out var h) && h == hash) return;

        using var hive = Registry.Users.OpenSubKey(sid, writable: true);
        if (hive is null) return;

        // Remove what we set before but no longer want (e.g. an app was unblocked), then write the current set.
        var previous = ReadLedger(hive);
        var wanted = desired.Select(d => d.Key + "\\" + d.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var old in previous.Where(p => !wanted.Contains(p))) DeleteValue(hive, old);

        foreach (var d in desired)
        {
            using var key = CreateProtected(hive, d.Key, sid);
            key.SetValue(d.Name, d.Data, d.Kind);
        }
        WriteLedger(hive, sid, wanted);

        if (!Applied.TryGetValue(sid, out var before) || before != hash)
            Log.Info($"Konto-Richtlinien für {sid} gesetzt ({desired.Count} Werte)");
        Applied[sid] = hash;
    }

    public static bool IsManaged(string sid)
    {
        using var hive = Registry.Users.OpenSubKey(sid, writable: false);
        using var ledger = hive?.OpenSubKey(Ledger);
        return ledger is not null;
    }

    public static void Remove(string sid)
    {
        using var hive = Registry.Users.OpenSubKey(sid, writable: true);
        if (hive is null) return;
        RemoveFrom(hive);
        Applied.Remove(sid);
        Log.Info($"Konto-Richtlinien für {sid} entfernt");
    }

    /// <summary>Removes exactly the values PaCo set (also used by the uninstaller for offline hives).</summary>
    public static void RemoveFrom(RegistryKey hive)
    {
        foreach (var entry in ReadLedger(hive)) DeleteValue(hive, entry);
        using (var dr = hive.OpenSubKey(ExplorerPolicies + @"\DisallowRun", writable: false))
            if (dr is not null && dr.ValueCount == 0) hive.DeleteSubKey(ExplorerPolicies + @"\DisallowRun", throwOnMissingSubKey: false);
        hive.DeleteSubKeyTree(@"Software\PaCo", throwOnMissingSubKey: false);
    }

    private static List<string> ReadLedger(RegistryKey hive)
    {
        using var ledger = hive.OpenSubKey(Ledger);
        return ledger?.GetValue("Values") is string[] values ? [.. values] : [];
    }

    private static void WriteLedger(RegistryKey hive, string sid, IEnumerable<string> entries)
    {
        using var ledger = CreateProtected(hive, Ledger, sid);
        ledger.SetValue("Values", entries.ToArray(), RegistryValueKind.MultiString);
    }

    private static void DeleteValue(RegistryKey hive, string entry)
    {
        var cut = entry.LastIndexOf('\\');
        if (cut <= 0) return;
        try
        {
            using var key = hive.OpenSubKey(entry[..cut], writable: true);
            key?.DeleteValue(entry[(cut + 1)..], throwOnMissingValue: false);
        }
        catch (Exception ex)
        {
            Log.Warn($"Richtlinie {entry} nicht entfernt: {ex.Message}");
        }
    }

    /// <summary>Opens/creates a key the child can read but not change: SYSTEM/Admins full, user read only.</summary>
    private static RegistryKey CreateProtected(RegistryKey parent, string path, string sid)
    {
        var key = parent.CreateSubKey(path, RegistryKeyPermissionCheck.ReadWriteSubTree);
        var sec = new RegistrySecurity();
        sec.SetAccessRuleProtection(true, false);
        var inherit = InheritanceFlags.ContainerInherit;
        sec.AddAccessRule(new RegistryAccessRule(new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null), RegistryRights.FullControl, inherit, PropagationFlags.None, AccessControlType.Allow));
        sec.AddAccessRule(new RegistryAccessRule(new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null), RegistryRights.FullControl, inherit, PropagationFlags.None, AccessControlType.Allow));
        sec.AddAccessRule(new RegistryAccessRule(new SecurityIdentifier(sid), RegistryRights.ReadKey, inherit, PropagationFlags.None, AccessControlType.Allow));
        // Like Windows' own policy keys: sandboxed system components (Start menu, Store apps) may read them too.
        sec.AddAccessRule(new RegistryAccessRule(new SecurityIdentifier("S-1-15-2-1"), RegistryRights.ReadKey, inherit, PropagationFlags.None, AccessControlType.Allow));
        sec.AddAccessRule(new RegistryAccessRule(new SecurityIdentifier(WellKnownSidType.RestrictedCodeSid, null), RegistryRights.ReadKey, inherit, PropagationFlags.None, AccessControlType.Allow));
        key.SetAccessControl(sec);
        return key;
    }
}
