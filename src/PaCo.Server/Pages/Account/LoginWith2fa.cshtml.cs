using System.ComponentModel.DataAnnotations;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.AspNetCore.RateLimiting;
using PaCo.Server.Data;
using PaCo.Server.Services;

namespace PaCo.Server.Pages.Account;

[EnableRateLimiting(AccountRateLimit.Name)]
public sealed class LoginWith2faModel(SignInManager<ParentUser> signIn, ILogger<LoginWith2faModel> log) : PageModel
{
    [BindProperty, Required(ErrorMessage = "Bitte den 6-stelligen Code eingeben."), StringLength(7, MinimumLength = 6, ErrorMessage = "Der Code hat 6 Ziffern.")]
    public string Code { get; set; } = "";

    [BindProperty]
    public bool RememberMe { get; set; }

    [BindProperty]
    public bool RememberDevice { get; set; }

    public string? Error { get; set; }

    public async Task<IActionResult> OnGetAsync(bool rememberMe)
    {
        if (await signIn.GetTwoFactorAuthenticationUserAsync() is null) return RedirectToPage("./Login");
        RememberMe = rememberMe;
        return Page();
    }

    public async Task<IActionResult> OnPostAsync()
    {
        if (!ModelState.IsValid) return Page();
        var user = await signIn.GetTwoFactorAuthenticationUserAsync();
        if (user is null) return RedirectToPage("./Login");

        var code = Code.Replace(" ", "").Replace("-", "");
        var result = await signIn.TwoFactorAuthenticatorSignInAsync(code, RememberMe, RememberDevice);
        if (result.Succeeded)
        {
            await LoginTracking.TouchAsync(signIn.UserManager, user.Email!);
            return RedirectToPage("/Index");
        }
        if (result.IsLockedOut)
        {
            Error = "Zu viele Fehlversuche. Das Konto ist für 15 Minuten gesperrt.";
            return Page();
        }
        log.LogWarning("Falscher 2FA-Code für {User}", user.Email);
        Error = "Der Code ist falsch. Bitte prüfe, ob die Uhrzeit am Handy stimmt.";
        return Page();
    }
}
