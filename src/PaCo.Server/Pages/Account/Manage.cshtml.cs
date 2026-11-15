using System.ComponentModel.DataAnnotations;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using PaCo.Server.Data;
using PaCo.Shared.I18n;

namespace PaCo.Server.Pages.Account;

public sealed class ManageModel(UserManager<ParentUser> users, SignInManager<ParentUser> signIn) : PageModel
{
    [BindProperty, Required(ErrorMessage = "Bitte einen Namen eingeben."), StringLength(100)]
    public string DisplayName { get; set; } = "";

    public string Email { get; set; } = "";

    public int RecoveryCodesLeft { get; set; }

    /// <summary>Language saved in the account (falls back to the current request language).</summary>
    public string AccountLanguage { get; set; } = Languages.Source;

    public async Task<IActionResult> OnGetAsync()
    {
        var user = await users.GetUserAsync(User);
        if (user is null) return RedirectToPage("./Login");
        DisplayName = user.DisplayName;
        Email = user.Email ?? "";
        RecoveryCodesLeft = await users.CountRecoveryCodesAsync(user);
        AccountLanguage = Languages.IsSupported(user.Language) ? user.Language! : Ui.Lang;
        return Page();
    }

    public async Task<IActionResult> OnPostAsync()
    {
        var user = await users.GetUserAsync(User);
        if (user is null) return RedirectToPage("./Login");
        if (!ModelState.IsValid)
        {
            Email = user.Email ?? "";
            RecoveryCodesLeft = await users.CountRecoveryCodesAsync(user);
            AccountLanguage = Languages.IsSupported(user.Language) ? user.Language! : Ui.Lang;
            return Page();
        }
        user.DisplayName = DisplayName.Trim();
        await users.UpdateAsync(user);
        await signIn.RefreshSignInAsync(user);
        TempData["Message"] = Ui.T("Dein Name wurde gespeichert.");
        return RedirectToPage();
    }

    public async Task<IActionResult> OnPostRecoveryCodesAsync()
    {
        var user = await users.GetUserAsync(User);
        if (user is null) return RedirectToPage("./Login");
        var codes = await users.GenerateNewTwoFactorRecoveryCodesAsync(user, 8) ?? [];
        TempData["RecoveryCodes"] = string.Join("\n", codes);
        return RedirectToPage("./SetupAuthenticator");
    }

    public async Task<IActionResult> OnPostLogoutEverywhereAsync()
    {
        var user = await users.GetUserAsync(User);
        if (user is null) return RedirectToPage("./Login");
        await users.UpdateSecurityStampAsync(user);
        await signIn.ForgetTwoFactorClientAsync();
        await signIn.SignOutAsync();
        return RedirectToPage("./Login");
    }
}
