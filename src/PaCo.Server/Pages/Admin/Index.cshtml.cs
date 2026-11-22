using System.ComponentModel.DataAnnotations;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using PaCo.Server.Data;
using PaCo.Server.Services;

namespace PaCo.Server.Pages.Admin;

public sealed record FamilyRow(Family Family, int Parents, int Children, int Devices);

/// <summary>
/// Super admin: families, parent accounts and invitations. Only counts are shown for family data – the admin
/// never sees children's names, rules or statistics.
/// </summary>
public sealed class IndexModel(PaCoDb db, UserManager<ParentUser> users, AccountAdmin admin, IOptions<ServerOptions> options,
    TimeProvider clock, ClientPackage client, GitHubUpdates github, ILogger<IndexModel> log) : PageModel
{
    public ClientPackage.Build? ClientBuild => client.Current;

    public int DevicesOutdated { get; private set; }

    public int DevicesTotal { get; private set; }

    public async Task<IActionResult> OnPostCheckUpdatesAsync(CancellationToken ct)
    {
        var r = await github.CheckAndDownloadAsync(ct);
        TempData[r.Ok ? "Message" : "Error"] = r.Message;
        return RedirectToPage();
    }

    /// <summary>Requests the newest client on every outdated device of every family.</summary>
    public async Task<IActionResult> OnPostUpdateAllAsync()
    {
        var devices = await db.Devices.IgnoreQueryFilters().Where(d => !d.Revoked).ToListAsync();
        var n = 0;
        foreach (var d in devices.Where(d => PaCo.Shared.UpdateManifest.IsNewer(client.Version, d.ClientVersion)))
        {
            d.UpdateRequested = true;
            n++;
        }
        await db.SaveChangesAsync();
        log.LogInformation("Update auf {Version} für {Count} Geräte angefordert", client.Version, n);
        TempData["Message"] = n switch
        {
            0 => Ui.T("Alle Geräte sind aktuell."),
            1 => Ui.F("1 Gerät installiert Version {0} beim nächsten Kontakt.", client.Version),
            _ => Ui.F("{0} Geräte installieren Version {1} beim nächsten Kontakt.", n, client.Version),
        };
        return RedirectToPage();
    }

    public IReadOnlyList<FamilyRow> Families { get; private set; } = [];

    public IReadOnlyList<ParentUser> Parents { get; private set; } = [];

    public IReadOnlyList<ParentUser> Admins { get; private set; } = [];

    public DateTimeOffset Now { get; private set; }

    [TempData]
    public string? InviteLink { get; set; }

    [TempData]
    public string? InviteFor { get; set; }

    [BindProperty, Required(ErrorMessage = "Bitte Namen eingeben."), StringLength(100)]
    public string InviteName { get; set; } = "";

    [BindProperty, Required(ErrorMessage = "Bitte E-Mail eingeben."), EmailAddress(ErrorMessage = "Ungültige E-Mail-Adresse.")]
    public string InviteEmail { get; set; } = "";

    /// <summary>Existing family, or null to create a new one named <see cref="NewFamilyName"/>.</summary>
    [BindProperty]
    public Guid? InviteFamilyId { get; set; }

    [BindProperty, StringLength(100)]
    public string? NewFamilyName { get; set; }

    public async Task OnGetAsync()
    {
        Now = clock.GetUtcNow();
        var families = await db.Families.OrderBy(f => f.Name).ToListAsync();
        // Admin has no family context: count explicitly across families.
        var children = await db.Children.IgnoreQueryFilters().GroupBy(c => c.FamilyId).Select(g => new { g.Key, N = g.Count() }).ToDictionaryAsync(x => x.Key, x => x.N);
        var devices = await db.Devices.IgnoreQueryFilters().Where(d => !d.Revoked).GroupBy(d => d.FamilyId).Select(g => new { g.Key, N = g.Count() }).ToDictionaryAsync(x => x.Key, x => x.N);
        var versions = await db.Devices.IgnoreQueryFilters().Where(d => !d.Revoked).Select(d => d.ClientVersion).ToListAsync();
        DevicesTotal = versions.Count;
        DevicesOutdated = versions.Count(v => PaCo.Shared.UpdateManifest.IsNewer(client.Version, v));
        var all = await users.Users.Include(u => u.Family).OrderBy(u => u.Email).ToListAsync();
        var adminIds = (await users.GetUsersInRoleAsync(PaCoRoles.SuperAdmin)).Select(u => u.Id).ToHashSet();

        Admins = all.Where(u => adminIds.Contains(u.Id)).ToList();
        Parents = all.Where(u => !adminIds.Contains(u.Id)).ToList();
        Families = families.Select(f => new FamilyRow(f, Parents.Count(p => p.FamilyId == f.Id), children.GetValueOrDefault(f.Id), devices.GetValueOrDefault(f.Id))).ToList();
    }

    public async Task<IActionResult> OnPostInviteAsync()
    {
        if (InviteFamilyId is null && string.IsNullOrWhiteSpace(NewFamilyName))
            ModelState.AddModelError(nameof(NewFamilyName), Ui.T("Bitte eine Familie wählen oder einen Namen für die neue Familie eingeben."));
        if (await users.FindByEmailAsync(InviteEmail.Trim()) is not null)
            ModelState.AddModelError(nameof(InviteEmail), Ui.T("Diese E-Mail-Adresse hat bereits ein Konto."));
        if (!ModelState.IsValid)
        {
            await OnGetAsync();
            return Page();
        }

        var familyId = InviteFamilyId ?? (await admin.CreateFamilyAsync(NewFamilyName!)).Id;
        var (user, link) = await admin.InviteParentAsync(InviteEmail, InviteName, familyId, BaseUrl());
        if (InviteFamilyId is not null)
            await admin.NotifyFamilyAsync(familyId, AccountAdmin.InvitedTitle, AccountAdmin.InvitedText, user.Email);
        log.LogInformation("Einladung erstellt für Konto {User}", user.Id);
        InviteLink = link;
        InviteFor = $"{user.DisplayName} ({user.Email})";
        return RedirectToPage();
    }

    public async Task<IActionResult> OnPostNewLinkAsync(Guid userId)
    {
        var user = await ParentAsync(userId);
        if (user is null) return NotFound();
        InviteLink = user.InvitePending ? await admin.InviteLinkAsync(user, BaseUrl()) : await admin.ResetAccessAsync(user, BaseUrl());
        if (user.FamilyId is { } fid)
            await admin.NotifyFamilyAsync(fid, AccountAdmin.NewLinkTitle, AccountAdmin.NewLinkText, user.Email);
        InviteFor = $"{user.DisplayName} ({user.Email})";
        log.LogWarning("Neuer Zugangslink für {Email} erstellt", user.Email);
        return RedirectToPage();
    }

    public async Task<IActionResult> OnPostResetPasswordAsync(Guid userId)
    {
        var user = await ParentAsync(userId);
        if (user is null) return NotFound();
        InviteLink = await admin.ResetPasswordOnlyAsync(user, BaseUrl());
        InviteFor = $"{user.DisplayName} ({user.Email})";
        if (user.FamilyId is { } fid) await admin.NotifyFamilyAsync(fid, AccountAdmin.NewLinkTitle, AccountAdmin.NewLinkText, user.Email);
        log.LogWarning("Passwort-Link für Konto {User} erstellt", user.Id);
        return RedirectToPage();
    }

    public async Task<IActionResult> OnPostResetTwoFactorAsync(Guid userId)
    {
        var user = await ParentAsync(userId);
        if (user is null) return NotFound();
        await admin.ResetTwoFactorOnlyAsync(user);
        if (user.FamilyId is { } fid) await admin.NotifyFamilyAsync(fid, AccountAdmin.TwoFactorResetTitle, AccountAdmin.TwoFactorResetText, user.Email);
        TempData["Message"] = Ui.F("Authenticator von {0} zurückgesetzt. Bei der nächsten Anmeldung (mit dem bisherigen Passwort) wird er neu eingerichtet.", user.Email);
        log.LogWarning("2FA für Konto {User} zurückgesetzt", user.Id);
        return RedirectToPage();
    }

    public async Task<IActionResult> OnPostBlockAsync(Guid userId, bool blocked)
    {
        var user = await ParentAsync(userId);
        if (user is null) return NotFound();
        await admin.SetBlockedAsync(user, blocked);
        TempData["Message"] = blocked ? Ui.F("{0} ist gesperrt.", user.Email) : Ui.F("{0} ist entsperrt.", user.Email);
        return RedirectToPage();
    }

    public async Task<IActionResult> OnPostDeleteUserAsync(Guid userId)
    {
        var user = await ParentAsync(userId);
        if (user is null) return NotFound();
        await users.DeleteAsync(user);
        await DbTicketStore.RemoveAllAsync(db, user.Id);
        TempData["Message"] = Ui.F("Konto {0} gelöscht. Die Familie und ihre Daten bleiben erhalten.", user.Email);
        return RedirectToPage();
    }

    public async Task<IActionResult> OnPostMoveUserAsync(Guid userId, Guid familyId)
    {
        var user = await ParentAsync(userId);
        if (user is null || !await db.Families.AnyAsync(f => f.Id == familyId)) return NotFound();
        user.FamilyId = familyId;
        await users.UpdateAsync(user);
        await users.UpdateSecurityStampAsync(user);
        TempData["Message"] = Ui.F("{0} wurde einer anderen Familie zugeordnet.", user.Email);
        return RedirectToPage();
    }

    public async Task<IActionResult> OnPostRenameFamilyAsync(Guid familyId, string name)
    {
        var fam = await db.Families.FirstOrDefaultAsync(f => f.Id == familyId);
        if (fam is null || string.IsNullOrWhiteSpace(name)) return NotFound();
        fam.Name = name.Trim()[..Math.Min(100, name.Trim().Length)];
        await db.SaveChangesAsync();
        return RedirectToPage();
    }

    public async Task<IActionResult> OnPostDeleteFamilyAsync(Guid familyId, string confirm)
    {
        var fam = await db.Families.FirstOrDefaultAsync(f => f.Id == familyId);
        if (fam is null) return NotFound();
        if (!string.Equals(confirm?.Trim(), fam.Name, StringComparison.Ordinal))
        {
            TempData["Error"] = Ui.T("Zum Löschen bitte den Familiennamen exakt eingeben.");
            return RedirectToPage();
        }
        await admin.DeleteFamilyAsync(familyId);
        log.LogWarning("Familie {Family} gelöscht", familyId);
        TempData["Message"] = Ui.F("Familie {0} mit allen Konten, Kindern, Geräten und Statistiken gelöscht.", fam.Name);
        return RedirectToPage();
    }

    /// <summary>Only parent accounts can be managed here – never super admins (incl. yourself).</summary>
    private async Task<ParentUser?> ParentAsync(Guid id)
    {
        var user = await users.FindByIdAsync(id.ToString());
        if (user is null || await users.IsInRoleAsync(user, PaCoRoles.SuperAdmin)) return null;
        return user;
    }

    private string BaseUrl() =>
        !string.IsNullOrWhiteSpace(options.Value.PublicUrl) ? options.Value.PublicUrl.TrimEnd('/') : $"{Request.Scheme}://{Request.Host}";
}
