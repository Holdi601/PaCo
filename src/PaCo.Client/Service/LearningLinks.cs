using System.Runtime.InteropServices;
using System.Text;
using PaCo.Client.Config;
using PaCo.Client.Native;
using PaCo.Shared;

namespace PaCo.Client.Service;

/// <summary>
/// Folder "Lernen" on the child's desktop with one internet shortcut per page/video the parents allowed for learning
/// (e.g. exactly one YouTube video). Without it the child could not find these pages: the rest of the site is blocked
/// in learning mode. Only shortcuts with PaCo's marker are ever changed or deleted – never the child's own files.
/// </summary>
internal static class LearningLinks
{
    private const string Marker = "; PaCo";
    private static readonly Dictionary<int, string> Applied = [];
    private static readonly Guid DesktopFolder = new("B4BFCC3A-DB2C-424C-B029-7FE99A87C641");

    [DllImport("shell32.dll")]
    private static extern int SHGetKnownFolderPath(ref Guid id, uint flags, IntPtr token, out IntPtr path);

    public static void Sync(int sessionId, ChildPolicy child)
    {
        try
        {
            var links = TimeGroups.LearningGroups(child)
                .SelectMany(g => g.Pages.Select(p => (Page: p, Title: g.PageTitles.GetValueOrDefault(p))))
                .DistinctBy(x => x.Page, StringComparer.OrdinalIgnoreCase)
                .Take(100)
                .ToList();
            var key = string.Join("\n", links.Select(l => $"{l.Page}|{l.Title}"));
            if (Applied.TryGetValue(sessionId, out var done) && done == key) return;

            var desktop = DesktopOf(sessionId);
            if (desktop is null) return;
            var dir = Path.Combine(desktop, L.In(child.Language, "Lernen"));
            var wanted = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var (page, title) in links)
            {
                var name = FileName(string.IsNullOrWhiteSpace(title) ? page : title!);
                var unique = name;
                for (var i = 2; wanted.ContainsKey(unique + ".url"); i++) unique = $"{name} ({i})";
                wanted[unique + ".url"] = $"[InternetShortcut]\r\nURL={TimeGroups.PageUrl(page)}\r\n{Marker}\r\n";
            }

            if (wanted.Count > 0) Directory.CreateDirectory(dir);
            if (Directory.Exists(dir))
            {
                foreach (var file in Directory.GetFiles(dir, "*.url"))
                {
                    if (wanted.ContainsKey(Path.GetFileName(file))) continue;
                    if (IsOurs(file)) File.Delete(file);
                }
                foreach (var (file, content) in wanted)
                {
                    var path = Path.Combine(dir, file);
                    if (!File.Exists(path) || File.ReadAllText(path) != content) File.WriteAllText(path, content, Encoding.UTF8);
                }
                if (wanted.Count == 0 && Directory.GetFileSystemEntries(dir).Length == 0) Directory.Delete(dir);
            }
            Applied[sessionId] = key;
        }
        catch (Exception ex)
        {
            Log.Warn($"Lern-Links: {ex.Message}");
        }
    }

    private static bool IsOurs(string file)
    {
        try { return new FileInfo(file).Length < 4096 && File.ReadAllText(file).Contains(Marker, StringComparison.Ordinal); }
        catch (IOException) { return false; }
    }

    private static string FileName(string s)
    {
        var bad = Path.GetInvalidFileNameChars();
        var clean = new string(s.Select(c => bad.Contains(c) ? '-' : c).ToArray()).Trim(' ', '.');
        return clean.Length == 0 ? "Link" : clean.Length > 80 ? clean[..80] : clean;
    }

    private static string? DesktopOf(int sessionId)
    {
        if (!Win32.WTSQueryUserToken(sessionId, out var token)) return null;
        using (token)
        {
            var id = DesktopFolder;
            if (SHGetKnownFolderPath(ref id, 0, token.DangerousGetHandle(), out var p) != 0) return null;
            try { return Marshal.PtrToStringUni(p); }
            finally { Marshal.FreeCoTaskMem(p); }
        }
    }
}
