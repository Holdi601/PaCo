using System.IO.Pipes;
using PaCo.Client.Config;
using PaCo.Client.Native;
using PaCo.Shared;
using PaCo.Shared.I18n;

namespace PaCo.Client.Agent;

/// <summary>
/// Runs as SYSTEM inside the child's session: tray status, warnings, foreground-app tracking, lock screen and
/// muting. It has no own logic about time – it only executes what the service tells it. If it dies, the service
/// restarts it; if that keeps failing the service signs the child out (fail closed).
/// </summary>
internal sealed class AgentApp : ApplicationContext
{
    private readonly string _pipeName;
    private readonly NotifyIcon _tray;
    private readonly ForegroundTracker _tracker = new();
    private readonly LockScreen _lock;
    private readonly System.Windows.Forms.Timer _sampleTimer = new() { Interval = 1000 };
    private readonly SynchronizationContext _ui;
    private IpcChannel? _channel;
    private int _tick;
    private string _childName = "";

    public static int Run(string pipeName)
    {
        AppDomain.CurrentDomain.UnhandledException += (_, e) => Log.Error("Agent abgestürzt", e.ExceptionObject as Exception);
        Application.SetUnhandledExceptionMode(UnhandledExceptionMode.CatchException);
        Application.ThreadException += (_, e) => Log.Error("Agent UI-Fehler", e.Exception);
        Application.EnableVisualStyles();
        Application.SetHighDpiMode(HighDpiMode.SystemAware);

        // The tray icon lives in explorer (medium integrity); allow its click messages to reach this SYSTEM process.
        Win32.ChangeWindowMessageFilter(0x0400 + 1024, Win32.MsgFltAdd);
        Win32.ChangeWindowMessageFilter(Win32.RegisterWindowMessage("TaskbarCreated"), Win32.MsgFltAdd);

        WindowsFormsSynchronizationContext.AutoInstall = true;
        using var form = new Form(); // ensures a WinForms sync context exists on this thread
        Application.Run(new AgentApp(pipeName));
        return 0;
    }

    private AgentApp(string pipeName)
    {
        _pipeName = pipeName;
        _ui = SynchronizationContext.Current ?? new WindowsFormsSynchronizationContext();
        _tray = new NotifyIcon
        {
            Icon = SystemIcons.Shield,
            Text = L.T("PaCo Kinderschutz"),
            Visible = true,
        };
        _tray.Click += (_, _) => _tray.ShowBalloonTip(5000, "PaCo", _tray.Text, ToolTipIcon.Info);
        _lock = new LockScreen(code => Send(new IpcMessage { Type = IpcMessage.Code, Text = code }),
            () => Send(new IpcMessage { Type = IpcMessage.RequestLogoff }),
            () => Send(new IpcMessage { Type = IpcMessage.StartLearning }));
        _sampleTimer.Tick += (_, _) => OnTick();
        _sampleTimer.Start();
        _scanner = new AlertScanner(
            hits => Send(new IpcMessage
            {
                Type = IpcMessage.AlertHits,
                Hits = hits.Select(h => new AlertHitDto(h.Category, h.Keyword, h.Snippet, h.App, h.Title, h.Source)).ToList(),
            }),
            () => _lock.IsShown);
        _ = ConnectAsync();
    }

    private readonly AlertScanner _scanner;

