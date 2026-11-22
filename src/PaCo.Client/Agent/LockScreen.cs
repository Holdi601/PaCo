using PaCo.Client.Config;
using PaCo.Client.Native;

namespace PaCo.Client.Agent;

/// <summary>
/// Shows the lock screen on a separate Windows desktop ("PaCoLock") created by this SYSTEM process.
/// The child's programs, taskbar and Alt+Tab live on the "Default" desktop and are unreachable while ours
/// is active. A watchdog switches back immediately if anything (e.g. Ctrl+Alt+Del → Task-Manager) leaves it.
/// </summary>
internal sealed class LockScreen
{
    /// <summary>
    /// New random name for every lock: a standard user cannot pre-create (squat) the desktop, and its DACL allows SYSTEM
    /// only, so no program of the child can be started on it.
    /// </summary>
    private string _desktopName = "";

    private Thread? _thread;
    private IntPtr _desktop;
    private volatile bool _active;
    private LockForm? _form;
    private readonly Action<string> _onCode;
    private readonly Action _onLogoff;
    private readonly Action _onLearn;

    public LockScreen(Action<string> onCode, Action onLogoff, Action onLearn)
    {
        _onCode = onCode;
        _onLogoff = onLogoff;
        _onLearn = onLearn;
    }

    public bool IsShown => _active;

    /// <summary>Shows the lock; returns true only if the lock desktop is really the active input desktop.</summary>
    /// <param name="learning">Names of the learning groups if learning mode is possible (shows a "Lernen" button).</param>
    public bool Show(string title, string text, int? logoffInSeconds, string? learning = null)
    {
        if (_active)
        {
            _form?.BeginInvoke(() => _form.Update(title, text, logoffInSeconds, learning));
            return IsOnLockDesktop();
        }

        _desktopName = "PaCoLock-" + Guid.NewGuid().ToString("N");
        if (!Win32.ConvertStringSecurityDescriptorToSecurityDescriptor("D:P(A;;GA;;;SY)", 1, out var sd, IntPtr.Zero))
            throw Win32.LastError("ConvertStringSecurityDescriptor");
        try
        {
            var sa = new Win32.SecurityAttributes { Length = System.Runtime.InteropServices.Marshal.SizeOf<Win32.SecurityAttributes>(), SecurityDescriptor = sd };
            _desktop = Win32.CreateDesktopSecure(_desktopName, IntPtr.Zero, IntPtr.Zero, 0, Win32.DesktopAllAccess, ref sa);
        }
        finally
        {
            Win32.LocalFree(sd);
        }
        if (_desktop == IntPtr.Zero) throw Win32.LastError("CreateDesktop");
        _active = true;

        var ready = new ManualResetEventSlim();
        var shown = false;
        _thread = new Thread(() =>
        {
            try
            {
                // Must happen before this thread creates any window.
                if (!Win32.SetThreadDesktop(_desktop)) throw Win32.LastError("SetThreadDesktop");
                _form = new LockForm(_onCode, _onLogoff, _onLearn);
                _form.Update(title, text, logoffInSeconds, learning);
                _form.Shown += (_, _) => { shown = true; ready.Set(); };
                Application.Run(_form);
            }
            catch (Exception ex)
            {
                Log.Error("Sperrbildschirm-Thread", ex);
                ready.Set();
            }
        })
        { IsBackground = true, Name = "LockScreen" };
        _thread.SetApartmentState(ApartmentState.STA);
        _thread.Start();
        ready.Wait(TimeSpan.FromSeconds(10));

        if (!Win32.SwitchDesktop(_desktop)) Log.Warn($"SwitchDesktop fehlgeschlagen: {System.Runtime.InteropServices.Marshal.GetLastWin32Error()}");
        new Thread(Watchdog) { IsBackground = true, Name = "LockWatchdog" }.Start();
        return shown && IsOnLockDesktop();
    }

    /// <summary>True if our lock desktop is the input desktop (or the secure desktop is up, e.g. Ctrl+Alt+Del).</summary>
    public bool IsOnLockDesktop()
    {
        if (!_active) return false;
        for (var i = 0; i < 10; i++)
        {
            var input = Win32.OpenInputDesktop(0, false, Win32.DesktopReadObjects);
            if (input == IntPtr.Zero) return true; // secure desktop – the child cannot use anything either
            var name = Win32.DesktopName(input);
            Win32.CloseDesktop(input);
            if (string.Equals(name, _desktopName, StringComparison.OrdinalIgnoreCase)) return true;
            Win32.SwitchDesktop(_desktop);
            Thread.Sleep(100);
        }
        return false;
    }

