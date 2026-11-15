using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.Options;
using PaCo.Server.Data;

namespace PaCo.Server.Services;

public static class PaCoClaims
{
    public const string Family = "paco:family";
    public const string MustChangePassword = "paco:mcp";
    public const string TwoFactor = "paco:2fa";
    public const string DisplayName = "paco:name";

    public const string Language = "paco:lang";
}

public static class PaCoRoles
{
    public const string SuperAdmin = "SuperAdmin";
}

public static class PaCoPolicies
{
    /// <summary>Parent with a family, password changed and 2FA active.</summary>
    public const string Parent = "Parent";

    /// <summary>Super admin with password changed and 2FA active.</summary>
    public const string SuperAdmin = "SuperAdmin";
}

public interface IFamilyContext
{
    /// <summary>The signed-in parent's family, or null.</summary>
    Guid? FamilyId { get; }
}

public sealed class HttpFamilyContext(IHttpContextAccessor accessor) : IFamilyContext
{
    public Guid? FamilyId
    {
        get
        {
            var user = accessor.HttpContext?.User;
            if (user?.Identity?.IsAuthenticated != true) return null;
            // Family data is only reachable after the 2FA setup is complete.
            if (user.FindFirstValue(PaCoClaims.TwoFactor) != "1" || user.FindFirstValue(PaCoClaims.MustChangePassword) == "1") return null;
            return Guid.TryParse(user.FindFirstValue(PaCoClaims.Family), out var id) ? id : null;
        }
    }
}

/// <summary>Adds family / onboarding state as claims so middleware and filters need no DB lookups.</summary>
public sealed class PaCoClaimsFactory(
    UserManager<ParentUser> users,
    RoleManager<IdentityRole<Guid>> roles,
    IOptions<IdentityOptions> options)
    : UserClaimsPrincipalFactory<ParentUser, IdentityRole<Guid>>(users, roles, options)
{
    protected override async Task<ClaimsIdentity> GenerateClaimsAsync(ParentUser user)
    {
        var id = await base.GenerateClaimsAsync(user);
        if (user.FamilyId is { } fam) id.AddClaim(new Claim(PaCoClaims.Family, fam.ToString()));
        id.AddClaim(new Claim(PaCoClaims.MustChangePassword, user.MustChangePassword ? "1" : "0"));
        id.AddClaim(new Claim(PaCoClaims.TwoFactor, user.TwoFactorEnabled ? "1" : "0"));
        id.AddClaim(new Claim(PaCoClaims.DisplayName, string.IsNullOrWhiteSpace(user.DisplayName) ? user.Email ?? "" : user.DisplayName));
        if (user.Language is { } lang) id.AddClaim(new Claim(PaCoClaims.Language, lang));
        return id;
    }
}

public static class Secrets
{
    // No 0/O/1/I/L to avoid typos when codes are typed by hand.
    private const string CodeAlphabet = "ABCDEFGHJKMNPQRSTUVWXYZ23456789";

    /// <summary>Pairing code like "K7QX-M2PA" (~40 bits, single use, expires).</summary>
    public static string PairingCode()
    {
        var s = RandomNumberGenerator.GetString(CodeAlphabet, 8);
        return $"{s[..4]}-{s[4..]}";
    }

    public static string NormalizeCode(string code) =>
        new string(code.Where(char.IsLetterOrDigit).Select(char.ToUpperInvariant).ToArray());

    /// <summary>Temporary password that satisfies the password policy; must be changed at first login.</summary>
    public static string TemporaryPassword()
    {
        const string lower = "abcdefghijkmnpqrstuvwxyz", upper = "ABCDEFGHJKLMNPQRSTUVWXYZ", digits = "23456789", sym = "!#%+-=?";
        var chars = RandomNumberGenerator.GetString(lower + upper + digits, 14).ToList();
        chars.Insert(RandomNumberGenerator.GetInt32(chars.Count), lower[RandomNumberGenerator.GetInt32(lower.Length)]);
        chars.Insert(RandomNumberGenerator.GetInt32(chars.Count), upper[RandomNumberGenerator.GetInt32(upper.Length)]);
        chars.Insert(RandomNumberGenerator.GetInt32(chars.Count), digits[RandomNumberGenerator.GetInt32(digits.Length)]);
        chars.Insert(RandomNumberGenerator.GetInt32(chars.Count), sym[RandomNumberGenerator.GetInt32(sym.Length)]);
        return new string(chars.ToArray());
    }

    public static string DeviceToken() => Base64Url(RandomNumberGenerator.GetBytes(32));

    public static string Sha256Hex(string value) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(value)));

    public static bool FixedTimeEqualsHex(string a, string b) =>
        a.Length == b.Length && CryptographicOperations.FixedTimeEquals(Encoding.ASCII.GetBytes(a), Encoding.ASCII.GetBytes(b));

    private static string Base64Url(byte[] bytes) => Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
}
