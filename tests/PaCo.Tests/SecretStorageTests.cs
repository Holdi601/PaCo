using System.Net;
using System.Security.Cryptography;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using PaCo.Server;
using PaCo.Server.Data;
using PaCo.Server.Services;
using PaCo.Shared;

namespace PaCo.Tests;

public partial class WebFlowTests
{
    [Fact]
    public void ReleaseSignaturesWithTheBuiltInKey()
    {
        Assert.Contains("BEGIN PUBLIC KEY", ServerOptions.BuiltInReleasePublicKey);
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var pub = key.ExportSubjectPublicKeyInfoPem();
        var sha = new string('a', 64);
        var sig = ReleaseSignature.Sign(key, "1.8.0", sha);
        Assert.True(ReleaseSignature.Verify(pub, "1.8.0", sha, sig));
        Assert.False(ReleaseSignature.Verify(pub, "1.8.1", sha, sig));                 // other version
        Assert.False(ReleaseSignature.Verify(pub, "1.8.0", new string('b', 64), sig)); // other file
        Assert.False(ReleaseSignature.Verify(ServerOptions.BuiltInReleasePublicKey, "1.8.0", sha, sig)); // other key
    }

    [Fact]
    public async Task SecretsAreStoredEncrypted()
    {
        var (_, fam) = await ParentOfNewFamilyAsync("sec-storage@test.local");
        using var scope = _f.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<PaCoDb>();
        var users = scope.ServiceProvider.GetRequiredService<UserManager<ParentUser>>();
        var user = (await users.FindByEmailAsync("sec-storage@test.local"))!;
        await users.GenerateNewTwoFactorRecoveryCodesAsync(user, 8);

        // Raw database values: 2FA key and recovery codes are not readable…
        var raw = await db.UserTokens.Where(t => t.UserId == user.Id).ToListAsync();
        Assert.All(raw.Where(t => PaCoUserStore.SecretTokens.Contains(t.Name)), t => Assert.StartsWith(PaCoUserStore.Prefix, t.Value));
        // …but Identity still works with them.
        Assert.Equal(32, (await users.GetAuthenticatorKeyAsync(user))!.Length);
        Assert.Equal(8, await users.CountRecoveryCodesAsync(user));

        // The family unlock key column holds ciphertext, the entity the real key.
        var conn = db.Database.GetDbConnection();
        await conn.OpenAsync();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = $"SELECT UnlockKey FROM Families WHERE Id = '{fam.ToString().ToUpperInvariant()}'";
        var column = (byte[]?)await cmd.ExecuteScalarAsync();
        Assert.True(SecretBox.IsWrapped(column));
        Assert.Equal(32, (await db.Families.SingleAsync(f => f.Id == fam)).UnlockKey.Length);
    }

    [Fact]
    public async Task NewRecoveryCodesAndAuthenticatorResetNeedThePassword()
    {
        var (b, _) = await ParentOfNewFamilyAsync("sec-reauth@test.local");
        var manage = await b.GetStringAsync("/Account/Manage");
        var r = await PostFormAsync(b, "/Account/Manage?handler=RecoveryCodes", manage, new() { ["currentPassword"] = "falsch" });
        Assert.Equal("/Account/Manage", r.Headers.Location?.ToString());
        manage = await b.GetStringAsync("/Account/Manage");
        r = await PostFormAsync(b, "/Account/Manage?handler=RecoveryCodes", manage, new() { ["currentPassword"] = "Eltern-Passwort-123" });
        Assert.Equal("/Account/SetupAuthenticator", r.Headers.Location?.ToString());
    }

    [Fact]
    public async Task PasswordOnlyResetKeepsTwoFactor()
    {
        var (_, _) = await ParentOfNewFamilyAsync("sec-pwreset@test.local");
        string link;
        using (var scope = _f.Services.CreateScope())
        {
            var admin = scope.ServiceProvider.GetRequiredService<AccountAdmin>();
            var users = scope.ServiceProvider.GetRequiredService<UserManager<ParentUser>>();
            var user = (await users.FindByEmailAsync("sec-pwreset@test.local"))!;
            link = await admin.ResetPasswordOnlyAsync(user, "https://localhost");
            Assert.True(await users.GetTwoFactorEnabledAsync(user));
        }
        // Whoever uses the link (even the server operator) only sets a password – no session without the 2FA code.
        var c = Browser();
        var path = link[link.IndexOf("/Account/", StringComparison.Ordinal)..];
        var page = await c.GetStringAsync(path);
        var r = await PostFormAsync(c, path, page, new() { ["Password"] = "Neues-Passwort-456", ["ConfirmPassword"] = "Neues-Passwort-456" });
        Assert.Equal("/Account/Login", r.Headers.Location?.ToString());
        Assert.NotEqual(HttpStatusCode.OK, (await c.GetAsync("/Dashboard")).StatusCode);
    }
}
