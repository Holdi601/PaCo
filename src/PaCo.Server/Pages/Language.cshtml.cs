using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using PaCo.Server.Data;
using PaCo.Shared.I18n;

namespace PaCo.Server.Pages;

/// <summary>Language switch (POST only): remembered in a cookie and, when signed in, in the parent's account.</summary>
public sealed class LanguageModel(UserManager<ParentUser> users, SignInManager<ParentUser> signIn) : PageModel
{
    public IActionResult OnGet() => LocalRedirect("/");

    public async Task<IActionResult> OnPostAsync(string? lang, string? returnUrl)
    {
        if (Languages.IsSupported(lang))
        {
            lang = Languages.All.First(l => l.Code.Equals(lang, StringComparison.OrdinalIgnoreCase)).Code;
            Ui.SetLanguageCookie(Response, lang);
            if (User.Identity?.IsAuthenticated == true && await users.GetUserAsync(User) is { } user)
            {
                user.Language = lang;
                await users.UpdateAsync(user);
                await signIn.RefreshSignInAsync(user);
            }
        }
        return LocalRedirect(Url.IsLocalUrl(returnUrl) ? returnUrl : "/");
    }
}
