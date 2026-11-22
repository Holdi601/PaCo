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

    /// <summary>Where the single-file bundle unpacks its native libraries (instead of the world-writable C:\Windows\Temp).</summary>
    public static string BundleDir => Path.Combine(DataDir, "bundle");

    public static string UpdateDir => Path.Combine(DataDir, "update");

    public static string PolicyFile => Path.Combine(DataDir, "policy.json");

    public static string StateFile => Path.Combine(DataDir, "state.json");

    public static string LogFile => Path.Combine(DataDir, "paco.log");
}

/// <summary>Restricted directory, atomic writes and machine-bound encryption.</summary>
internal static class SecureStore
{
    private static readonly SecurityIdentifier System = new(WellKnownSidType.LocalSystemSid, null);
    private static readonly SecurityIdentifier Admins = new(WellKnownSidType.BuiltinAdministratorsSid, null);

    private static DirectorySecurity DataDirSecurity()
    {
        var sec = new DirectorySecurity();
        sec.SetOwner(Admins);
        sec.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
        foreach (var sid in new[] { System, Admins })
            sec.AddAccessRule(new FileSystemAccessRule(sid, FileSystemRights.FullControl,
                InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit, PropagationFlags.None, AccessControlType.Allow));
        return sec;
    }

    /// <summary>
    /// Data dir with an explicit, non-inherited ACL: SYSTEM and Administrators full control, nobody else.
    /// On a fresh installation (not paired yet) an existing folder is removed and created anew in one step with the
    /// ACL – anything a standard user may have placed in C:\ProgramData\PaCo beforehand is gone. In every case the
    /// contents are sanitized (see <see cref="Sanitize"/>). Returns the number of foreign entries that were fixed.
    /// </summary>
    public static int EnsureDataDir()
    {
        Privileges.EnableOwnershipPrivileges();
        var dir = new DirectoryInfo(Paths.DataDir);
        if (dir.Exists && (dir.Attributes.HasFlag(FileAttributes.ReparsePoint) || !File.Exists(Paths.DeviceFile)))
        {
            Log.Warn("Daten-Ordner wird neu angelegt (Erstinstallation oder Verknüpfung)");
            ForceDelete(dir);
            dir.Refresh();
        }
        if (!dir.Exists) dir.Create(DataDirSecurity()); // created with the ACL – no window without protection
        else dir.SetAccessControl(DataDirSecurity());
        return Sanitize(dir);
    }

    /// <summary>
    /// Every entry below the data dir: links (junctions/symlinks) are removed, files and folders get Administrators
    /// as owner and only the inherited ACL – a file the child created or prepared cannot stay writable for them.
    /// </summary>
    private static int Sanitize(DirectoryInfo dir)
    {
        var fixedCount = 0;
        foreach (var e in dir.EnumerateFileSystemInfos("*", new EnumerationOptions { AttributesToSkip = 0, RecurseSubdirectories = false }))
        {
            try
            {
                if (e.Attributes.HasFlag(FileAttributes.ReparsePoint))
                {
                    ForceDelete(e);
                    fixedCount++;
                    continue;
                }
                if (Reset(e)) fixedCount++;
                if (e is DirectoryInfo sub) fixedCount += Sanitize(sub);
            }
            catch (Exception ex)
            {
                Log.Warn($"Daten-Ordner: {e.Name} wird entfernt ({ex.Message})");
                try { ForceDelete(e); fixedCount++; } catch (Exception ex2) { Log.Error($"Daten-Ordner: {e.Name} nicht entfernbar", ex2); }
            }
        }
        return fixedCount;
    }

    /// <summary>Owner = Administrators and no explicit ACEs (inherit only). Returns true if something had to change.</summary>
    private static bool Reset(FileSystemInfo e)
    {
        FileSystemSecurity current = e is DirectoryInfo d ? d.GetAccessControl() : ((FileInfo)e).GetAccessControl();
        var owner = current.GetOwner(typeof(SecurityIdentifier)) as SecurityIdentifier;
        var explicitRules = current.GetAccessRules(includeExplicit: true, includeInherited: false, typeof(SecurityIdentifier)).Count;
        if (owner is not null && (owner == Admins || owner == System) && explicitRules == 0 && !current.AreAccessRulesProtected) return false;

        if (e is DirectoryInfo dir)
        {
            var sec = new DirectorySecurity();
            sec.SetOwner(Admins);
            sec.SetAccessRuleProtection(false, false);
            dir.SetAccessControl(sec);
        }
        else
        {
            var sec = new FileSecurity();
            sec.SetOwner(Admins);
            sec.SetAccessRuleProtection(false, false);
            ((FileInfo)e).SetAccessControl(sec);
        }
        Log.Warn($"Daten-Ordner: Rechte von {e.Name} korrigiert (Besitzer war {owner?.Value ?? "?"}, {explicitRules} eigene Regeln)");
        return true;
    }

