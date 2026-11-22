using System.ComponentModel.DataAnnotations;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.AspNetCore.RateLimiting;
using PaCo.Server.Data;
using PaCo.Server.Services;

namespace PaCo.Server.Pages.Account;

/// <summary>Invited parents set their own password here (link from the super admin, valid 7 days, single use).</summary>
[EnableRateLimiting(AccountRateLimit.Name)]
public sealed class AcceptInviteModel(UserManager<ParentUser> users, SignInManager<ParentUser> signIn, ILogger<AcceptInviteModel> log) : PageModel
{
    [BindProperty(SupportsGet = true)]
    public Guid Uid { get; set; }

    [BindProperty(SupportsGet = true)]
    public string Token { get; set; } = "";

    [BindProperty, Required(ErrorMessage = "Bitte Passwort eingeben."), DataType(DataType.Password)]
    public string Password { get; set; } = "";

    [BindProperty, DataType(DataType.Password), Compare(nameof(Password), ErrorMessage = "Die Passwörter stimmen nicht überein.")]
    public string ConfirmPassword { get; set; } = "";

    public string? Email { get; set; }

    public string? Name { get; set; }

    public bool Invalid { get; set; }

    public async Task<IActionResult> OnGetAsync()
    {
        var user = await ValidAsync();
        if (user is null)
        {
            Invalid = true;
            return Page();
        }
        Email = user.Email;
        Name = user.DisplayName;
        return Page();
    }

    public async Task<IActionResult> OnPostAsync()
    {
        var user = await ValidAsync();
        if (user is null)
        {
            Invalid = true;
            return Page();
        }
        Email = user.Email;
        Name = user.DisplayName;
        if (!ModelState.IsValid) return Page();

        var result = await users.ResetPasswordAsync(user, Token, Password);
        if (!result.Succeeded)
        {
            foreach (var e in result.Errors) ModelState.AddModelError("", e.Description);
            return Page();
        }
        user.MustChangePassword = false;
        await users.UpdateAsync(user);
        await users.UpdateSecurityStampAsync(user); // link is now used up
        log.LogInformation("Einladung angenommen: {User}", user.Email);

        if (await users.GetTwoFactorEnabledAsync(user))
        {
            // Only the password was reset: the normal sign-in (with the authenticator code) still applies – a reset link
            // alone never opens an account.
            TempData["Message"] = Ui.T("Neues Passwort gespeichert. Bitte jetzt mit Passwort und Code aus der Authenticator-App anmelden.");
            return RedirectToPage("./Login");
        }
        await signIn.SignInAsync(user, isPersistent: false);
        await LoginTracking.TouchAsync(users, user.Email!);
        return RedirectToPage("./SetupAuthenticator");
    }

    private async Task<ParentUser?> ValidAsync()
    {
        if (Uid == Guid.Empty || string.IsNullOrEmpty(Token)) return null;
        var user = await users.FindByIdAsync(Uid.ToString());
        if (user is null || !user.InvitePending || await users.IsLockedOutAsync(user)) return null;
        var ok = await users.VerifyUserTokenAsync(user, users.Options.Tokens.PasswordResetTokenProvider, UserManager<ParentUser>.ResetPasswordTokenPurpose, Token);
        return ok ? user : null;
    }
}