    public void ShowCodeResult(bool ok, string message) => _form?.BeginInvoke(() => _form.SetResult(ok, message));

    public void Hide()
    {
        if (!_active) return;
        _active = false;
        var def = Win32.OpenDesktop("Default", 0, false, Win32.DesktopSwitch);
        if (def != IntPtr.Zero)
        {
            Win32.SwitchDesktop(def);
            Win32.CloseDesktop(def);
        }
        try
        {
            _form?.Invoke(() => _form.CloseForGood());
        }
        catch (Exception ex)
        {
            Log.Warn($"Sperrbildschirm schließen: {ex.Message}");
        }
        _thread?.Join(TimeSpan.FromSeconds(5));
        _form = null;
        if (_desktop != IntPtr.Zero)
        {
            Win32.CloseDesktop(_desktop);
            _desktop = IntPtr.Zero;
        }
    }

    private void Watchdog()
    {
        while (_active)
        {
            Thread.Sleep(300);
            if (!_active) break;
            var input = Win32.OpenInputDesktop(0, false, Win32.DesktopReadObjects);
            // Null = secure desktop (Ctrl+Alt+Del, UAC) – cannot switch away from it, wait.
            if (input == IntPtr.Zero) continue;
            var name = Win32.DesktopName(input);
            Win32.CloseDesktop(input);
            if (name is not null && !string.Equals(name, _desktopName, StringComparison.OrdinalIgnoreCase) && _active)
                Win32.SwitchDesktop(_desktop);
        }
    }
}

/// <summary>Full-screen black form with code entry. Cannot be closed by the child.</summary>
internal sealed class LockForm : Form
{
    private readonly Label _title = new() { ForeColor = Color.White, Font = new Font("Segoe UI", 28, FontStyle.Bold), AutoSize = false, TextAlign = ContentAlignment.MiddleCenter, Dock = DockStyle.Top, Height = 80 };
    private readonly Label _text = new() { ForeColor = Color.Gainsboro, Font = new Font("Segoe UI", 14), AutoSize = false, TextAlign = ContentAlignment.TopCenter, Dock = DockStyle.Top, Height = 90 };
    private readonly Label _countdown = new() { ForeColor = Color.Orange, Font = new Font("Segoe UI", 13, FontStyle.Bold), AutoSize = false, TextAlign = ContentAlignment.MiddleCenter, Dock = DockStyle.Top, Height = 36 };
    private readonly TextBox _code = new() { Font = new Font("Consolas", 22), Width = 300, MaxLength = 13, TextAlign = HorizontalAlignment.Center, PlaceholderText = "12345-67890" };
    private readonly Button _unlock = new() { Text = L.T("Freischalten"), Font = new Font("Segoe UI", 13), Width = 180, Height = 48, BackColor = Color.FromArgb(47, 93, 138), ForeColor = Color.White, FlatStyle = FlatStyle.Flat };
    private readonly Label _result = new() { ForeColor = Color.Gainsboro, Font = new Font("Segoe UI", 12), AutoSize = false, TextAlign = ContentAlignment.MiddleCenter, Dock = DockStyle.Top, Height = 40 };
    private readonly Button _logoff = new() { Text = L.T("Abmelden"), Font = new Font("Segoe UI", 11), Width = 140, Height = 40, ForeColor = Color.White, FlatStyle = FlatStyle.Flat };
    private readonly Button _learn = new() { Text = "&" + L.T("Lernen"), Font = new Font("Segoe UI", 13), Width = 180, Height = 48, BackColor = Color.FromArgb(46, 125, 80), ForeColor = Color.White, FlatStyle = FlatStyle.Flat, Visible = false };
    private readonly Label _learnHint = new() { ForeColor = Color.Gainsboro, Font = new Font("Segoe UI", 11), AutoSize = false, TextAlign = ContentAlignment.MiddleCenter, Dock = DockStyle.Top, Height = 0 };
    private readonly System.Windows.Forms.Timer _timer = new() { Interval = 1000 };
    private readonly Action<string> _onCode;
    private bool _allowClose;
    private int? _secondsLeft;

    public LockForm(Action<string> onCode, Action onLogoff, Action onLearn)
    {
        _onCode = onCode;
        FormBorderStyle = FormBorderStyle.None;
        WindowState = FormWindowState.Maximized;
        Bounds = SystemInformation.VirtualScreen;
        BackColor = Color.Black;
        TopMost = true;
        ShowInTaskbar = false;
        KeyPreview = true;
        Text = "PaCo";

        var center = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 3, BackColor = Color.Black };
        center.RowStyles.Add(new RowStyle(SizeType.Percent, 30));
        center.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        center.RowStyles.Add(new RowStyle(SizeType.Percent, 70));

