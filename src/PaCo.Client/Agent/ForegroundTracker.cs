using System.Diagnostics;
using PaCo.Client.Native;

namespace PaCo.Client.Agent;

/// <summary>Samples the foreground app once per second and aggregates seconds per process.</summary>
internal sealed class ForegroundTracker
{
    private sealed record ProcInfo(string Name, string Display, TimeSpan Cached);

    private readonly Dictionary<int, ProcInfo> _cache = [];
    private readonly Dictionary<string, (string Display, int Seconds, string? Title)> _pending = new(StringComparer.OrdinalIgnoreCase);
    private readonly int _ownPid = Environment.ProcessId;

    /// <summary>Foreground window of the last sample (process name, description, window handle, title).</summary>
    public (string Process, string Description, IntPtr Hwnd, string? Title)? Current { get; private set; }

    public void Sample()
    {
        Current = null;
        var hwnd = Win32.GetForegroundWindow();
        if (hwnd == IntPtr.Zero) return;
        Win32.GetWindowThreadProcessId(hwnd, out var pid);
        if (pid == 0 || pid == _ownPid) return;

        var info = Resolve(pid);
        if (info is null) return;
        // UWP apps are hosted by ApplicationFrameHost – look for the real app process in its child windows.
        if (info.Name == "applicationframehost")
        {
            var real = 0;
            Win32.EnumChildWindows(hwnd, (child, _) =>
            {
                Win32.GetWindowThreadProcessId(child, out var cpid);
                if (cpid != pid) { real = cpid; return false; }
                return true;
            }, IntPtr.Zero);
            if (real != 0 && Resolve(real) is { } r) info = r;
        }

        var title = WindowTitle(hwnd);
        Current = (info.Name, info.Display, hwnd, title);
        _pending[info.Name] = _pending.TryGetValue(info.Name, out var cur)
            ? (info.Display, cur.Seconds + 1, title)
            : (info.Display, 1, title);
    }

    public List<ForegroundSample> Drain()
    {
        var list = _pending.Select(kv => new ForegroundSample(kv.Key, kv.Value.Display, kv.Value.Seconds, kv.Value.Title)).ToList();
        _pending.Clear();
        if (_cache.Count > 500) _cache.Clear();
        return list;
    }

    private ProcInfo? Resolve(int pid)
    {
        if (_cache.TryGetValue(pid, out var c) && Mono.Now - c.Cached < TimeSpan.FromMinutes(5)) return c;
        try
        {
            using var p = Process.GetProcessById(pid);
            var name = p.ProcessName.ToLowerInvariant();
            var display = name;
            var path = ImagePath(pid);
            if (path is not null)
            {
                var vi = FileVersionInfo.GetVersionInfo(path);
                display = !string.IsNullOrWhiteSpace(vi.FileDescription) ? vi.FileDescription!.Trim()
                    : !string.IsNullOrWhiteSpace(vi.ProductName) ? vi.ProductName!.Trim() : name;
            }
            return _cache[pid] = new ProcInfo(name, display, Mono.Now);
        }
        catch
        {
            return null;
        }
    }

    public static string? ImagePath(int pid)
    {
        var h = Win32.OpenProcess(Win32.ProcessQueryLimitedInformation, false, pid);
        if (h == IntPtr.Zero) return null;
        try
        {
            var buf = new char[1024];
            var size = buf.Length;
            return Win32.QueryFullProcessImageName(h, 0, buf, ref size) ? new string(buf, 0, size) : null;
        }
        finally
        {
            Win32.CloseHandle(h);
        }
    }

    private static string? WindowTitle(IntPtr hwnd)
    {
        var buf = new char[512];
        var n = Win32.GetWindowText(hwnd, buf, buf.Length);
        return n > 0 ? new string(buf, 0, n) : null;
    }
}
