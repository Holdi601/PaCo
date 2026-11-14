using System.Diagnostics;
using System.Windows.Automation;
using PaCo.Client.Config;
using PaCo.Client.Native;
using PaCo.Shared;

namespace PaCo.Client.Agent;

/// <summary>A keyword hit found in the child's session (only the hit + snippet leave this class).</summary>
internal sealed record AlertHit(AlertCategory Category, string Keyword, string Snippet, string App, string? Title, string Source);

/// <summary>
/// Every few seconds: reads the foreground window title and (optionally) the visible text of the active window via
/// UI Automation, and matches it locally against the alarm keywords. Runs on its own MTA thread with a time budget so
/// a slow application can never block the agent.
/// </summary>
internal sealed class AlertScanner : IDisposable
{
    private static readonly TimeSpan Interval = TimeSpan.FromSeconds(8);
    private static readonly TimeSpan Budget = TimeSpan.FromMilliseconds(1500);
    private const int MaxElements = 800;
    private const int MaxChars = 30_000;

    private readonly Action<List<AlertHit>> _report;
    private readonly Func<bool> _paused;
    private readonly Thread _thread;
    private readonly CancellationTokenSource _cts = new();
    private volatile AlertKeywords.Matcher? _matcher;
    private volatile bool _scanText;
    private string? _lastKey;

    public AlertScanner(Action<List<AlertHit>> report, Func<bool> paused)
    {
        _report = report;
        _paused = paused;
        _thread = new Thread(Run) { IsBackground = true, Name = "AlertScanner" };
        _thread.SetApartmentState(ApartmentState.MTA);
        _thread.Start();
    }

    public void Configure(AlertSettings settings)
    {
        var m = new AlertKeywords.Matcher(settings);
        _matcher = m.IsEmpty ? null : m;
        _scanText = settings.ScanWindowText;
    }

    private void Run()
    {
        while (!_cts.IsCancellationRequested)
        {
            try
            {
                if (_matcher is { } matcher && !_paused()) ScanOnce(matcher);
            }
            catch (Exception ex)
            {
                Log.Warn($"Alarm-Scan: {ex.GetType().Name}: {ex.Message}");
            }
            _cts.Token.WaitHandle.WaitOne(Interval);
        }
    }

    private void ScanOnce(AlertKeywords.Matcher matcher)
    {
        var hwnd = Win32.GetForegroundWindow();
        if (hwnd == IntPtr.Zero) return;
        Win32.GetWindowThreadProcessId(hwnd, out var pid);
        if (pid == 0 || pid == Environment.ProcessId) return;
        string app;
        try
        {
            using var p = Process.GetProcessById(pid);
            app = p.ProcessName;
        }
        catch
        {
            return;
        }

        var buf = new char[512];
        var n = Win32.GetWindowText(hwnd, buf, buf.Length);
        var title = n > 0 ? new string(buf, 0, n) : "";
        var text = _scanText ? ReadText(hwnd) : "";

        // Unchanged window content → nothing new to report.
        var key = $"{hwnd}|{title}|{text.Length}|{text.GetHashCode()}";
        if (key == _lastKey) return;
        _lastKey = key;

        var hits = new List<AlertHit>();
        foreach (var m in matcher.Find(title)) hits.Add(new AlertHit(m.Category, m.Keyword, m.Snippet, app, title, "Fenstertitel"));
        if (text.Length > 0)
            foreach (var m in matcher.Find(text))
                if (!hits.Any(h => h.Keyword == m.Keyword))
                    hits.Add(new AlertHit(m.Category, m.Keyword, m.Snippet, app, title, "Bildschirmtext"));
        if (hits.Count > 0) _report(hits);
    }

    /// <summary>Names/values of text-bearing elements of the window (depth-first, bounded by time and size).</summary>
    private static string ReadText(IntPtr hwnd)
    {
        var sw = Stopwatch.StartNew();
        var sb = new System.Text.StringBuilder();
        AutomationElement root;
        try
        {
            root = AutomationElement.FromHandle(hwnd);
        }
        catch
        {
            return "";
        }
        var walker = TreeWalker.ControlViewWalker;
        var stack = new Stack<AutomationElement>();
        stack.Push(root);
        var count = 0;
        while (stack.Count > 0 && count < MaxElements && sw.Elapsed < Budget && sb.Length < MaxChars)
        {
            var el = stack.Pop();
            count++;
            try
            {
                var type = el.Current.ControlType;
                if (type == ControlType.Text || type == ControlType.Edit || type == ControlType.Document ||
                    type == ControlType.ListItem || type == ControlType.DataItem || type == ControlType.Hyperlink)
                {
                    var name = el.Current.Name;
                    if (!string.IsNullOrWhiteSpace(name)) sb.Append(name).Append('\n');
                    if (type == ControlType.Edit && el.TryGetCurrentPattern(ValuePattern.Pattern, out var vp))
                    {
                        var v = ((ValuePattern)vp).Current.Value;
                        if (!string.IsNullOrWhiteSpace(v)) sb.Append(v).Append('\n');
                    }
                }
                // Push children in reverse so the visual order is roughly preserved.
                var children = new List<AutomationElement>();
                for (var c = walker.GetFirstChild(el); c is not null && children.Count < 200; c = walker.GetNextSibling(c)) children.Add(c);
                for (var i = children.Count - 1; i >= 0; i--) stack.Push(children[i]);
            }
            catch (ElementNotAvailableException)
            {
                // element vanished meanwhile
            }
        }
        return sb.Length > MaxChars ? sb.ToString(0, MaxChars) : sb.ToString();
    }

    public void Dispose() => _cts.Cancel();
}