    /// <summary>Deletes a file, folder tree or link (a link itself, never its target).</summary>
    private static void ForceDelete(FileSystemInfo e)
    {
        if (e is DirectoryInfo d)
        {
            if (d.Attributes.HasFlag(FileAttributes.ReparsePoint)) { d.Delete(recursive: false); return; }
            foreach (var child in d.EnumerateFileSystemInfos("*", new EnumerationOptions { AttributesToSkip = 0 })) ForceDelete(child);
            d.Attributes = FileAttributes.Normal;
            d.Delete(recursive: false);
        }
        else
        {
            e.Attributes = FileAttributes.Normal;
            e.Delete();
        }
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

    /// <summary>
    /// Reads and parses the file; if it is missing, unreadable or corrupt, the backup (.bak) is tried. Null only if
    /// both fail – a damaged file never silently resets usage or rules if a good backup exists.
    /// </summary>
    public static T? LoadWithBackup<T>(string path, Func<byte[], T> parse) where T : class
    {
        foreach (var p in new[] { path, path + ".bak" })
        {
            try
            {
                if (!File.Exists(p)) continue;
                return parse(File.ReadAllBytes(p));
            }
            catch (Exception ex)
            {
                Log.Warn($"{Path.GetFileName(p)} nicht lesbar: {ex.Message}");
            }
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
        var loaded = SecureStore.LoadWithBackup(Paths.DeviceFile, raw => PaCoJson.Deserialize<DeviceSecrets>(SecureStore.Unprotect(raw)));
        if (loaded is not null || !File.Exists(Paths.DeviceFile)) return loaded;
        var raw = SecureStore.ReadWithBackup(Paths.DeviceFile)!;
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

    public static void Error(string message, Exception? ex = null)
    {
        Write("ERROR", ex is null ? message : $"{message}: {ex}");
        // Short version (no stack trace) to the server so problems are visible without access to the PC.
        try { ErrorSink?.Invoke(ex is null ? message : $"{message}: {ex.GetType().Name}: {ex.Message}"); } catch { /* never throw from logging */ }
    }

    /// <summary>Set by the service: forwards errors to the server (rate-limited there).</summary>
    public static Action<string>? ErrorSink { get; set; }

    /// <summary>Last <paramref name="maxBytes"/> of the log (plus the rotated file if needed) for diagnostics.</summary>
    public static string Tail(int maxBytes = 200_000)
    {
        lock (Gate)
        {
            var sb = new System.Text.StringBuilder();
            foreach (var f in new[] { Paths.LogFile + ".1", Paths.LogFile })
            {
                if (!File.Exists(f)) continue;
                sb.Append(File.ReadAllText(f, System.Text.Encoding.UTF8));
            }
            var s = sb.ToString();
            return s.Length > maxBytes ? s[^maxBytes..] : s;
        }
    }

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
                // UTF-8 with BOM (written only for a new file) so Notepad and Windows PowerShell show umlauts correctly.
                File.AppendAllText(Paths.LogFile, $"{DateTime.Now:yyyy-MM-dd HH:mm:ss} [{level}] [{Environment.ProcessId}] {message}{Environment.NewLine}",
                    new System.Text.UTF8Encoding(encoderShouldEmitUTF8Identifier: true));
            }
        }
        catch
        {
            // Logging must never take enforcement down.
        }
    }
}

/// <summary>Enables SeTakeOwnership/SeRestore/SeBackup in the current (SYSTEM or elevated admin) token.</summary>
internal static class Privileges
{
    [System.Runtime.InteropServices.DllImport("advapi32.dll", SetLastError = true)]
    private static extern bool OpenProcessToken(IntPtr process, uint access, out IntPtr token);

    [System.Runtime.InteropServices.DllImport("advapi32.dll", SetLastError = true, CharSet = System.Runtime.InteropServices.CharSet.Unicode)]
    private static extern bool LookupPrivilegeValue(string? system, string name, out long luid);

    [System.Runtime.InteropServices.DllImport("advapi32.dll", SetLastError = true)]
    private static extern bool AdjustTokenPrivileges(IntPtr token, bool disableAll, ref TokenPrivilege state, int length, IntPtr previous, IntPtr returnLength);

    [System.Runtime.InteropServices.DllImport("kernel32.dll")]
    private static extern bool CloseHandle(IntPtr h);

    [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential, Pack = 4)]
    private struct TokenPrivilege
    {
        public int Count;
        public long Luid;
        public int Attributes;
    }

    public static void EnableOwnershipPrivileges()
    {
        if (!OpenProcessToken(System.Diagnostics.Process.GetCurrentProcess().Handle, 0x0020 | 0x0008, out var token)) return;
        try
        {
            foreach (var name in new[] { "SeTakeOwnershipPrivilege", "SeRestorePrivilege", "SeBackupPrivilege" })
            {
                if (!LookupPrivilegeValue(null, name, out var luid)) continue;
                var tp = new TokenPrivilege { Count = 1, Luid = luid, Attributes = 2 /* SE_PRIVILEGE_ENABLED */ };
                AdjustTokenPrivileges(token, false, ref tp, 0, IntPtr.Zero, IntPtr.Zero);
            }
        }
        finally
        {
            CloseHandle(token);
        }
    }
}
