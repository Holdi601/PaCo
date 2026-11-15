using System.ComponentModel.DataAnnotations;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using PaCo.Server.Data;
using PaCo.Server.Services;

namespace PaCo.Server.Pages;

/// <summary>Parents see the other parents of their family and can invite / remove co-parents (own family only).</summary>
[EnableRateLimiting(AccountRateLimit.Name)]
public sealed class FamilyModel(UserManager<ParentUser> users, AccountAdmin admin, IFamilyContext family, IOptions<ServerOptions> options,
    TimeProvider clock, ILogger<FamilyModel> log) : PageModel
{
    public IReadOnlyList<ParentUser> Parents { get; private set; } = [];

    public Guid MyId { get; private set; }

    public DateTimeOffset Now { get; private set; }

    [TempData]
    public string? InviteLink { get; set; }

    [TempData]
    public string? InviteFor { get; set; }

    [BindProperty, Required(ErrorMessage = "Bitte Namen eingeben."), StringLength(100)]
    public string InviteName { get; set; } = "";

    [BindProperty, Required(ErrorMessage = "Bitte E-Mail eingeben."), EmailAddress(ErrorMessage = "Ungültige E-Mail-Adresse.")]
    public string InviteEmail { get; set; } = "";

    public async Task OnGetAsync()
    {
        Now = clock.GetUtcNow();
        MyId = ParentActions.UserId(User);
        var fid = family.FamilyId!.Value;
        Parents = await users.Users.Where(u => u.FamilyId == fid).OrderBy(u => u.DisplayName).ToListAsync();
    }

    public async Task<IActionResult> OnPostInviteAsync()
    {
        if (await users.FindByEmailAsync(InviteEmail.Trim()) is not null)
            ModelState.AddModelError(nameof(InviteEmail), Ui.T("Diese E-Mail-Adresse hat bereits ein Konto."));
        if (!ModelState.IsValid)
        {
            await OnGetAsync();
            return Page();
        }
        var (user, link) = await admin.InviteParentAsync(InviteEmail, InviteName, family.FamilyId!.Value, BaseUrl());
        log.LogInformation("Elternteil {Inviter} lädt {Email} in Familie {Family} ein", ParentActions.UserId(User), user.Email, family.FamilyId);
        InviteLink = link;
        InviteFor = $"{user.DisplayName} ({user.Email})";
        return RedirectToPage();
    }

    public async Task<IActionResult> OnPostNewLinkAsync(Guid userId)
    {
        var u = await OtherParentAsync(userId);
        if (u is null || !u.InvitePending) return NotFound();
        InviteLink = await admin.InviteLinkAsync(u, BaseUrl());
        InviteFor = $"{u.DisplayName} ({u.Email})";
        return RedirectToPage();
    }

    public async Task<IActionResult> OnPostRemoveAsync(Guid userId)
    {
        var u = await OtherParentAsync(userId);
        if (u is null) return NotFound();
        await users.DeleteAsync(u);
        log.LogWarning("Elternteil {Email} aus Familie {Family} entfernt durch {By}", u.Email, family.FamilyId, ParentActions.UserId(User));
        TempData["Message"] = Ui.F("{0} wurde aus der Familie entfernt.", u.DisplayName);
        return RedirectToPage();
    }

    /// <summary>Another parent of the same family – never yourself, never another family, never a super admin.</summary>
    private async Task<ParentUser?> OtherParentAsync(Guid id)
    {
        if (id == ParentActions.UserId(User)) return null;
        var u = await users.FindByIdAsync(id.ToString());
        if (u is null || u.FamilyId != family.FamilyId || await users.IsInRoleAsync(u, PaCoRoles.SuperAdmin)) return null;
        return u;
    }

    private string BaseUrl() =>
        !string.IsNullOrWhiteSpace(options.Value.PublicUrl) ? options.Value.PublicUrl.TrimEnd('/') : $"{Request.Scheme}://{Request.Host}";
}
