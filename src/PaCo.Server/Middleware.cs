using System.Security.Claims;
using Microsoft.AspNetCore.Identity;
using PaCo.Server.Services;

namespace PaCo.Server;

public static class AccountRateLimit
{
    public const string Name = "account";
}

public static class Middleware
{
    /// <summary>Strict browser security headers. Pages use no inline scripts or styles.</summary>
    public static IApplicationBuilder UseSecurityHeaders(this IApplicationBuilder app) => app.Use(async (ctx, next) =>
    {
        var h = ctx.Response.Headers;
        // Map tiles come from OpenStreetMap; everything else only from this server.
        h.ContentSecurityPolicy = "default-src 'self'; img-src 'self' data: https://tile.openstreetmap.org; style-src 'self'; script-src 'self'; " +
                                  "frame-src https://maps.google.com https://www.google.com; " +
                                  "frame-ancestors 'none'; form-action 'self'; base-uri 'none'; object-src 'none'";
        h.XContentTypeOptions = "nosniff";
        h.XFrameOptions = "DENY";
        h["Referrer-Policy"] = "no-referrer";
        h["Permissions-Policy"] = "camera=(), microphone=(), geolocation=(), payment=()";
        h["Cross-Origin-Opener-Policy"] = "same-origin";
        if (!ctx.Request.Path.StartsWithSegments("/lib") && !ctx.Request.Path.StartsWithSegments("/css") && !ctx.Request.Path.StartsWithSegments("/js"))
            h.CacheControl = "no-store";
        await next();
    });

    /// <summary>
    /// Signed-in users who still have a temporary password or no 2FA can only reach the onboarding pages.
    /// </summary>
    public static IApplicationBuilder UseOnboardingRedirect(this IApplicationBuilder app) => app.Use(async (ctx, next) =>
    {
        var user = ctx.User;
        if (user.Identity?.IsAuthenticated == true && !ctx.Request.Path.StartsWithSegments("/api"))
        {
            var path = ctx.Request.Path;
            var allowed = path.StartsWithSegments("/Account/Logout") || path.StartsWithSegments("/lib") ||
                          path.StartsWithSegments("/css") || path.StartsWithSegments("/js") || path.StartsWithSegments("/favicon.ico") || path.StartsWithSegments("/favicon.svg") || path.StartsWithSegments("/Language");
            if (!allowed)
            {
                if (user.FindFirstValue(PaCoClaims.MustChangePassword) == "1" && !path.StartsWithSegments("/Account/ChangePassword"))
                {
                    ctx.Response.Redirect("/Account/ChangePassword");
                    return;
                }
                if (user.FindFirstValue(PaCoClaims.MustChangePassword) != "1" && user.FindFirstValue(PaCoClaims.TwoFactor) != "1" &&
                    !path.StartsWithSegments("/Account/SetupAuthenticator"))
                {
                    ctx.Response.Redirect("/Account/SetupAuthenticator");
                    return;
                }
            }
        }
        await next();
    });
}

public sealed class GermanIdentityErrors : IdentityErrorDescriber
{
    public override IdentityError PasswordTooShort(int length) => new() { Code = nameof(PasswordTooShort), Description = $"Das Passwort muss mindestens {length} Zeichen lang sein." };
    public override IdentityError PasswordRequiresDigit() => new() { Code = nameof(PasswordRequiresDigit), Description = "Das Passwort braucht mindestens eine Ziffer." };
    public override IdentityError PasswordRequiresLower() => new() { Code = nameof(PasswordRequiresLower), Description = "Das Passwort braucht mindestens einen Kleinbuchstaben." };
    public override IdentityError PasswordRequiresUpper() => new() { Code = nameof(PasswordRequiresUpper), Description = "Das Passwort braucht mindestens einen Großbuchstaben." };
    public override IdentityError PasswordMismatch() => new() { Code = nameof(PasswordMismatch), Description = "Das aktuelle Passwort ist falsch." };
    public override IdentityError DuplicateEmail(string email) => new() { Code = nameof(DuplicateEmail), Description = $"Die E-Mail-Adresse {email} wird bereits verwendet." };
    public override IdentityError DuplicateUserName(string userName) => new() { Code = nameof(DuplicateUserName), Description = $"Der Benutzername {userName} wird bereits verwendet." };
    public override IdentityError InvalidEmail(string? email) => new() { Code = nameof(InvalidEmail), Description = $"Die E-Mail-Adresse {email} ist ungültig." };
}
