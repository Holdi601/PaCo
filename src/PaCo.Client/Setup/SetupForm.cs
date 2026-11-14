using System.Security.Principal;
using PaCo.Client.Config;
using PaCo.Client.Service;
using PaCo.Shared;

namespace PaCo.Client.Setup;

/// <summary>Installer wizard: connect → choose child accounts → install.</summary>
internal sealed class SetupForm : Form
{
    private readonly Panel _page = new() { Dock = DockStyle.Fill, Padding = new Padding(24) };
    private readonly SetupPayload? _payload;
    private PairResponse? _pair;
    private string _serverUrl = "";
    private List<WindowsAccount> _accounts = [];

    public SetupForm(SetupPayload? payload)
    {
        _payload = payload;
        Text = "PaCo Kinderschutz – Einrichtung";
        Width = 760;
        Height = 560;
        StartPosition = FormStartPosition.CenterScreen;
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = false;
        Font = new Font("Segoe UI", 10);
        BackColor = Color.White;
        Icon = SystemIcons.Shield;
        Controls.Add(_page);
        Controls.Add(new Label
        {
            Text = "  PaCo Kinderschutz", Dock = DockStyle.Top, Height = 54, Font = new Font("Segoe UI", 16, FontStyle.Bold),
            ForeColor = Color.White, BackColor = Color.FromArgb(47, 93, 138), TextAlign = ContentAlignment.MiddleLeft,
        });
        Load += (_, _) => { if (Installer.IsInstalled) ShowInstalledPage(); else ShowConnectPage(); };
    }

