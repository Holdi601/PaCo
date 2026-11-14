using System.ComponentModel.DataAnnotations;
using System.Text;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.AspNetCore.RateLimiting;
using PaCo.Server.Data;
using QRCoder;

namespace PaCo.Server.Pages.Account;

/// <summary>TOTP setup (Google Authenticator, Microsoft Authenticator, Aegis, …) with QR code and recovery codes.</summary>
[EnableRateLimiting(AccountRateLimit.Name)]
public sealed class SetupAuthenticatorModel(UserManager<ParentUser> users, SignInManager<ParentUser> signIn, ILogger<SetupAuthenticatorModel> log) : PageModel
{
    public string SharedKey { get; set; } = "";

    public string QrDataUri { get; set; } = "";

    public bool AlreadyEnabled { get; set; }

    [TempData]
    public string? RecoveryCodes { get; set; }

    [BindProperty, Required(ErrorMessage = "Bitte den Code aus der App eingeben.")]
    public string Code { get; set; } = "";

    public async Task<IActionResult> OnGetAsync()
    {
        var user = await users.GetUserAsync(User);
        if (user is null) return RedirectToPage("./Login");
        if (user.MustChangePassword) return RedirectToPage("./ChangePassword");
        AlreadyEnabled = user.TwoFactorEnabled && RecoveryCodes is null;
        await LoadAsync(user);
        return Page();
    }

    /// <summary>Re-setup (new phone): only allowed for signed-in users who are already fully authenticated.</summary>
    public async Task<IActionResult> OnPostResetAsync()
    {
        var user = await users.GetUserAsync(User);
        if (user is null) return RedirectToPage("./Login");
        await users.SetTwoFactorEnabledAsync(user, false);
        await users.ResetAuthenticatorKeyAsync(user);
        log.LogWarning("2FA zurückgesetzt durch Benutzer {User}", user.Email);
        await signIn.RefreshSignInAsync(user);
        return RedirectToPage();
    }

    public async Task<IActionResult> OnPostAsync()
    {
        var user = await users.GetUserAsync(User);
        if (user is null) return RedirectToPage("./Login");
        if (!ModelState.IsValid)
        {
            await LoadAsync(user);
            return Page();
        }

        var code = Code.Replace(" ", "").Replace("-", "");
        var valid = await users.VerifyTwoFactorTokenAsync(user, users.Options.Tokens.AuthenticatorTokenProvider, code);
        if (!valid)
        {
            ModelState.AddModelError(nameof(Code), "Der Code stimmt nicht. Bitte prüfe, ob die Uhrzeit am Handy automatisch gestellt wird.");
            await LoadAsync(user);
            return Page();
        }

        await users.SetTwoFactorEnabledAsync(user, true);
        var codes = await users.GenerateNewTwoFactorRecoveryCodesAsync(user, 8) ?? [];
        RecoveryCodes = string.Join("\n", codes);
        await signIn.RefreshSignInAsync(user);
        log.LogInformation("2FA aktiviert für {User}", user.Email);
        return RedirectToPage();
    }

    private async Task LoadAsync(ParentUser user)
    {
        var key = await users.GetAuthenticatorKeyAsync(user);
        if (string.IsNullOrEmpty(key))
        {
            await users.ResetAuthenticatorKeyAsync(user);
            key = await users.GetAuthenticatorKeyAsync(user);
        }
        SharedKey = FormatKey(key!);

        var label = Uri.EscapeDataString($"PaCo:{user.Email}");
        var uri = $"otpauth://totp/{label}?secret={key}&issuer=PaCo&digits=6&period=30";
        using var gen = new QRCodeGenerator();
        using var data = gen.CreateQrCode(uri, QRCodeGenerator.ECCLevel.M);
        var png = new PngByteQRCode(data).GetGraphic(8);
        QrDataUri = "data:image/png;base64," + Convert.ToBase64String(png);
    }

    private static string FormatKey(string key)
    {
        var sb = new StringBuilder();
        for (var i = 0; i < key.Length; i += 4)
            sb.Append(key.AsSpan(i, Math.Min(4, key.Length - i))).Append(' ');
        return sb.ToString().ToLowerInvariant().Trim();
    }
}
