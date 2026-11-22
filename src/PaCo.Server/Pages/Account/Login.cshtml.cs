using System.ComponentModel.DataAnnotations;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.AspNetCore.RateLimiting;
using PaCo.Server.Data;
using PaCo.Server.Services;

namespace PaCo.Server.Pages.Account;

[EnableRateLimiting(AccountRateLimit.Name)]
public sealed class LoginModel(SignInManager<ParentUser> signIn, ILogger<LoginModel> log) : PageModel
{
    [BindProperty, Required(ErrorMessage = "Bitte E-Mail eingeben."), EmailAddress(ErrorMessage = "Ungültige E-Mail-Adresse.")]
    public string Email { get; set; } = "";

    [BindProperty, Required(ErrorMessage = "Bitte Passwort eingeben."), DataType(DataType.Password)]
    public string Password { get; set; } = "";

    [BindProperty]
    public bool RememberMe { get; set; }

    public string? Error { get; set; }

    public IActionResult OnGet() => User.Identity?.IsAuthenticated == true ? RedirectToPage("/Index") : Page();

    public async Task<IActionResult> OnPostAsync()
    {
        if (!ModelState.IsValid) return Page();

        // Unknown address: spend the same time as a real password check, so timing reveals nothing.
        if (await signIn.UserManager.FindByEmailAsync(Email.Trim()) is null)
            signIn.UserManager.PasswordHasher.HashPassword(new ParentUser(), Password);
        var result = await signIn.PasswordSignInAsync(Email.Trim(), Password, RememberMe, lockoutOnFailure: true);
        if (result.Succeeded)
        {
            await LoginTracking.TouchAsync(signIn.UserManager, Email.Trim());
            return RedirectToPage("/Index");
        }
        if (result.RequiresTwoFactor) return RedirectToPage("./LoginWith2fa", new { RememberMe });
        if (result.IsLockedOut)
        {
            log.LogWarning("Konto gesperrt nach Fehlversuchen: {Email}", Email);
        }
        else
        {
            log.LogWarning("Fehlgeschlagener Login für {Email} von {Ip}", Email, HttpContext.Connection.RemoteIpAddress);
        }
        // Same answer for every failure – nobody can find out which addresses have an account.
        Error = Ui.T("Anmeldung fehlgeschlagen. Prüfe E-Mail und Passwort – nach mehreren Fehlversuchen ist die Anmeldung 15 Minuten gesperrt.");
        return Page();
    }
}
