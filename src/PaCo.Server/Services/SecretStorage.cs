using System.Security.Cryptography;
using System.Text;
using System.Xml.Linq;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.DataProtection.KeyManagement;
using Microsoft.AspNetCore.DataProtection.XmlEncryption;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Identity.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using PaCo.Server.Data;

namespace PaCo.Server.Services;

/// <summary>
/// The master key (environment variable <c>PaCo__MasterKey</c>, 32 random bytes as Base64) lives OUTSIDE the data
/// directory, e.g. in deploy/.env. It encrypts the Data Protection key ring, which in turn encrypts all secrets in the
/// database (2FA keys, recovery codes, the family key) and the policy signing key. A copy of the data directory alone
/// (backup, stolen disk image) is therefore useless.
/// </summary>
public static class MasterKey
{
    public static byte[]? Parse(string? base64)
    {
        if (string.IsNullOrWhiteSpace(base64)) return null;
        var bytes = Convert.FromBase64String(base64.Trim());
        if (bytes.Length < 32) throw new InvalidOperationException("PaCo__MasterKey muss mindestens 32 Byte (Base64) lang sein.");
        return HKDF.DeriveKey(HashAlgorithmName.SHA256, bytes, 32, info: Encoding.UTF8.GetBytes("paco-dp-keyring-v1"));
    }

    public static string Generate() => Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));
}

/// <summary>Encrypts Data Protection keys with AES-256-GCM under the master key.</summary>
public sealed class MasterKeyXmlEncryptor(byte[] key) : IXmlEncryptor
{
    public EncryptedXmlInfo Encrypt(XElement plaintextElement)
    {
        var plain = Encoding.UTF8.GetBytes(plaintextElement.ToString(SaveOptions.DisableFormatting));
        var nonce = RandomNumberGenerator.GetBytes(12);
        var tag = new byte[16];
        var cipher = new byte[plain.Length];
        using (var aes = new AesGcm(key, 16)) aes.Encrypt(nonce, plain, cipher, tag);
        var element = new XElement("pacoEncryptedKey", new XAttribute("v", "1"), Convert.ToBase64String([.. nonce, .. tag, .. cipher]));
        return new EncryptedXmlInfo(element, typeof(MasterKeyXmlDecryptor));
    }
}

public sealed class MasterKeyXmlDecryptor(IServiceProvider services) : IXmlDecryptor
{
    public XElement Decrypt(XElement encryptedElement)
    {
        var key = MasterKey.Parse(services.GetRequiredService<IOptions<ServerOptions>>().Value.MasterKey)
                  ?? throw new InvalidOperationException("Verschlüsselte Schlüssel gefunden, aber PaCo__MasterKey fehlt.");
        var data = Convert.FromBase64String(encryptedElement.Value);
        var plain = new byte[data.Length - 28];
        using (var aes = new AesGcm(key, 16)) aes.Decrypt(data.AsSpan(0, 12), data.AsSpan(28), data.AsSpan(12, 16), plain);
        return XElement.Parse(Encoding.UTF8.GetString(plain));
    }
}

/// <summary>Byte secrets in database columns (family unlock key): "PCE1" + Data Protection ciphertext.</summary>
public static class SecretBox
{
    private static readonly byte[] Prefix = "PCE1"u8.ToArray();

    public static IDataProtector? Protector { get; set; }

    public static bool IsWrapped(byte[]? v) => v is { Length: > 4 } && v.AsSpan(0, 4).SequenceEqual(Prefix);

    public static byte[] Wrap(byte[] v) => Protector is null || IsWrapped(v) ? v : [.. Prefix, .. Protector.Protect(v)];

    public static byte[] Unwrap(byte[] v) =>
        !IsWrapped(v) ? v : (Protector ?? throw new InvalidOperationException("Datenschutz-Schlüssel nicht verfügbar")).Unprotect(v.AsSpan(4).ToArray());
}

