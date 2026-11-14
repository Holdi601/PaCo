using System.Diagnostics;
using PaCo.Client.Agent;
using PaCo.Client.Config;

namespace PaCo.Client.Service;

/// <summary>Kills blocked programs in a child's session. Matches process name and the exe's original file name
/// (so renaming "Discord.exe" to "x.exe" does not help).</summary>
internal static class AppBlocker
{
    private static readonly Dictionary<string, string?> OriginalNames = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Returns display names of killed programs.</summary>
    public static List<string> Enforce(int sessionId, IReadOnlyList<string> blocked)
    {
        var killed = new List<string>();
        if (blocked.Count == 0) return killed;
        var set = new HashSet<string>(blocked, StringComparer.OrdinalIgnoreCase);

        foreach (var p in Process.GetProcesses())
        {
            using (p)
            {
                try
                {
                    if (p.SessionId != sessionId) continue;
                    var name = p.ProcessName;
                    var hit = set.Contains(name);
                    if (!hit)
                    {
                        var orig = OriginalName(p.Id);
                        hit = orig is not null && set.Contains(orig);
                    }
                    if (!hit) continue;
                    p.Kill(entireProcessTree: true);
                    killed.Add(name);
                }
                catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception)
                {
                    // exited meanwhile or access denied (protected process)
                }
            }
        }
        if (OriginalNames.Count > 2000) OriginalNames.Clear();
        return killed.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
    }

    private static string? OriginalName(int pid)
    {
        var path = ForegroundTracker.ImagePath(pid);
        if (path is null) return null;
        if (OriginalNames.TryGetValue(path, out var cached)) return cached;
        string? name = null;
        try
        {
            var vi = FileVersionInfo.GetVersionInfo(path);
            if (!string.IsNullOrWhiteSpace(vi.OriginalFilename)) name = Path.GetFileNameWithoutExtension(vi.OriginalFilename.Trim());
        }
        catch (Exception ex)
        {
            Log.Warn($"Versionsinfo {path}: {ex.Message}");
        }
        return OriginalNames[path] = name?.ToLowerInvariant();
    }
}
