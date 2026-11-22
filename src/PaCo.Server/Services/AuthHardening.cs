using System.Buffers.Binary;
using System.Security.Cryptography;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using PaCo.Server.Data;

namespace PaCo.Server.Services;

/// <summary>
/// Authenticator codes (TOTP, RFC 6238): window ±1 step (±30 s) instead of ±2, and every code works only ONCE –
/// a code seen over someone's shoulder or captured cannot be used for a second sign-in.
/// </summary>
public sealed class OneTimeAuthenticatorTokenProvider : IUserTwoFactorTokenProvider<ParentUser>
{
    private const string LoginProvider = "PaCo";
    private const string LastStepName = "LastTotpStep";

    public async Task<bool> CanGenerateTwoFactorTokenAsync(UserManager<ParentUser> manager, ParentUser user) =>
        !string.IsNullOrWhiteSpace(await manager.GetAuthenticatorKeyAsync(user));

    public Task<string> GenerateAsync(string purpose, UserManager<ParentUser> manager, ParentUser user) => Task.FromResult(string.Empty);

    public async Task<bool> ValidateAsync(string purpose, string token, UserManager<ParentUser> manager, ParentUser user)
    {
        var key = await manager.GetAuthenticatorKeyAsync(user);
        if (string.IsNullOrWhiteSpace(key) || !int.TryParse(token, out var code) || token.Length != 6) return false;
        var secret = Base32Decode(key);
        var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds() / 30;
        long.TryParse(await manager.GetAuthenticationTokenAsync(user, LoginProvider, LastStepName), out var lastUsed);
        for (var step = now - 1; step <= now + 1; step++)
        {
            if (step <= lastUsed || Totp(secret, step) != code) continue;
            await manager.SetAuthenticationTokenAsync(user, LoginProvider, LastStepName, step.ToString(System.Globalization.CultureInfo.InvariantCulture));
            return true;
        }
        return false;
    }

    private static int Totp(byte[] key, long step)
    {
        Span<byte> msg = stackalloc byte[8];
        BinaryPrimitives.WriteInt64BigEndian(msg, step);
        var hash = HMACSHA1.HashData(key, msg);
        var offset = hash[^1] & 0xF;
        var bin = ((hash[offset] & 0x7F) << 24) | (hash[offset + 1] << 16) | (hash[offset + 2] << 8) | hash[offset + 3];
        return bin % 1_000_000;
    }

    private static byte[] Base32Decode(string s)
    {
        const string alphabet = "ABCDEFGHIJKLMNOPQRSTUVWXYZ234567";
        var bits = 0;
        var value = 0;
        var output = new List<byte>();
        foreach (var c in s.ToUpperInvariant().Where(c => c != '=' && c != ' '))
        {
            var i = alphabet.IndexOf(c);
            if (i < 0) continue;
            value = (value << 5) | i;
            bits += 5;
            if (bits >= 8)
            {
                output.Add((byte)(value >> (bits - 8)));
                bits -= 8;
            }
        }
        return [.. output];
    }
}

/// <summary>
/// Sign-in sessions are kept on the server: "Abmelden" really ends the session (a copied cookie is worthless
/// afterwards), and removed or blocked parents are signed out at their next request.
/// </summary>
public sealed class DbTicketStore(IServiceScopeFactory scopes) : ITicketStore
{
    private static readonly TicketSerializer Serializer = TicketSerializer.Default;

    public async Task<string> StoreAsync(AuthenticationTicket ticket)
    {
        var key = Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(32));
        await RenewAsync(key, ticket);
        return key;
    }

    public async Task RenewAsync(string key, AuthenticationTicket ticket)
    {
        using var scope = scopes.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<PaCoDb>();
        var row = await db.AuthSessions.FindAsync(key);
        if (row is null)
        {
            row = new AuthSession { Id = key };
            db.AuthSessions.Add(row);
        }
        row.UserId = Guid.TryParse(ticket.Principal.FindFirst(System.Security.Claims.ClaimTypes.NameIdentifier)?.Value, out var uid) ? uid : null;
        row.ExpiresUtc = ticket.Properties.ExpiresUtc ?? DateTimeOffset.UtcNow.AddDays(7);
        row.Value = Serializer.Serialize(ticket);
        await db.SaveChangesAsync();
    }

    public async Task<AuthenticationTicket?> RetrieveAsync(string key)
    {
        using var scope = scopes.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<PaCoDb>();
        var row = await db.AuthSessions.AsNoTracking().FirstOrDefaultAsync(s => s.Id == key);
        if (row is null || row.ExpiresUtc < DateTimeOffset.UtcNow) return null;
        return Serializer.Deserialize(row.Value);
    }

    public async Task RemoveAsync(string key)
    {
        using var scope = scopes.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<PaCoDb>();
        await db.AuthSessions.Where(s => s.Id == key).ExecuteDeleteAsync();
    }

    /// <summary>Ends every session of a parent (removed, blocked, access reset, "sign out everywhere").</summary>
    public static Task RemoveAllAsync(PaCoDb db, Guid userId) => db.AuthSessions.Where(s => s.UserId == userId).ExecuteDeleteAsync();
}

/// <summary>Registers the session store for the Identity sign-in cookie.</summary>
public sealed class TicketStoreSetup(IServiceScopeFactory scopes) : Microsoft.Extensions.Options.IPostConfigureOptions<CookieAuthenticationOptions>
{
    public void PostConfigure(string? name, CookieAuthenticationOptions options)
    {
        if (name == IdentityConstants.ApplicationScheme) options.SessionStore = new DbTicketStore(scopes);
    }
}