/// <summary>2FA keys and recovery codes are stored encrypted ("enc1:" + Data Protection) instead of readable.</summary>
public sealed class PaCoUserStore(PaCoDb context, IDataProtectionProvider dp, IdentityErrorDescriber? describer = null)
    : UserStore<ParentUser, IdentityRole<Guid>, PaCoDb, Guid>(context, describer)
{
    public const string Prefix = "enc1:";
    public static readonly string[] SecretTokens = ["AuthenticatorKey", "RecoveryCodes"];
    private readonly IDataProtector _protector = dp.CreateProtector("PaCo.UserTokens.v1");

    public override async Task<string?> GetTokenAsync(ParentUser user, string loginProvider, string name, CancellationToken cancellationToken)
    {
        var value = await base.GetTokenAsync(user, loginProvider, name, cancellationToken);
        return value is not null && SecretTokens.Contains(name) && value.StartsWith(Prefix, StringComparison.Ordinal)
            ? _protector.Unprotect(value[Prefix.Length..])
            : value;
    }

    public override Task SetTokenAsync(ParentUser user, string loginProvider, string name, string? value, CancellationToken cancellationToken) =>
        base.SetTokenAsync(user, loginProvider, name,
            value is not null && SecretTokens.Contains(name) ? Prefix + _protector.Protect(value) : value, cancellationToken);
}

/// <summary>Startup: encrypts secrets that are still stored readable (older versions) and creates an encrypted key.</summary>
public static class SecretMigration
{
    public static async Task RunAsync(IServiceProvider services, ServerOptions opts, ILogger log)
    {
        var dp = services.GetRequiredService<IDataProtectionProvider>();
        SecretBox.Protector = dp.CreateProtector("PaCo.DbSecrets.v1");

        // With a master key: if the key ring holds only unencrypted keys, add an encrypted one and use it from now on.
        if (MasterKey.Parse(opts.MasterKey) is not null)
        {
            var dir = Path.Combine(opts.DataDir, "dp-keys");
            var files = Directory.Exists(dir) ? Directory.GetFiles(dir, "key-*.xml") : [];
            if (!files.Any(f => File.ReadAllText(f).Contains("pacoEncryptedKey", StringComparison.Ordinal)))
            {
                services.GetRequiredService<IKeyManager>().CreateNewKey(DateTimeOffset.UtcNow, DateTimeOffset.UtcNow.AddDays(90));
                log.LogWarning("Neuer, mit dem Master-Schlüssel verschlüsselter Datenschutz-Schlüssel erstellt.");
            }
        }
        else
        {
            log.LogWarning("PaCo__MasterKey ist nicht gesetzt – die Schlüssel im Datenordner sind nicht zusätzlich verschlüsselt. Siehe docs/server-setup.md.");
        }

        using var scope = services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<PaCoDb>();
        var tokenProtector = dp.CreateProtector("PaCo.UserTokens.v1");
        var plainTokens = await db.UserTokens.Where(t => PaCoUserStore.SecretTokens.Contains(t.Name) && t.Value != null && !t.Value.StartsWith(PaCoUserStore.Prefix)).ToListAsync();
        foreach (var t in plainTokens) t.Value = PaCoUserStore.Prefix + tokenProtector.Protect(t.Value!);

        var families = await db.Families.ToListAsync();
        var conn = db.Database.GetDbConnection();
        var wrapped = new HashSet<Guid>();
        await conn.OpenAsync();
        await using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = "SELECT Id, UnlockKey FROM Families";
            await using var r = await cmd.ExecuteReaderAsync();
            while (await r.ReadAsync())
                if (SecretBox.IsWrapped((byte[])r[1])) wrapped.Add(Guid.Parse(r.GetString(0)));
        }
        foreach (var f in families.Where(f => !wrapped.Contains(f.Id))) db.Entry(f).Property(x => x.UnlockKey).IsModified = true;
        var count = plainTokens.Count + families.Count(f => !wrapped.Contains(f.Id));
        if (count > 0)
        {
            await db.SaveChangesAsync();
            log.LogInformation("{Count} Geheimnisse in der Datenbank verschlüsselt", count);
        }
    }
}
