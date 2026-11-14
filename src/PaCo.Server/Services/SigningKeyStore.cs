using System.Security.Cryptography;
using Microsoft.Extensions.Options;
using PaCo.Shared;

namespace PaCo.Server.Services;

/// <summary>Holds the server's ECDSA policy signing key. Created once, then never changes (clients pin it).</summary>
public sealed class SigningKeyStore : IDisposable
{
    private readonly ECDsa _key;

    public SigningKeyStore(IOptions<ServerOptions> options, ILogger<SigningKeyStore> log)
    {
        var path = Path.Combine(options.Value.DataDir, "policy-signing-key.pem");
        _key = ECDsa.Create();
        if (File.Exists(path))
        {
            _key.ImportFromPem(File.ReadAllText(path));
        }
        else
        {
            using var created = PolicySigning.CreateKey();
            var pem = created.ExportPkcs8PrivateKeyPem();
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
            File.WriteAllText(path, pem);
            if (!OperatingSystem.IsWindows())
                File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite);
            _key.ImportFromPem(pem);
            log.LogWarning("Neuer Signaturschlüssel erzeugt: {Path}. Diese Datei sichern – ohne sie müssen alle Geräte neu gekoppelt werden.", path);
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
