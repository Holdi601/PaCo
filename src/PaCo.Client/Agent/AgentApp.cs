using System.IO.Pipes;
using PaCo.Client.Config;
using PaCo.Client.Native;
using PaCo.Shared;

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
            Text = "PaCo Kinderschutz",
            Visible = true,
        };
        _tray.Click += (_, _) => _tray.ShowBalloonTip(5000, "PaCo", _tray.Text, ToolTipIcon.Info);
        _lock = new LockScreen(code => Send(new IpcMessage { Type = IpcMessage.Code, Text = code }),
            () => Send(new IpcMessage { Type = IpcMessage.RequestLogoff }));
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
        // Service gone or pipe closed: exit; the service starts a fresh agent when needed.
        _ui.Post(_ => Shutdown(), null);
    }

    private void Handle(IpcMessage msg)
    {
        try
        {
            switch (msg.Type)
            {
                case IpcMessage.Status:
                    _childName = msg.ChildName ?? "";
                    _tray.Text = Truncate(msg.Paused ? "PaCo: pausiert"
                        : !msg.Allowed ? "PaCo: keine Zeit mehr"
                        : msg.RemainingSeconds is { } r ? $"PaCo: noch {FormatMinutes(r)}" : "PaCo: ohne Zeitlimit", 63);
                    break;
                case IpcMessage.Notify:
                    _tray.ShowBalloonTip(10_000, msg.Title ?? "PaCo", msg.Text ?? "", ToolTipIcon.Warning);
                    break;
                case IpcMessage.ShowLock:
                    _lock.Show(msg.Title ?? "Bildschirmzeit vorbei", msg.Text ?? "", msg.Seconds);
                    Send(new IpcMessage { Type = IpcMessage.LockState, Flag = true });
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
                    Shutdown();
                    break;
                case IpcMessage.AlertConfig when msg.Text is { } json:
                    _scanner.Configure(PaCoJson.Deserialize<AlertSettings>(json));
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
                    if (msg.Flag) _tray.ShowBalloonTip(6000, "PaCo", "Deine Eltern haben ein Bildschirmfoto angesehen.", ToolTipIcon.Info);
                    break;
            }
        }
        catch (Exception ex)
        {
            Log.Error($"Agent: Befehl {msg.Type}", ex);
            Send(new IpcMessage { Type = IpcMessage.LogLine, Text = $"{msg.Type}: {ex.Message}" });
        }
    }

    private void OnTick()
    {
        if (!_lock.IsShown) _tracker.Sample();
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
        seconds >= 3600 ? $"{seconds / 3600} Std {seconds % 3600 / 60} Min" : $"{Math.Max(1, (seconds + 59) / 60)} Min";

    private static string Truncate(string s, int max) => s.Length <= max ? s : s[..max];
}