        var box = new Panel { Width = 760, Height = 520, Anchor = AnchorStyles.None, BackColor = Color.Black };
        var entry = new FlowLayoutPanel { Dock = DockStyle.Top, Height = 64, FlowDirection = FlowDirection.LeftToRight, Padding = new Padding(120, 6, 0, 0) };
        entry.Controls.Add(_code);
        entry.Controls.Add(_unlock);
        var hint = new Label
        {
            Text = L.T("Freischaltcode der Eltern eingeben. Deine Eltern können dir auch über die PaCo-Website mehr Zeit geben."),
            ForeColor = Color.Gray, Font = new Font("Segoe UI", 10), AutoSize = false, TextAlign = ContentAlignment.MiddleCenter, Dock = DockStyle.Top, Height = 40,
        };
        var bottom = new FlowLayoutPanel { Dock = DockStyle.Top, Height = 60, Padding = new Padding(310, 10, 0, 0) };
        bottom.Controls.Add(_logoff);
        // Learning mode (if the parents allow it): back to the desktop, but only learning apps and sites work.
        var learnRow = new FlowLayoutPanel { Dock = DockStyle.Top, Height = 60, Padding = new Padding(290, 6, 0, 0) };
        learnRow.Controls.Add(_learn);

        // Dock=Top stacks in reverse order of adding.
        box.Controls.Add(bottom);
        box.Controls.Add(learnRow);
        box.Controls.Add(_learnHint);
        box.Controls.Add(_result);
        box.Controls.Add(hint);
        box.Controls.Add(entry);
        box.Controls.Add(_countdown);
        box.Controls.Add(_text);
        box.Controls.Add(_title);
        center.Controls.Add(new Panel { BackColor = Color.Black, Dock = DockStyle.Fill }, 0, 0);
        center.Controls.Add(box, 0, 1);
        center.Controls.Add(new Panel { BackColor = Color.Black, Dock = DockStyle.Fill }, 0, 2);
        Controls.Add(center);

        _unlock.Click += (_, _) => Submit();
        _code.KeyDown += (_, e) => { if (e.KeyCode == Keys.Enter) { e.SuppressKeyPress = true; Submit(); } };
        _logoff.Click += (_, _) => onLogoff();
        _learn.Click += (_, _) => { _learn.Enabled = false; onLearn(); };
        _timer.Tick += (_, _) => Tick();
        _timer.Start();
        Shown += (_, _) => { Activate(); _code.Focus(); };
        Deactivate += (_, _) => BeginInvoke(() => { if (!_allowClose) { TopMost = true; Activate(); } });
    }

    public void Update(string title, string text, int? logoffInSeconds, string? learning)
    {
        _title.Text = title;
        _text.Text = text;
        _learn.Visible = learning is not null;
        _learn.Enabled = true;
        _learnHint.Height = learning is null ? 0 : 40;
        _learnHint.Text = learning is null ? "" : L.F("Lernen geht weiter: {0}", learning);
        _secondsLeft = logoffInSeconds;
        _countdown.Text = logoffInSeconds is { } s ? L.F("Du wirst in {0} Sekunden abgemeldet.", s) : "";
    }

    public void SetResult(bool ok, string message)
    {
        _result.ForeColor = ok ? Color.LightGreen : Color.Salmon;
        _result.Text = message;
        _unlock.Enabled = true;
        if (!ok) { _code.SelectAll(); _code.Focus(); }
    }

    public void CloseForGood()
    {
        _allowClose = true;
        _timer.Stop();
        Close();
    }

    private void Submit()
    {
        var code = _code.Text.Trim();
        if (code.Length == 0) return;
        _unlock.Enabled = false;
        _result.ForeColor = Color.Gainsboro;
        _result.Text = L.T("Prüfe Code …");
        _onCode(code);
    }

    private void Tick()
    {
        if (_secondsLeft is > 0)
        {
            _secondsLeft--;
            _countdown.Text = L.F("Du wirst in {0} Sekunden abgemeldet.", _secondsLeft);
        }
        if (!_allowClose && !TopMost) TopMost = true;
    }

    protected override void OnFormClosing(FormClosingEventArgs e)
    {
        // Alt+F4 & co.: only the service may close the lock screen.
        if (!_allowClose && e.CloseReason != CloseReason.WindowsShutDown) e.Cancel = true;
        base.OnFormClosing(e);
    }

    protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
    {
        if (keyData is (Keys.Alt | Keys.F4) or (Keys.Alt | Keys.Tab) or Keys.Escape) return true;
        return base.ProcessCmdKey(ref msg, keyData);
    }
}