    // ---------- Page 1: connect ----------
    private void ShowConnectPage()
    {
        var intro = Para("PaCo wird jetzt mit dem PaCo-Server verbunden. Danach wählst du aus, welche Windows-Konten Kindern gehören.\n\n" +
                         "Wichtig: Diese Einrichtung muss mit einem Administrator-Konto gemacht werden (nicht mit dem Konto des Kindes).", 90);
        var urlLabel = Para("Serveradresse", 24);
        var url = new TextBox { Dock = DockStyle.Top, Text = _payload?.ServerUrl ?? "https://", Height = 30 };
        var codeLabel = Para("Kopplungscode (von der Website, „Geräte → Neues Gerät hinzufügen“)", 30);
        var code = new TextBox { Dock = DockStyle.Top, Text = _payload?.PairingCode ?? "", Font = new Font("Consolas", 14), CharacterCasing = CharacterCasing.Upper };
        var status = Para("", 50, Color.Firebrick);
        var next = Button("Verbinden");
        next.Click += async (_, _) =>
        {
            next.Enabled = false;
            status.ForeColor = Color.DimGray;
            status.Text = "Verbinde …";
            try
            {
                _serverUrl = url.Text.Trim().TrimEnd('/');
                if (!_serverUrl.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
                    throw new InvalidOperationException("Die Serveradresse muss mit https:// beginnen.");
                _pair = await Installer.PairAsync(_serverUrl, code.Text.Trim());
                _accounts = LocalAccounts.List();
                Log.Info($"Gekoppelt mit {_serverUrl} als Gerät {_pair.DeviceId}");
                ShowAccountsPage();
            }
            catch (Exception ex)
            {
                status.ForeColor = Color.Firebrick;
                status.Text = Friendly(ex);
                next.Enabled = true;
            }
        };
        Show(next, intro, urlLabel, url, codeLabel, code, status);
        if (_payload is not null) next.Focus();
    }

    // ---------- Page 2: accounts ----------
    private sealed record Row(WindowsAccount Account, ComboBox Combo, CheckBox? MakeStandard);

    private void ShowAccountsPage()
    {
        var me = WindowsIdentity.GetCurrent().User?.Value;
        var intro = Para($"Verbunden mit Familie „{_pair!.FamilyName}“. Wähle für jedes Windows-Konto, ob es einem Kind gehört. " +
                         "Für Kinderkonten gelten dann alle Regeln von der Website.", 50);
        var table = new TableLayoutPanel { Dock = DockStyle.Top, AutoSize = true, ColumnCount = 3, Padding = new Padding(0, 6, 0, 6) };
        table.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 38));
        table.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 37));
        table.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 25));
        var rows = new List<Row>();

        foreach (var acc in _accounts)
        {
            var isMe = acc.Sid == me;
            var name = acc.FullName is { } fn && fn != acc.UserName ? $"{acc.UserName} ({fn})" : acc.UserName;
            if (isMe) name += " – das bist du";
            else if (acc.IsAdmin) name += " – Administrator";
            table.Controls.Add(new Label { Text = name, AutoSize = true, Anchor = AnchorStyles.Left, Padding = new Padding(0, 6, 0, 0) });

            var combo = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Width = 240 };
            combo.Items.Add(new Choice("— kein Kind (nicht überwacht) —", null, null));
            foreach (var c in _pair.Children) combo.Items.Add(new Choice($"Kind: {c.Name}", c.Id, null));
            var suggested = (acc.FullName ?? acc.UserName).Split(' ')[0];
            if (!_pair.Children.Any(c => string.Equals(c.Name, suggested, StringComparison.OrdinalIgnoreCase)))
                combo.Items.Add(new Choice($"Neues Kind: {suggested}", null, suggested));
            var existing = combo.Items.Cast<Choice>().FirstOrDefault(c => c.ChildId is not null && string.Equals(c.Label, $"Kind: {suggested}", StringComparison.OrdinalIgnoreCase));
            combo.SelectedIndex = isMe || acc.IsAdmin ? 0 : existing is not null ? combo.Items.IndexOf(existing) : combo.Items.Count - 1;
            combo.Enabled = !isMe;
            table.Controls.Add(combo);

            CheckBox? std = null;
            if (acc.IsAdmin && !isMe)
            {
                std = new CheckBox { Text = "zu Standardkonto machen", AutoSize = true, Anchor = AnchorStyles.Left };
                combo.SelectedIndexChanged += (_, _) => std.Checked = ((Choice)combo.SelectedItem!).IsChild;
                table.Controls.Add(std);
            }
            else
            {
                table.Controls.Add(new Label());
            }
            rows.Add(new Row(acc, combo, std));
        }

        var warn = Para("Kinderkonten müssen Standardkonten sein – mit Administratorrechten könnte ein Kind PaCo entfernen.", 44, Color.DarkOrange);
        var status = Para("", 40, Color.Firebrick);
        var next = Button("Installieren");
        next.Click += async (_, _) =>
        {
            var chosen = rows.Where(r => ((Choice)r.Combo.SelectedItem!).IsChild).ToList();
            if (chosen.Count == 0)
            {
                status.Text = "Bitte mindestens ein Kinderkonto auswählen.";
                return;
            }
            var adminKids = chosen.Where(r => r.Account.IsAdmin && r.MakeStandard?.Checked != true).ToList();
            if (adminKids.Count > 0 && MessageBox.Show(this,
                    $"{string.Join(", ", adminKids.Select(r => r.Account.UserName))} hat Administratorrechte. Damit kann PaCo umgangen werden. Trotzdem fortfahren?",
                    "PaCo", MessageBoxButtons.YesNo, MessageBoxIcon.Warning) != DialogResult.Yes)
                return;

            var assignments = rows.Select(r =>
            {
                var c = (Choice)r.Combo.SelectedItem!;
                return new AccountAssignment { Sid = r.Account.Sid, ChildId = c.ChildId, NewChildName = c.NewName };
            }).ToList();
            var demote = rows.Where(r => r.MakeStandard?.Checked == true).Select(r => r.Account.Sid).ToList();
            await ShowSecurityPageAsync(assignments, demote, chosen.Select(r => r.Account.Sid).ToList());
        };
        var scroll = new Panel { Dock = DockStyle.Top, Height = 220, AutoScroll = true };
        scroll.Controls.Add(table);
        Show(next, intro, scroll, warn, status);
    }

    private sealed record Choice(string Label, Guid? ChildId, string? NewName)
    {
        public bool IsChild => ChildId is not null || NewName is not null;

        public override string ToString() => Label;
    }

    // ---------- Page 3: security check ----------
    private async Task ShowSecurityPageAsync(List<AccountAssignment> assignments, List<string> demote, List<string> childSids)
    {
        Show(null, Para("Sicherheits-Check läuft …", 40));
        var findings = await Task.Run(() => SecurityAudit.Run(childSids));
        // Child accounts chosen for conversion count as fixed.
        findings = findings.Where(x => !(x.Code.StartsWith("child-admin:") && demote.Contains(x.Code["child-admin:".Length..]))).ToList();

        var intro = Para("Damit dein Kind PaCo nicht umgehen kann und der PC gut geschützt ist, wurde Folgendes geprüft:", 40);
        var list = new FlowLayoutPanel { Dock = DockStyle.Top, Height = 250, AutoScroll = true, FlowDirection = FlowDirection.TopDown, WrapContents = false };
        foreach (var x in findings.OrderByDescending(x => x.Severity))
        {
            var (icon, color) = x.Severity switch
            {
                SecuritySeverity.Critical => ("✖", Color.Firebrick),
                SecuritySeverity.Warning => ("▲", Color.DarkOrange),
                SecuritySeverity.Info => ("ℹ", Color.SteelBlue),
                _ => ("✔", Color.SeaGreen),
            };
            list.Controls.Add(new Label
            {
                Text = $"{icon}  {x.Text}{(x.Fixable ? "  (automatisch behebbar)" : "")}", ForeColor = color, AutoSize = true, MaximumSize = new Size(660, 0),
                Padding = new Padding(0, 3, 0, 3),
            });
        }
        var fixBox = new CheckBox
        {
            Text = "Empfohlene Korrekturen automatisch anwenden", Checked = true, Dock = DockStyle.Top, Height = 32,
            Visible = findings.Any(x => x.Fixable && x.Severity != SecuritySeverity.Ok),
        };
        var critical = findings.Count(x => x.Severity == SecuritySeverity.Critical && !x.Fixable);
        var hint = Para(critical > 0 ? "Rote Punkte ohne automatische Korrektur bitte selbst beheben (z. B. Passwort für das Admin-Konto setzen) – PaCo zeigt sie auch auf der Website an." : "", 44, Color.Firebrick);
        var next = Button("Installieren");
        next.Click += async (_, _) => await RunInstallAsync(assignments, demote, fixBox.Checked ? findings : []);
        Show(next, intro, list, fixBox, hint);
    }

    // ---------- Page 4: install ----------
    private async Task RunInstallAsync(List<AccountAssignment> assignments, List<string> demote, List<SecurityFinding> fixes)
    {
        var status = Para("Installiere …", 60);
        var bar = new ProgressBar { Dock = DockStyle.Top, Style = ProgressBarStyle.Marquee, Height = 24 };
        Show(null, status, bar);
        try
        {
            if (fixes.Count > 0)
            {
                status.Text = "Sicherheits-Korrekturen werden angewendet …";
                await Task.Run(() => SecurityAudit.Fix(fixes));
            }
            await Installer.InstallAsync(_serverUrl, _pair!, assignments, demote, new Progress<string>(s => status.Text = s));
            ShowDonePage();
        }
        catch (Exception ex)
        {
            Log.Error("Installation fehlgeschlagen", ex);
            bar.Style = ProgressBarStyle.Continuous;
            status.ForeColor = Color.Firebrick;
            status.Text = "Installation fehlgeschlagen: " + Friendly(ex) + "\n\nDetails: C:\\ProgramData\\PaCo\\paco.log";
        }
    }

    private void ShowDonePage()
    {
        var done = Para("✅ PaCo ist eingerichtet!\n\n" +
                        "• Die Regeln gelten ab der nächsten Anmeldung des Kindes (bereits angemeldete Kinder sofort).\n" +
                        "• Bildschirmzeit, Sperren und Zusatzzeit verwaltest du auf der PaCo-Website.\n" +
                        "• Tipp: Setze ein BIOS-Passwort, damit niemand die Uhr im BIOS verstellt.", 180);
        var close = Button("Schließen");
        close.Click += (_, _) => Close();
        Show(close, done);
    }

    // ---------- Already installed ----------
    private void ShowInstalledPage()
    {
        var info = Para("PaCo ist auf diesem PC bereits installiert.\n\nAktualisieren ersetzt das Programm durch diese Version – alle Einstellungen bleiben erhalten.", 110);
        var status = Para("", 60);
        var update = Button("Aktualisieren");
        var remove = new Button { Text = "Deinstallieren", Dock = DockStyle.Bottom, Height = 36 };
        update.Click += async (_, _) =>
        {
            update.Enabled = remove.Enabled = false;
            try
            {
                SecureStore.EnsureDataDir();
                await Installer.InstallBinariesAndServiceAsync(new Progress<string>(s => status.Text = s));
                status.Text = "✅ Aktualisiert.";
            }
            catch (Exception ex)
            {
                Log.Error("Update fehlgeschlagen", ex);
                status.ForeColor = Color.Firebrick;
                status.Text = Friendly(ex);
            }
        };
        remove.Click += async (_, _) =>
        {
            if (MessageBox.Show(this, "PaCo wirklich entfernen? Danach gelten auf diesem PC keine Regeln mehr.", "PaCo",
                    MessageBoxButtons.YesNo, MessageBoxIcon.Warning) != DialogResult.Yes) return;
            update.Enabled = remove.Enabled = false;
            await Installer.UninstallAsync(new Progress<string>(s => status.Text = s));
        };
        Show(update, info, status);
        _page.Controls.Add(remove);
    }

    // ---------- helpers ----------
    private void Show(Button? primary, params Control[] controls)
    {
        _page.Controls.Clear();
        // Dock=Top stacks bottom-up in reverse insertion order.
        for (var i = controls.Length - 1; i >= 0; i--) _page.Controls.Add(controls[i]);
        if (primary is not null)
        {
            _page.Controls.Add(primary);
            AcceptButton = primary;
        }
    }

    private static Label Para(string text, int height, Color? color = null) => new()
    {
        Text = text, Dock = DockStyle.Top, Height = height, ForeColor = color ?? Color.Black, Padding = new Padding(0, 6, 0, 0),
    };

    private static Button Button(string text) => new()
    {
        Text = text, Dock = DockStyle.Bottom, Height = 44, BackColor = Color.FromArgb(47, 93, 138), ForeColor = Color.White,
        FlatStyle = FlatStyle.Flat, Font = new Font("Segoe UI", 11, FontStyle.Bold),
    };

    private static string Friendly(Exception ex) => ex switch
    {
        HttpRequestException => "Der Server ist nicht erreichbar. Ist der PC mit dem Internet verbunden und die Adresse richtig?",
        TaskCanceledException => "Der Server antwortet nicht (Zeitüberschreitung).",
        ServerException s => s.Message,
        _ => ex.Message,
    };
}
