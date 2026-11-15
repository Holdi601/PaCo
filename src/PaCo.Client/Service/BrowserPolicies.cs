using System.Security.AccessControl;
using System.Security.Principal;
using Microsoft.Win32;
using PaCo.Client.Config;
using PaCo.Shared;

namespace PaCo.Client.Service;

/// <summary>
/// Writes browser policies into the child's registry hive (HKEY_USERS\{SID}\Software\Policies\…) for Edge,
/// Chrome and Firefox. The keys get an ACL that lets the child only read them.
/// </summary>
internal static class BrowserPolicies
{
    private const string Marker = "PaCoManaged";
    private static readonly string[] Roots = [@"Software\Policies\Microsoft\Edge", @"Software\Policies\Google\Chrome", @"Software\Policies\Mozilla\Firefox"];
    private static readonly Dictionary<string, string> Applied = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Applies the policy if the user's hive is loaded. Re-applies every call when <paramref name="force"/>.</summary>
    public static void Apply(string sid, WebFilterPolicy web, bool force)
    {
        var hash = PaCoJson.Serialize(web);
        if (!force && Applied.TryGetValue(sid, out var h) && h == hash) return;

        using var hive = Registry.Users.OpenSubKey(sid, writable: true);
        if (hive is null) return; // user not logged on – applied at next logon

        var sites = web.BlockedSites.Where(s => !string.IsNullOrWhiteSpace(s)).Distinct(StringComparer.OrdinalIgnoreCase).ToList();

        WriteChromium(hive, Roots[0], sid, web, sites, edge: true);
        WriteChromium(hive, Roots[1], sid, web, sites, edge: false);
        WriteFirefox(hive, Roots[2], sid, web, sites);

        // Re-applied every minute against tampering; only log real changes.
        if (!Applied.TryGetValue(sid, out var previous) || previous != hash)
            Log.Info($"Browser-Regeln für {sid} gesetzt ({sites.Count} gesperrte Seiten)");
        Applied[sid] = hash;
    }

    public static void Remove(string sid)
    {
        using var hive = Registry.Users.OpenSubKey(sid, writable: true);
        if (hive is null) return;
        RemoveFrom(hive);
        Applied.Remove(sid);
    }

    /// <summary>Removes PaCo's policies from an opened user hive (also used by the uninstaller for offline hives).</summary>
    public static void RemoveFrom(RegistryKey hive)
    {
        foreach (var root in Roots)
        {
            using var key = hive.OpenSubKey(root, writable: false);
            if (key?.GetValue(Marker) is null) continue;
            hive.DeleteSubKeyTree(root, throwOnMissingSubKey: false);
        }
    }

    private static void WriteChromium(RegistryKey hive, string root, string sid, WebFilterPolicy web, List<string> sites, bool edge)
    {
        hive.DeleteSubKeyTree(root, throwOnMissingSubKey: false);
        using var key = CreateProtected(hive, root, sid);
        key.SetValue(Marker, 1, RegistryValueKind.DWord);

        if (web.BlockPrivateBrowsing)
        {
            key.SetValue(edge ? "InPrivateModeAvailability" : "IncognitoModeAvailability", 1, RegistryValueKind.DWord);
            key.SetValue("BrowserGuestModeEnabled", 0, RegistryValueKind.DWord);
            key.SetValue(edge ? "BrowserAddProfileEnabled" : "BrowserAddPersonEnabled", 0, RegistryValueKind.DWord);
        }
        if (web.ForceSafeSearch)
        {
            key.SetValue("ForceGoogleSafeSearch", 1, RegistryValueKind.DWord);
            if (edge) key.SetValue("ForceBingSafeSearch", 2, RegistryValueKind.DWord); // 2 = strict
        }
        if (web.YouTubeRestricted) key.SetValue("ForceYouTubeRestrict", 2, RegistryValueKind.DWord); // 2 = strict
        // Settings that would otherwise allow escaping the rules.
        if (web.BlockDevTools) key.SetValue("DeveloperToolsAvailability", 2, RegistryValueKind.DWord);

        if (sites.Count > 0)
        {
            using var list = CreateProtected(key, "URLBlocklist", sid);
            for (var i = 0; i < sites.Count; i++) list.SetValue((i + 1).ToString(), sites[i], RegistryValueKind.String);
        }
    }

    private static void WriteFirefox(RegistryKey hive, string root, string sid, WebFilterPolicy web, List<string> sites)
    {
        hive.DeleteSubKeyTree(root, throwOnMissingSubKey: false);
        using var key = CreateProtected(hive, root, sid);
        key.SetValue(Marker, 1, RegistryValueKind.DWord);
        if (web.BlockPrivateBrowsing) key.SetValue("DisablePrivateBrowsing", 1, RegistryValueKind.DWord);
        if (web.BlockDevTools) key.SetValue("DisableDeveloperTools", 1, RegistryValueKind.DWord);
        if (sites.Count > 0)
        {
            using var filter = CreateProtected(key, @"WebsiteFilter\Block", sid);
            var i = 1;
            foreach (var s in sites)
            {
                var host = s.TrimStart('*', '.');
                filter.SetValue((i++).ToString(), $"*://{host}/*", RegistryValueKind.String);
                filter.SetValue((i++).ToString(), $"*://*.{host}/*", RegistryValueKind.String);
            }
        }
    }

    /// <summary>Creates a key the child can read but not change: SYSTEM/Admins full, user read only.</summary>
    private static RegistryKey CreateProtected(RegistryKey parent, string path, string sid)
    {
        var sec = new RegistrySecurity();
        sec.SetAccessRuleProtection(true, false);
        var inherit = InheritanceFlags.ContainerInherit;
        sec.AddAccessRule(new RegistryAccessRule(new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null), RegistryRights.FullControl, inherit, PropagationFlags.None, AccessControlType.Allow));
        sec.AddAccessRule(new RegistryAccessRule(new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null), RegistryRights.FullControl, inherit, PropagationFlags.None, AccessControlType.Allow));
        sec.AddAccessRule(new RegistryAccessRule(new SecurityIdentifier(sid), RegistryRights.ReadKey, inherit, PropagationFlags.None, AccessControlType.Allow));
        var key = parent.CreateSubKey(path, RegistryKeyPermissionCheck.ReadWriteSubTree);
        key.SetAccessControl(sec);
        return key;
    }
}
