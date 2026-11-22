using System.Security.Cryptography;
using System.Text;

namespace PaCo.Shared;

/// <summary>
/// Releases are signed with an offline key that only exists on the maintainer's PC (tools/release/sign.cs). The server
/// accepts a client version from GitHub only with a valid signature – a hacked GitHub account alone cannot push
/// software onto the children's PCs.
/// </summary>
public static class ReleaseSignature
{
    public static byte[] Message(string version, string sha256Hex) =>
        Encoding.UTF8.GetBytes($"paco-release|{version}|{sha256Hex.ToLowerInvariant()}");

    public static string Sign(ECDsa key, string version, string sha256Hex) =>
        Convert.ToBase64String(key.SignData(Message(version, sha256Hex), HashAlgorithmName.SHA256, DSASignatureFormat.IeeeP1363FixedFieldConcatenation));

    public static bool Verify(string publicKeyPem, string version, string sha256Hex, string signatureBase64)
    {
        try
        {
            using var key = ECDsa.Create();
            key.ImportFromPem(publicKeyPem);
            return key.VerifyData(Message(version, sha256Hex), Convert.FromBase64String(signatureBase64.Trim()),
                HashAlgorithmName.SHA256, DSASignatureFormat.IeeeP1363FixedFieldConcatenation);
        }
        catch (Exception ex) when (ex is FormatException or CryptographicException or ArgumentException)
        {
            return false;
        }
    }
}
