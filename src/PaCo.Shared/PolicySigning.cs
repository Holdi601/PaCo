using System.Security.Cryptography;

namespace PaCo.Shared;

/// <summary>A JSON payload together with an ECDSA P-256 signature over its exact bytes.</summary>
public sealed record SignedEnvelope
{
    public string Payload { get; init; } = "";

    public string Signature { get; init; } = "";
}

public sealed class PolicySignatureException(string message) : Exception(message);

/// <summary>
/// Signs policies on the server and verifies them on the client. The client pins the server's public key at
/// pairing time, so neither a tampered cache file nor a man in the middle can inject rules.
/// </summary>
public static class PolicySigning
{
    public static SignedEnvelope Sign<T>(T document, ECDsa privateKey)
    {
        var bytes = PaCoJson.SerializeToUtf8(document);
        var sig = privateKey.SignData(bytes, HashAlgorithmName.SHA256, DSASignatureFormat.IeeeP1363FixedFieldConcatenation);
        return new SignedEnvelope { Payload = Convert.ToBase64String(bytes), Signature = Convert.ToBase64String(sig) };
    }

    public static T Verify<T>(SignedEnvelope envelope, string publicKeyPem)
    {
        byte[] bytes, sig;
        try
        {
            bytes = Convert.FromBase64String(envelope.Payload);
            sig = Convert.FromBase64String(envelope.Signature);
        }
        catch (FormatException)
        {
            throw new PolicySignatureException("Signierte Daten sind beschädigt.");
        }

        using var key = ECDsa.Create();
        key.ImportFromPem(publicKeyPem);
        if (!key.VerifyData(bytes, sig, HashAlgorithmName.SHA256, DSASignatureFormat.IeeeP1363FixedFieldConcatenation))
            throw new PolicySignatureException("Signatur ungültig – Regeln wurden manipuliert oder stammen nicht vom Server.");

        return PaCoJson.Deserialize<T>(bytes);
    }

    public static ECDsa CreateKey() => ECDsa.Create(ECCurve.NamedCurves.nistP256);

    public static string ExportPublicPem(ECDsa key) => key.ExportSubjectPublicKeyInfoPem();
}
