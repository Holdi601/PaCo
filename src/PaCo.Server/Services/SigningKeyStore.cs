using Microsoft.AspNetCore.DataProtection;
using System.Security.Cryptography;
using Microsoft.Extensions.Options;
using PaCo.Shared;

namespace PaCo.Server.Services;

/// <summary>Holds the server's ECDSA policy signing key. Created once, then never changes (clients pin it).</summary>
public sealed class SigningKeyStore : IDisposable
{
    private readonly ECDsa _key;

    public SigningKeyStore(IOptions<ServerOptions> options, ILogger<SigningKeyStore> log, Microsoft.AspNetCore.DataProtection.IDataProtectionProvider dp)
    {
        // The private key is stored encrypted (policy-signing-key.enc); an older readable .pem is converted once.
        var protector = dp.CreateProtector("PaCo.SigningKey.v1");
        var plainPath = Path.Combine(options.Value.DataDir, "policy-signing-key.pem");
        var encPath = Path.Combine(options.Value.DataDir, "policy-signing-key.enc");
        _key = ECDsa.Create();
        string pem;
        if (File.Exists(encPath))
        {
            pem = protector.Unprotect(File.ReadAllText(encPath));
        }
        else if (File.Exists(plainPath))
        {
            pem = File.ReadAllText(plainPath);
        }
        else
        {
            using var created = PolicySigning.CreateKey();
            pem = created.ExportPkcs8PrivateKeyPem();
            log.LogWarning("Neuer Signaturschlüssel erzeugt. Datenordner sichern – ohne ihn müssen alle Geräte neu gekoppelt werden.");
        }
        _key.ImportFromPem(pem);
        if (!File.Exists(encPath))
        {
            Directory.CreateDirectory(options.Value.DataDir);
            File.WriteAllText(encPath, protector.Protect(pem));
            if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(encPath, UnixFileMode.UserRead | UnixFileMode.UserWrite);
            if (File.Exists(plainPath)) File.Delete(plainPath);
        }
        PublicKeyPem = PolicySigning.ExportPublicPem(_key);
    }

    public string PublicKeyPem { get; }

    private readonly Lock _gate = new();

    public SignedEnvelope Sign<T>(T document)
    {
        lock (_gate) return PolicySigning.Sign(document, _key);
    }

    public void Dispose() => _key.Dispose();
}
