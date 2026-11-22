using System.Diagnostics;
using PaCo.Client.Agent;
using PaCo.Client.Config;
using PaCo.Client.Native;
using PaCo.Shared;

namespace PaCo.Client.Service;

/// <summary>
/// Learning mode (time used up, parents allow learning): every program with a window that is not a learning app is
/// closed, and so are music/audio-book players running hidden in the tray. Only processes that belong to the child's
/// account are touched – never Windows' own processes in the session (csrss, dwm, winlogon … run as SYSTEM; killing
/// them would crash Windows).
/// </summary>
internal static class LearningEnforcer
{
    /// <summary>Windows shell and helpers the child needs for a usable desktop (and accessibility tools).</summary>
    private static readonly HashSet<string> Shell = new(StringComparer.OrdinalIgnoreCase)
    {
        "explorer", "sihost", "taskhostw", "ctfmon", "runtimebroker", "dllhost", "conhost", "applicationframehost",
        "shellexperiencehost", "startmenuexperiencehost", "shellhost", "searchapp", "searchhost", "searchui", "textinputhost",
        "windowsinternal.composableshell.experiences.textinput.inputapp", "lockapp", "useroobebroker", "smartscreen",
        "securityhealthsystray", "systemsettingsbroker", "osk", "tabtip", "magnify", "narrator", "msedgewebview2",
    };

    /// <summary>Players that keep playing music, audio books or voice chat without a visible window (tray).</summary>
    private static readonly HashSet<string> AudioApps = new(StringComparer.OrdinalIgnoreCase)
    {
        "spotify", "vlc", "wmplayer", "music.ui", "microsoft.media.player", "itunes", "applemusic", "audible", "foobar2000",
        "winamp", "aimp", "musicbee", "deezer", "amazon music", "amazonmusic", "tidal", "audacious", "mpc-hc", "mpc-hc64",
        "potplayer", "potplayermini", "potplayermini64", "kodi", "plex", "discord", "teamspeak", "ts3client_win64", "tonies",
        "soundcloud", "youtube music desktop app", "qobuz", "napster",
    };

    /// <summary>Closes all reported window programs that are not allowed; returns their display names.</summary>
    /// <param name="firstRound">First check after learning mode started: browsers are closed too, so no old tab keeps playing.</param>
    public static List<string> Close(int sessionId, string childSid, IEnumerable<WindowProcess> windows, ChildPolicy child, bool browsersAllowed, bool firstRound)
    {
        var closed = new List<string>();
        foreach (var w in windows)
        {
            if (string.IsNullOrEmpty(w.Process) || Shell.Contains(w.Process)) continue;
            var browser = BrowserAddress.IsBrowser(w.Process);
            var allowed = TimeGroups.IsLearningApp(child, w.Process, w.Description) || (browser && browsersAllowed && !firstRound);
            if (allowed) continue;
            if (Kill(w.Pid, w.Process, sessionId, childSid)) closed.Add(string.IsNullOrWhiteSpace(w.Description) ? w.Process : w.Description!);
        }
        return closed.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
    }

    /// <summary>
    /// Programs without a window: hidden music/audio-book players (unless the parents made them a learning app) and – in
    /// the first round – every browser process (background tabs, "keep running in the background").
    /// </summary>
    public static List<string> CloseBackground(int sessionId, string childSid, ChildPolicy child, bool firstRound)
    {
        var closed = new List<string>();
        foreach (var p in Process.GetProcesses())
        {
            using (p)
            {
                string name;
                try
                {
                    if (p.SessionId != sessionId) continue;
                    name = p.ProcessName.ToLowerInvariant();
                }
                catch (InvalidOperationException) { continue; }
                var target = (firstRound && BrowserAddress.IsBrowser(name)) || (AudioApps.Contains(name) && !TimeGroups.IsLearningApp(child, name, Description(p.Id)));
                if (target && Kill(p.Id, name, sessionId, childSid)) closed.Add(name);
            }
        }
        return closed.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
    }

    /// <summary>Ends all browser processes of the child (a site's time is used up: tabs in the background stop too).</summary>
    public static List<string> KillBrowsers(int sessionId, string childSid)
    {
        var closed = new List<string>();
        foreach (var p in Process.GetProcesses())
        {
            using (p)
            {
                string name;
                try
                {
                    if (p.SessionId != sessionId) continue;
                    name = p.ProcessName.ToLowerInvariant();
                }
                catch (InvalidOperationException) { continue; }
                if (BrowserAddress.IsBrowser(name) && Kill(p.Id, name, sessionId, childSid)) closed.Add(name);
            }
        }
        return closed.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
    }

    private static string? Description(int pid)
    {
        var path = ForegroundTracker.ImagePath(pid);
        if (path is null) return null;
        try { return FileVersionInfo.GetVersionInfo(path).FileDescription?.Trim(); }
        catch (Exception) { return null; }
    }

    private static bool Kill(int pid, string process, int sessionId, string childSid)
    {
        try
        {
            using var p = Process.GetProcessById(pid);
            // The pid must still be the same program in the child's session, owned by the child.
            if (p.SessionId != sessionId || !string.Equals(p.ProcessName, process, StringComparison.OrdinalIgnoreCase)) return false;
            if (!string.Equals(OwnerSid(pid), childSid, StringComparison.OrdinalIgnoreCase)) return false;
            p.Kill(entireProcessTree: true);
            return true;
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            return false; // exited meanwhile or protected
        }
    }

    private static string? OwnerSid(int pid)
    {
        var h = Win32.OpenProcess(Win32.ProcessQueryLimitedInformation, false, pid);
        if (h == IntPtr.Zero) return null;
        try
        {
            if (!Win32.OpenProcessToken(h, 0x0008 /* TOKEN_QUERY */, out var token)) return null;
            using (token) return Sessions.SidOfToken(token);
        }
        catch (Exception ex)
        {
            Log.Warn($"Besitzer von Prozess {pid}: {ex.Message}");
            return null;
        }
        finally
        {
            Win32.CloseHandle(h);
        }
    }
}
