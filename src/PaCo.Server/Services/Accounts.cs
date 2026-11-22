using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using PaCo.Server.Data;

namespace PaCo.Server.Services;

public static class LoginTracking
{
    public static async Task TouchAsync(UserManager<ParentUser> users, string email)
    {
        var user = await users.FindByEmailAsync(email);
        if (user is null) return;
        user.LastLoginUtc = DateTimeOffset.UtcNow;
        await users.UpdateAsync(user);
    }
}

/// <summary>
/// Super-admin account management. Accounts are only ever created here (or by the CLI); there is no self sign-up.
/// Invitations are password-reset tokens (valid 7 days) for a user without password.
/// </summary>
public sealed class AccountAdmin(PaCoDb db, UserManager<ParentUser> users, TimeProvider clock, Notifier notifier)
{
    // Push texts for the family when the server operator acts inside it.
    // i18n-keys:start server
    public const string InvitedTitle = "PaCo: neues Elternteil eingeladen";
    public const string InvitedText = "Der Server-Betreiber hat {0} in deine Familie eingeladen. Falls das nicht abgesprochen war, entferne die Person unter „Familie“.";
    public const string NewLinkTitle = "PaCo: neuer Zugangslink erstellt";
    public const string NewLinkText = "Der Server-Betreiber hat für {0} einen neuen Zugangslink erstellt. Falls das nicht abgesprochen war, melde dich bei ihm.";
    // i18n-keys:end

    /// <summary>
    /// The server operator acted inside a family (invite, new access link): every parent of that family gets a push
    /// message – the operator cannot quietly get access to a family's data.
    /// </summary>
    public async Task NotifyFamilyAsync(Guid familyId, string germanTitle, string germanText, params object?[] args)
    {
        var fam = await db.Families.SingleOrDefaultAsync(f => f.Id == familyId);
        if (fam?.NtfyTopic is not { } topic) return;
        var lang = await AlertTexts.PushLanguageAsync(db, familyId, default);
        notifier.Enqueue(new PushMessage(topic, Ui.In(lang, germanTitle), Ui.In(lang, germanText, args), 4, null, "key"));
    }

    public static readonly TimeSpan InviteLifetime = TimeSpan.FromDays(7);

    public async Task<Family> CreateFamilyAsync(string name, string timeZoneId = "Europe/Berlin")
    {
        var fam = new Family
        {
            Name = name.Trim(),
            TimeZoneId = timeZoneId,
            UnlockKey = System.Security.Cryptography.RandomNumberGenerator.GetBytes(32),
            CreatedUtc = clock.GetUtcNow(),
        };
        db.Families.Add(fam);
        await db.SaveChangesAsync();
        return fam;
    }

    /// <summary>Creates a parent without password and returns the invitation link path.</summary>
    public async Task<(ParentUser User, string Link)> InviteParentAsync(string email, string displayName, Guid familyId, string baseUrl)
    {
        if (!await db.Families.AnyAsync(f => f.Id == familyId)) throw new InvalidOperationException("Familie nicht gefunden.");
        var user = new ParentUser
        {
            UserName = email.Trim(),
            Email = email.Trim(),
            DisplayName = displayName.Trim(),
            FamilyId = familyId,
            CreatedUtc = clock.GetUtcNow(),
        };
        var result = await users.CreateAsync(user);
        if (!result.Succeeded) throw new InvalidOperationException(string.Join(" ", result.Errors.Select(e => e.Description)));
        return (user, await InviteLinkAsync(user, baseUrl));
    }

    /// <summary>New invitation / password-reset link. Invalidates older links (security stamp changes).</summary>
    public async Task<string> InviteLinkAsync(ParentUser user, string baseUrl)
    {
        await users.UpdateSecurityStampAsync(user);
        var token = await users.GeneratePasswordResetTokenAsync(user);
        return $"{baseUrl.TrimEnd('/')}/Account/AcceptInvite?uid={user.Id}&token={Uri.EscapeDataString(token)}";
    }

    /// <summary>Removes password and 2FA and returns a fresh link with which the parent sets everything up again.</summary>
    public async Task<string> ResetAccessAsync(ParentUser user, string baseUrl)
    {
        if (await users.HasPasswordAsync(user)) await users.RemovePasswordAsync(user);
        await users.SetTwoFactorEnabledAsync(user, false);
        await users.ResetAuthenticatorKeyAsync(user);
        await users.SetLockoutEndDateAsync(user, null);
        await users.ResetAccessFailedCountAsync(user);
        await DbTicketStore.RemoveAllAsync(db, user.Id);
        return await InviteLinkAsync(user, baseUrl);
    }

    public async Task SetBlockedAsync(ParentUser user, bool blocked)
    {
        await users.SetLockoutEnabledAsync(user, true);
        await users.SetLockoutEndDateAsync(user, blocked ? DateTimeOffset.MaxValue : null);
        await users.UpdateSecurityStampAsync(user);
        await DbTicketStore.RemoveAllAsync(db, user.Id); // ends existing sessions immediately
    }

    public async Task DeleteFamilyAsync(Guid familyId)
    {
        var parents = await users.Users.Where(u => u.FamilyId == familyId).ToListAsync();
        foreach (var p in parents) await users.DeleteAsync(p);
        // Family-scoped rows cascade via FK.
        var fam = await db.Families.SingleAsync(f => f.Id == familyId);
        db.Families.Remove(fam);
        await db.SaveChangesAsync();
    }
}
