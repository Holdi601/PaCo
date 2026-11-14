using System.ComponentModel.DataAnnotations;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.AspNetCore.RateLimiting;
using PaCo.Server.Data;

namespace PaCo.Server.Pages.Account;

[EnableRateLimiting(AccountRateLimit.Name)]
public sealed class ChangePasswordModel(UserManager<ParentUser> users, SignInManager<ParentUser> signIn) : PageModel
{
    [BindProperty, Required(ErrorMessage = "Bitte aktuelles Passwort eingeben."), DataType(DataType.Password)]
    public string CurrentPassword { get; set; } = "";

    [BindProperty, Required(ErrorMessage = "Bitte neues Passwort eingeben."), DataType(DataType.Password)]
    public string NewPassword { get; set; } = "";

    [BindProperty, DataType(DataType.Password), Compare(nameof(NewPassword), ErrorMessage = "Die Passwörter stimmen nicht überein.")]
    public string ConfirmPassword { get; set; } = "";

    public bool Forced { get; set; }

    public async Task<IActionResult> OnGetAsync()
    {
        var user = await users.GetUserAsync(User);
        if (user is null) return RedirectToPage("./Login");
        Forced = user.MustChangePassword;
        return Page();
    }

    public async Task<IActionResult> OnPostAsync()
    {
        var user = await users.GetUserAsync(User);
        if (user is null) return RedirectToPage("./Login");
        Forced = user.MustChangePassword;
        if (!ModelState.IsValid) return Page();
        if (NewPassword == CurrentPassword)
        {
            ModelState.AddModelError(nameof(NewPassword), "Das neue Passwort muss sich vom alten unterscheiden.");
            return Page();
        }

        var result = await users.ChangePasswordAsync(user, CurrentPassword, NewPassword);
        if (!result.Succeeded)
        {
            foreach (var e in result.Errors) ModelState.AddModelError("", e.Description);
            return Page();
        }
        user.MustChangePassword = false;
        await users.UpdateAsync(user);
        await signIn.RefreshSignInAsync(user);
        TempData["Message"] = "Passwort geändert.";
        return RedirectToPage(user.TwoFactorEnabled ? "/Index" : "./SetupAuthenticator");
    }
}
