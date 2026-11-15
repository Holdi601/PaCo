using System.DirectoryServices.AccountManagement;
using System.Security.Principal;
using PaCo.Shared;

namespace PaCo.Client.Service;

/// <summary>Local Windows accounts (including Microsoft-account-linked ones) on this PC.</summary>
internal static class LocalAccounts
{
    private static readonly SecurityIdentifier AdminsGroup = new(WellKnownSidType.BuiltinAdministratorsSid, null);

    public static List<WindowsAccount> List()
    {
        var result = new List<WindowsAccount>();
        using var ctx = new PrincipalContext(ContextType.Machine);
        using var admins = GroupPrincipal.FindByIdentity(ctx, IdentityType.Sid, AdminsGroup.Value);
        var adminSids = admins?.GetMembers(recursive: true).Select(m => m.Sid?.Value).Where(s => s is not null).ToHashSet() ?? [];

        using var search = new PrincipalSearcher(new UserPrincipal(ctx));
        foreach (var p in search.FindAll().OfType<UserPrincipal>())
        {
            using (p)
            {
                var sid = p.Sid?.Value;
                if (sid is null || p.Enabled == false) continue;
                // Built-in Administrator (500), Guest (501), DefaultAccount (503), WDAGUtilityAccount (504).
                var rid = sid[(sid.LastIndexOf('-') + 1)..];
                if (rid is "500" or "501" or "503" or "504") continue;
                result.Add(new WindowsAccount
                {
                    Sid = sid,
                    UserName = p.SamAccountName ?? p.Name ?? sid,
                    FullName = string.IsNullOrWhiteSpace(p.DisplayName) ? null : p.DisplayName,
                    IsAdmin = adminSids.Contains(sid),
                });
            }
        }
        return result.OrderBy(a => a.UserName, StringComparer.OrdinalIgnoreCase).ToList();
    }

    /// <summary>Removes an account from the local Administrators group (makes it a standard user).</summary>
    public static void MakeStandardUser(string sid)
    {
        using var ctx = new PrincipalContext(ContextType.Machine);
        using var admins = GroupPrincipal.FindByIdentity(ctx, IdentityType.Sid, AdminsGroup.Value)
            ?? throw new InvalidOperationException("Gruppe Administratoren nicht gefunden.");
        using var user = UserPrincipal.FindByIdentity(ctx, IdentityType.Sid, sid)
            ?? throw new InvalidOperationException(L.T("Benutzer nicht gefunden."));
        if (admins.Members.Remove(user)) admins.Save();

        using var users = GroupPrincipal.FindByIdentity(ctx, IdentityType.Sid, new SecurityIdentifier(WellKnownSidType.BuiltinUsersSid, null).Value);
        if (users is not null && !users.Members.Contains(user))
        {
            users.Members.Add(user);
            users.Save();
        }
    }
}
