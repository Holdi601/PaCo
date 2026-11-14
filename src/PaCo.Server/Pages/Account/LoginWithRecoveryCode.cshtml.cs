using System.ComponentModel.DataAnnotations;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.AspNetCore.RateLimiting;
using PaCo.Server.Data;

namespace PaCo.Server.Pages.Account;

[EnableRateLimiting(AccountRateLimit.Name)]
public sealed class LoginWithRecoveryCodeModel(SignInManager<ParentUser> signIn, ILogger<LoginWithRecoveryCodeModel> log) : PageModel
{
    [BindProperty, Required(ErrorMessage = "Bitte Notfall-Code eingeben.")]
    public string Code { get; set; } = "";

    public string? Error { get; set; }

    public async Task<IActionResult> OnGetAsync() =>
        await signIn.GetTwoFactorAuthenticationUserAsync() is null ? RedirectToPage("./Login") : Page();

    public async Task<IActionResult> OnPostAsync()
    {
        if (!ModelState.IsValid) return Page();
        var user = await signIn.GetTwoFactorAuthenticationUserAsync();
        if (user is null) return RedirectToPage("./Login");

        var result = await signIn.TwoFactorRecoveryCodeSignInAsync(Code.Replace(" ", ""));
        if (result.Succeeded)
        {
            log.LogWarning("Anmeldung mit Notfall-Code: {User}", user.Email);
            TempData["Message"] = "Mit Notfall-Code angemeldet. Dieser Code ist jetzt verbraucht – richte die Authenticator-App bei Bedarf neu ein (Konto → 2FA neu einrichten).";
            return RedirectToPage("/Index");
        }
        Error = result.IsLockedOut ? "Zu viele Fehlversuche. Das Konto ist für 15 Minuten gesperrt." : "Der Notfall-Code ist ungültig.";
        return Page();
    }
}