    private async Task ConnectAsync()
    {
        try
        {
            var pipe = new NamedPipeClientStream(".", _pipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
            await pipe.ConnectAsync(10_000);
            _channel = new IpcChannel(pipe);
            await _channel.SendAsync(new IpcMessage { Type = IpcMessage.Hello, Seconds = Environment.ProcessId });
            while (await _channel.ReceiveAsync() is { } msg)
                _ui.Post(_ => Handle(msg), null);
        }
        catch (Exception ex)
        {
            Log.Error("Agent: Verbindung zum Dienst", ex);
        }
        // Service gone or pipe closed: exit; the service starts a fresh agent when needed. If the lock screen is up
        // and nobody said "unlock", sign the session out rather than revealing the desktop (fail closed).
        _ui.Post(_ =>
        {
            if (_lock.IsShown && !_exitRequested)
            {
                Log.Warn("Verbindung zum Dienst verloren, während gesperrt – Sitzung wird abgemeldet");
                Win32.WTSLogoffSession(Win32.WtsCurrentServer, -1, false);
            }
            Shutdown();
        }, null);
    }

    private void Handle(IpcMessage msg)
    {
        try
        {
            switch (msg.Type)
            {
                case IpcMessage.Status:
                    _childName = msg.ChildName ?? "";
                    if (msg.Lang is { } statusLang && Languages.IsSupported(statusLang)) L.Lang = statusLang;
                    _tray.Text = Truncate(msg.Paused ? L.T("PaCo: pausiert")
                        : !msg.Allowed && _learning ? L.T("PaCo: Lernmodus – nur Lern-Apps")
                        : !msg.Allowed ? L.T("PaCo: keine Zeit mehr")
                        : msg.RemainingSeconds is { } r ? L.F("PaCo: noch {0}", FormatMinutes(r)) : L.T("PaCo: ohne Zeitlimit"), 63);
                    break;
                case IpcMessage.Notify:
                    _tray.ShowBalloonTip(10_000, msg.Title ?? "PaCo", msg.Text ?? "", ToolTipIcon.Warning);
                    break;
                case IpcMessage.ShowLock:
                    if (msg.Lang is { } lang && Languages.IsSupported(lang)) L.Lang = lang;
                    bool locked;
                    try { locked = _lock.Show(msg.Title ?? L.T("Bildschirmzeit vorbei"), msg.Text ?? "", msg.Seconds, msg.Learning); }
                    catch (Exception ex) { Log.Error("Sperrbildschirm", ex); locked = false; }
                    // Only a confirmed lock counts – otherwise the service signs the child out (fail closed).
                    Send(new IpcMessage { Type = IpcMessage.LockState, Flag = locked });
                    break;
                case IpcMessage.HideLock:
                    _lock.Hide();
                    Send(new IpcMessage { Type = IpcMessage.LockState, Flag = false });
                    break;
                case IpcMessage.CodeResult:
                    _lock.ShowCodeResult(msg.Flag, msg.Text ?? "");
                    break;
                case IpcMessage.Mute:
                    Audio.SetMuteAll(true);
                    break;
                case IpcMessage.RestoreAudio:
                    Audio.SetMuteAll(false);
                    break;
                case IpcMessage.Exit:
                    _exitRequested = true;
                    Shutdown();
                    break;
                case IpcMessage.AlertConfig when msg.Text is { } json:
                    _scanner.Configure(PaCoJson.Deserialize<AlertSettings>(json));
                    break;
                case IpcMessage.CloseTab:
                    CloseTab(msg.Host, msg.Url);
                    break;
                case IpcMessage.LearningMode:
                    _learning = msg.Flag;
                    if (_learning) ReportWindows();
                    break;

                case IpcMessage.TakeScreenshot when msg.RequestId is { } rid:
                    byte[] jpeg = [];
                    try
                    {
                        jpeg = ScreenCapture.CaptureJpeg();
                    }
                    catch (Exception ex)
                    {
                        Log.Warn($"Bildschirmfoto fehlgeschlagen: {ex.Message}");
                    }
                    Send(new IpcMessage { Type = IpcMessage.ScreenshotData, RequestId = rid, Text = Convert.ToBase64String(jpeg) });
                    if (msg.Flag) _tray.ShowBalloonTip(6000, "PaCo", L.T("Deine Eltern haben ein Bildschirmfoto angesehen."), ToolTipIcon.Info);
                    break;
            }
        }
        catch (Exception ex)
        {
            Log.Error($"Agent: Befehl {msg.Type}", ex);
            Send(new IpcMessage { Type = IpcMessage.LogLine, Text = $"{msg.Type}: {ex.Message}" });
        }
    }

    private bool _exitRequested;
    private bool _learning;

    /// <summary>Learning mode: which programs have windows – the service closes everything that is not for learning.</summary>
    private void ReportWindows()
    {
        try
        {
            var audio = Audio.PlayingProcessIds().Select(_tracker.Describe).OfType<WindowProcess>().Take(50).ToList();
            Send(new IpcMessage { Type = IpcMessage.Windows, WindowProcesses = _tracker.VisibleWindowProcesses().Take(200).ToList(), AudioProcesses = audio });
        }
        catch (Exception ex)
        {
            Log.Warn($"Fensterliste: {ex.Message}");
        }
    }
    private string? _host;
    private string? _url;
    private (IntPtr Hwnd, string? Title) _hostFor;
    private TimeSpan _hostAt = Mono.Never;
    private int _hostReading;
    private string? _sentFocus;
    private int _focusTick;

    /// <summary>
    /// Tells the service what is in the foreground (for per-app/site limits and learning time) – on every change and
    /// every 5 s. The browser address is read in the background (UI Automation can take a moment).
    /// </summary>
    private void ReportFocus()
    {
        var cur = _lock.IsShown ? null : _tracker.Current;
        if (cur is { } c && BrowserAddress.IsBrowser(c.Process))
        {
            var stale = _hostFor != (c.Hwnd, c.Title) || Mono.Now - _hostAt > TimeSpan.FromSeconds(10);
            if (stale && Interlocked.CompareExchange(ref _hostReading, 1, 0) == 0)
            {
                var (hwnd, title, process) = (c.Hwnd, c.Title, c.Process);
                Task.Run(() =>
                {
                    try
                    {
                        var address = BrowserAddress.ReadAddress(hwnd, process);
                        _host = TimeGroups.HostOf(address);
                        _url = _host is null ? null : address;
                        _hostFor = (hwnd, title);
                        _hostAt = Mono.Now;
                    }
                    finally { Interlocked.Exchange(ref _hostReading, 0); }
                });
            }
        }
        var host = cur is { } b && BrowserAddress.IsBrowser(b.Process) && _hostFor.Hwnd == b.Hwnd ? _host : null;
        var url = host is null ? null : _url;
        var key = cur is null ? "" : $"{cur.Value.Process}|{cur.Value.Description}|{url ?? host}";
        if (key == _sentFocus && ++_focusTick % 5 != 0) return;
        _sentFocus = key;
        Send(new IpcMessage { Type = IpcMessage.Focus, Process = cur?.Process, Description = cur?.Description, Host = host, Url = url });
    }

    /// <summary>
    /// Closes the active browser tab if it still shows the given site (time used up) or – learning mode – the given
    /// address that is not allowed (Ctrl+W).
    /// </summary>
    private void CloseTab(string? site, string? url = null)
    {
        if ((site is null && url is null) || _tracker.Current is not { } c || !BrowserAddress.IsBrowser(c.Process)) return;
        var address = BrowserAddress.ReadAddress(c.Hwnd, c.Process);
        var host = TimeGroups.HostOf(address);
        if (host is null) return;
        if (site is not null && !TimeGroups.SiteMatches(host, site)) return;
        if (url is not null && !string.Equals(address, url, StringComparison.Ordinal)) return;
        if (Win32.GetForegroundWindow() != c.Hwnd) return; // focus moved meanwhile – never close something else
        SendKeys.SendWait("^w");
        Log.Info($"Tab mit {host} geschlossen");
    }

    private void OnTick()
    {
        if (!_lock.IsShown) _tracker.Sample();
        ReportFocus();
        if (_learning && !_lock.IsShown && _tick % 2 == 0) ReportWindows();
        if (++_tick % 5 == 0)
        {
            var samples = _tracker.Drain();
            if (samples.Count > 0) Send(new IpcMessage { Type = IpcMessage.Usage, Samples = samples });
        }
    }

    private void Send(IpcMessage msg)
    {
        var ch = _channel;
        if (ch is null) return;
        _ = ch.SendAsync(msg).ContinueWith(t => Log.Warn($"Agent senden: {t.Exception?.GetBaseException().Message}"), TaskContinuationOptions.OnlyOnFaulted);
    }

    private void Shutdown()
    {
        try
        {
            _scanner.Dispose();
            _lock.Hide();
            _tray.Visible = false;
            _tray.Dispose();
        }
        catch { /* exiting */ }
        ExitThread();
    }

    private static string FormatMinutes(long seconds) =>
        seconds >= 3600 ? L.F("{0} Std {1} Min", seconds / 3600, seconds % 3600 / 60) : L.F("{0} Min", Math.Max(1, (seconds + 59) / 60));

    private static string Truncate(string s, int max) => s.Length <= max ? s : s[..max];
}
