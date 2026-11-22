#:package System.Security.Cryptography.ProtectedData@10.0.15
#:property TargetFramework=net10.0-windows
// Offline release signing (Windows, maintainer's PC). Usage from the repository root:
//   dotnet run tools/release/sign.cs -- init                     create the key pair (once); writes the public key
//                                                                 to src/PaCo.Server/release-signing.pub
//   dotnet run tools/release/sign.cs -- sign <exe> <version>     writes <exe>.sig
//   dotnet run tools/release/sign.cs -- release <version>        downloads PaCo-Setup.exe of GitHub release v<version>,
//                                                                 signs it and uploads PaCo-Setup.exe.sig (needs gh)
// The private key is stored DPAPI-encrypted for the current Windows user in %APPDATA%\PaCo-Release\signing-key.dat
// and never leaves this PC. Keep a copy (e.g. "export") in a password manager.
using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;

var keyFile = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "PaCo-Release", "signing-key.dat");
var pubFile = Path.Combine("src", "PaCo.Server", "release-signing.pub");
var entropy = Encoding.UTF8.GetBytes("PaCo release signing key");

ECDsa LoadKey()
{
    if (!File.Exists(keyFile)) throw new InvalidOperationException($"Kein Signaturschlüssel ({keyFile}) – zuerst 'init'.");
    var pem = Encoding.UTF8.GetString(ProtectedData.Unprotect(File.ReadAllBytes(keyFile), entropy, DataProtectionScope.CurrentUser));
    var k = ECDsa.Create();
    k.ImportFromPem(pem);
    return k;
}

string Sha256(string path)
{
    using var fs = File.OpenRead(path);
    return Convert.ToHexStringLower(SHA256.HashData(fs));
}

void Run(string exe, string args)
{
    using var p = Process.Start(new ProcessStartInfo(exe, args) { UseShellExecute = false })!;
    p.WaitForExit();
    if (p.ExitCode != 0) throw new InvalidOperationException($"{exe} {args} → Exitcode {p.ExitCode}");
}

switch (args.FirstOrDefault())
{
    case "init":
        if (File.Exists(keyFile)) { Console.WriteLine($"Schlüssel existiert bereits: {keyFile}"); break; }
        using (var key = ECDsa.Create(ECCurve.NamedCurves.nistP256))
        {
            Directory.CreateDirectory(Path.GetDirectoryName(keyFile)!);
            File.WriteAllBytes(keyFile, ProtectedData.Protect(Encoding.UTF8.GetBytes(key.ExportPkcs8PrivateKeyPem()), entropy, DataProtectionScope.CurrentUser));
            File.WriteAllText(pubFile, key.ExportSubjectPublicKeyInfoPem() + "\n");
            Console.WriteLine($"Privater Schlüssel: {keyFile} (nur für diesen Windows-Benutzer lesbar)");
            Console.WriteLine($"Öffentlicher Schlüssel: {pubFile} – committen.");
        }
        break;

    case "export":
        // For the password manager: prints the private key (only when explicitly asked).
        using (var key = LoadKey()) Console.WriteLine(key.ExportPkcs8PrivateKeyPem());
        break;

    case "sign" when args.Length == 3:
        using (var key = LoadKey())
        {
            var sig = PaCo.ReleaseSig.Sign(key, args[2], Sha256(args[1]));
            File.WriteAllText(args[1] + ".sig", sig + "\n");
            Console.WriteLine($"Signiert: {args[1]}.sig");
        }
        break;

    case "release" when args.Length == 2:
        {
            var version = args[1].TrimStart('v');
            var dir = Path.Combine(Path.GetTempPath(), "paco-release-" + version);
            Directory.CreateDirectory(dir);
            Run("gh", $"release download v{version} --pattern PaCo-Setup.exe --dir \"{dir}\" --clobber");
            var exe = Path.Combine(dir, "PaCo-Setup.exe");
            using (var key = LoadKey()) File.WriteAllText(exe + ".sig", PaCo.ReleaseSig.Sign(key, version, Sha256(exe)) + "\n");
            Run("gh", $"release upload v{version} \"{exe}.sig\" --clobber");
            Console.WriteLine($"Release v{version} signiert und PaCo-Setup.exe.sig hochgeladen.");
            break;
        }

    default:
        Console.WriteLine("init | export | sign <exe> <version> | release <version>");
        return 1;
}
return 0;

namespace PaCo
{
    /// <summary>Same message format as PaCo.Shared.ReleaseSignature (kept in sync by the server test).</summary>
    internal static class ReleaseSig
    {
        public static string Sign(ECDsa key, string version, string sha256Hex) =>
            Convert.ToBase64String(key.SignData(Encoding.UTF8.GetBytes($"paco-release|{version}|{sha256Hex.ToLowerInvariant()}"),
                HashAlgorithmName.SHA256, DSASignatureFormat.IeeeP1363FixedFieldConcatenation));
    }
}
