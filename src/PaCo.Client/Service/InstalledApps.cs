using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.ComTypes;
using System.Text;
using Microsoft.Win32;
using PaCo.Client.Config;
using PaCo.Shared;

namespace PaCo.Client.Service;

/// <summary>
/// Programs installed on this PC, read from the Start menu shortcuts (all users and the children's accounts). The
/// parents pick learning apps and aids from this list on the website – PaCo does not have to guess which programs are
/// "legitimate help". The program description lets learning time recognise the real program (a game renamed to the
/// same file name keeps its own description).
/// </summary>
internal static class InstalledApps
{
    private static readonly string[] Skip = ["uninstall", "deinstall", "unins0", "setup", "update", "readme", "help", "hilfe", "website", "paco"];

    /// <summary>
    /// From the Windows folder only tools that can be a learning aid; the rest there are admin tools (command prompt,
    /// disk cleanup, …) that would only clutter the parents' pick list.
    /// </summary>
    private static readonly HashSet<string> WindowsAids = new(StringComparer.OrdinalIgnoreCase)
        { "notepad", "mspaint", "charmap", "osk", "magnify", "narrator", "snippingtool", "calc", "wordpad", "write", "xpsrchvw", "soundrecorder", "stikynot" };

    public static List<InstalledApp> Scan(IEnumerable<string> childSids)
    {
        var dirs = new List<string>
        {
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), @"Microsoft\Windows\Start Menu\Programs"),
        };
        foreach (var sid in childSids.Distinct())
            if (ProfileDir(sid) is { } profile) dirs.Add(Path.Combine(profile, @"AppData\Roaming\Microsoft\Windows\Start Menu\Programs"));

        var result = new Dictionary<string, InstalledApp>(StringComparer.OrdinalIgnoreCase);
        foreach (var dir in dirs.Where(Directory.Exists))
        {
            IEnumerable<string> links;
            try { links = Directory.EnumerateFiles(dir, "*.lnk", SearchOption.AllDirectories).Take(2000).ToList(); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { continue; }
            foreach (var link in links)
            {
                var name = Path.GetFileNameWithoutExtension(link);
                if (Skip.Any(s => name.Contains(s, StringComparison.OrdinalIgnoreCase))) continue;
                var target = Target(link);
                if (target is null || !target.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) || !File.Exists(target)) continue;
                var process = Path.GetFileNameWithoutExtension(target).ToLowerInvariant();
                if (target.StartsWith(Environment.GetFolderPath(Environment.SpecialFolder.Windows), StringComparison.OrdinalIgnoreCase) && !WindowsAids.Contains(process)) continue;
                if (Skip.Any(s => process.Contains(s, StringComparison.OrdinalIgnoreCase)) || result.ContainsKey(process)) continue;
                string? description = null;
                try
                {
                    var vi = FileVersionInfo.GetVersionInfo(target);
                    description = !string.IsNullOrWhiteSpace(vi.FileDescription) ? vi.FileDescription.Trim()
                        : !string.IsNullOrWhiteSpace(vi.ProductName) ? vi.ProductName.Trim() : null;
                }
                catch (Exception) { /* no version info */ }
                result[process] = new InstalledApp { Process = Cut(process, 80), Name = Cut(name, 120), Description = description is null ? null : Cut(description, 120) };
                if (result.Count >= 300) break;
            }
        }
        return result.Values.OrderBy(a => a.Name, StringComparer.CurrentCultureIgnoreCase).ToList();
    }

    private static string Cut(string s, int max) => s.Length <= max ? s : s[..max];

    private static string? ProfileDir(string sid)
    {
        try
        {
            using var key = Registry.LocalMachine.OpenSubKey($@"SOFTWARE\Microsoft\Windows NT\CurrentVersion\ProfileList\{sid}");
            return key?.GetValue("ProfileImagePath") is string p ? Environment.ExpandEnvironmentVariables(p) : null;
        }
        catch (Exception) { return null; }
    }

    /// <summary>Target of a shortcut (.lnk) via the Shell's ShellLink object.</summary>
    private static string? Target(string link)
    {
        object? obj = null;
        try
        {
            obj = Activator.CreateInstance(Type.GetTypeFromCLSID(new Guid("00021401-0000-0000-C000-000000000046"))!);
            ((IPersistFile)obj!).Load(link, 0);
            var sb = new StringBuilder(1024);
            ((IShellLinkW)obj).GetPath(sb, sb.Capacity, IntPtr.Zero, 0);
            var path = Environment.ExpandEnvironmentVariables(sb.ToString());
            return path.Length == 0 ? null : path;
        }
        catch (Exception ex)
        {
            Log.Warn($"Verknüpfung {Path.GetFileName(link)}: {ex.Message}");
            return null;
        }
        finally
        {
            if (obj is not null) Marshal.ReleaseComObject(obj);
        }
    }

    [ComImport, Guid("000214F9-0000-0000-C000-000000000046"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IShellLinkW
    {
        [PreserveSig] int GetPath([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder file, int max, IntPtr findData, uint flags);
        // Further IShellLinkW methods are not used (and therefore not declared – only the vtable prefix matters).
    }
}
