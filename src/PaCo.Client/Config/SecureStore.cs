using System.Security.AccessControl;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text;
using PaCo.Shared;

namespace PaCo.Client.Config;

internal static class Paths
{
    public const string ServiceName = "PaCo";
    public const string DisplayName = "PaCo Kinderschutz";

    public static string InstallDir => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "PaCo");

    public static string InstalledExe => Path.Combine(InstallDir, "PaCo.exe");

    /// <summary>C:\ProgramData\PaCo – ACL: SYSTEM + Administrators only. Children cannot read or write here.</summary>
    public static string DataDir => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "PaCo");

    public static string DeviceFile => Path.Combine(DataDir, "device.bin");

    public static string PolicyFile => Path.Combine(DataDir, "policy.json");

    public static string StateFile => Path.Combine(DataDir, "state.json");

    public static string LogFile => Path.Combine(DataDir, "paco.log");
}

/// <summary>Restricted directory, atomic writes and machine-bound encryption.</summary>
internal static class SecureStore
{
    private static readonly SecurityIdentifier System = new(WellKnownSidType.LocalSystemSid, null);
    private static readonly SecurityIdentifier Admins = new(WellKnownSidType.BuiltinAdministratorsSid, null);

    /// <summary>Creates the data dir with an explicit, non-inherited ACL: SYSTEM and Administrators full control, nobody else.</summary>
    public static void EnsureDataDir()
    {
        var dir = new DirectoryInfo(Paths.DataDir);
        if (!dir.Exists) dir.Create();
        var sec = new DirectorySecurity();
        sec.SetOwner(Admins);
        sec.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
        foreach (var sid in new[] { System, Admins })
            sec.AddAccessRule(new FileSystemAccessRule(sid, FileSystemRights.FullControl,
                InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit, PropagationFlags.None, AccessControlType.Allow));
        dir.SetAccessControl(sec);
    }

    /// <summary>Write temp file, flush, then atomically replace – a crash never leaves a half-written file.</summary>
    public static void WriteAtomic(string path, byte[] data)
    {
        var tmp = path + ".tmp";
        using (var fs = new FileStream(tmp, FileMode.Create, FileAccess.Write, FileShare.None, 4096, FileOptions.WriteThrough))
        {
            fs.Write(data);
            fs.Flush(true);
        }
        if (File.Exists(path)) File.Replace(tmp, path, path + ".bak", ignoreMetadataErrors: true);
        else File.Move(tmp, path);
    }

    public static byte[]? ReadWithBackup(string path)
    {
        foreach (var p in new[] { path, path + ".bak" })
        {
            try
            {
                if (File.Exists(p)) return File.ReadAllBytes(p);
            }
            catch (IOException) { }
        }
        return null;
    }

    /// <summary>DPAPI LocalMachine: the blob is useless on any other PC (e.g. if copied off via USB).</summary>
    public static byte[] Protect(byte[] data) =>
        ProtectedData.Protect(data, Encoding.UTF8.GetBytes("PaCo-device-v1"), DataProtectionScope.LocalMachine);

    public static byte[] Unprotect(byte[] data) =>
        ProtectedData.Unprotect(data, Encoding.UTF8.GetBytes("PaCo-device-v1"), DataProtectionScope.LocalMachine);
}

/// <summary>Pairing result – the device's identity. Stored encrypted.</summary>
internal sealed record DeviceSecrets
{
    public string ServerUrl { get; init; } = "";

    public Guid DeviceId { get; init; }

    public string DeviceToken { get; init; } = "";

    public string PolicyPublicKeyPem { get; init; } = "";

    public string UnlockKey { get; init; } = "";

    public string FamilyName { get; init; } = "";

    public byte[] UnlockKeyBytes => Convert.FromBase64String(UnlockKey);

    public static DeviceSecrets? Load()
    {
        var raw = SecureStore.ReadWithBackup(Paths.DeviceFile);
        if (raw is null) return null;
        try
        {
            return PaCoJson.Deserialize<DeviceSecrets>(SecureStore.Unprotect(raw));
        }
        catch (Exception ex) when (ex is CryptographicException or System.Text.Json.JsonException)
        {
            Log.Error("device.bin kann nicht gelesen werden", ex);
            return null;
        }
    }

    public void Save() => SecureStore.WriteAtomic(Paths.DeviceFile, SecureStore.Protect(PaCoJson.SerializeToUtf8(this)));
}

/// <summary>Minimal file log in the protected data dir (rotates at 2 MB).</summary>
internal static class Log
{
    private static readonly Lock Gate = new();

    public static void Info(string message) => Write("INFO", message);

    public static void Warn(string message) => Write("WARN", message);

    public static void Error(string message, Exception? ex = null) => Write("ERROR", ex is null ? message : $"{message}: {ex}");

    private static void Write(string level, string message)
    {
        try
        {
            lock (Gate)
            {
                if (!Directory.Exists(Paths.DataDir)) return;
                var fi = new FileInfo(Paths.LogFile);
                if (fi.Exists && fi.Length > 2 * 1024 * 1024)
                {
                    File.Copy(fi.FullName, fi.FullName + ".1", overwrite: true);
                    fi.Delete();
                }
                File.AppendAllText(Paths.LogFile, $"{DateTime.Now:yyyy-MM-dd HH:mm:ss} [{level}] [{Environment.ProcessId}] {message}{Environment.NewLine}");
            }
        }
        catch
        {
            // Logging must never take enforcement down.
        }
    }
}
