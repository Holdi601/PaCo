using System.Diagnostics;
using Microsoft.Extensions.Hosting;
using PaCo.Client.Config;
using PaCo.Client.Native;
using PaCo.Shared;

namespace PaCo.Client.Service;

/// <summary>
/// The enforcement loop (1 Hz). Fail-closed: if the session agent cannot be started or connected, a blocked
/// child is signed out instead of being let through.
/// </summary>
internal sealed class Enforcer : BackgroundService
{
    private static readonly int[] WarnAt = [600, 300, 60];
    private static readonly TimeSpan AgentGrace = TimeSpan.FromSeconds(25);
    private static readonly TimeSpan LogoffCountdownAfterLogin = TimeSpan.FromSeconds(60);
    private static readonly TimeSpan LogoffDelayAfterExpiry = TimeSpan.FromSeconds(4);

    private sealed class Track
    {
        public bool WasAllowed;
        public DateTimeOffset? BlockedSince;
        public bool ExpiredDuringUse;
        public bool LockRequested;
        public bool Muted;
        public DateTimeOffset? LogoffAt;
        public double Fraction;
        public DateOnly WarnDay;
        public HashSet<int> Warned = [];
        public DateTime LastStatus = DateTime.MinValue;
        public bool? LastAllowed;
        public Guid ChildId;
        public DateTime LastBlockedNotice = DateTime.MinValue;
        public string? CurrentApp;
        public DateTime CurrentAppAt = DateTime.MinValue;
    }

    private readonly Runtime _rt;
    private readonly AgentManager _agents;
    private readonly Dictionary<int, Track> _tracks = [];
    private readonly Lock _tracksGate = new();
    private DateTime _lastBrowserForce = DateTime.MinValue;
    private DateTime _lastAppScan = DateTime.MinValue;
    private bool _revokedHandled;

    public Enforcer(Runtime rt)
    {
        _rt = rt;
        _agents = new AgentManager(OnAgentMessageAsync);
    }

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        Log.Info("Enforcer gestartet");
        var sw = Stopwatch.StartNew();
        var last = sw.Elapsed;
        while (!ct.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(1000, ct);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            // Wall-clock independent elapsed time; capped so sleep/hibernate is never charged.
            var elapsed = Math.Clamp((sw.Elapsed - last).TotalSeconds, 0, 5);
            last = sw.Elapsed;
            try
            {
                await TickAsync(elapsed);
            }
            catch (Exception ex)
            {
                Log.Error("Enforcer-Tick", ex);
            }
        }
        lock (_rt.Gate) _rt.SaveIfNeeded(force: true);
        _agents.Dispose();
        Log.Info("Enforcer beendet");
    }

    private async Task TickAsync(double elapsed)
    {
        var policy = _rt.Policies.Current;
        if (policy is null)
        {
            _agents.Retain(new HashSet<int>());
            return;
        }
        if (policy.Revoked)
        {
            HandleRevoked(policy);
            return;
        }

        var tz = FamilyClock.Resolve(policy.TimeZoneId);
        var sessions = Sessions.Enumerate();
        var monitored = sessions
            .Select(s => (Session: s, Child: policy.ChildForSid(s.Sid)))
            .Where(x => x.Child is not null)
            .Select(x => (x.Session, Child: x.Child!))
            .ToList();
        _agents.Retain(monitored.Select(m => m.Session.SessionId).ToHashSet());

        DateTimeOffset now;
        lock (_rt.Gate) now = _rt.Now();

        var forceBrowser = DateTime.UtcNow - _lastBrowserForce > TimeSpan.FromMinutes(1);
        if (forceBrowser) _lastBrowserForce = DateTime.UtcNow;
        var scanApps = DateTime.UtcNow - _lastAppScan > TimeSpan.FromSeconds(2);
        if (scanApps) _lastAppScan = DateTime.UtcNow;

        var active = new HashSet<Guid>();
        var locked = new HashSet<Guid>();
        var live = new Dictionary<Guid, ChildLiveStatus>();

        foreach (var (s, child) in monitored)
        {
            Track track;
            lock (_tracksGate)
            {
                if (!_tracks.TryGetValue(s.SessionId, out track!))
                {
                    _tracks[s.SessionId] = track = new Track { ChildId = child.ChildId };
                    Log.Info($"Kinder-Sitzung erkannt: {s.UserName} (Sitzung {s.SessionId}) → {child.Name}");
                }
                track.ChildId = child.ChildId;
            }

            _agents.Ensure(s.SessionId);
            var inUse = s.Active && !s.Locked;

            Evaluation eval;
            lock (_rt.Gate)
            {
                var day = FamilyClock.Day(now, tz);
                var usage = _rt.State.UsageFor(child.ChildId, day);
                eval = TimeRules.Evaluate(child, usage, now, tz);
                if (inUse && eval.Allowed)
                {
                    track.Fraction += elapsed;
                    var whole = (long)track.Fraction;
                    if (whole > 0)
                    {
                        TimeRules.Charge(usage, eval, whole);
                        track.Fraction -= whole;
                        _rt.MarkDirty();
                    }
                    eval = TimeRules.Evaluate(child, usage, now, tz);
                }
            }

            try
            {
                BrowserPolicies.Apply(s.Sid, child.Web, forceBrowser);
            }
            catch (Exception ex)
            {
                if (forceBrowser) Log.Error($"Browser-Regeln für {s.UserName}", ex);
            }

            if (scanApps && !child.Paused && child.BlockedApps.Count > 0)
            {
                var killed = AppBlocker.Enforce(s.SessionId, child.BlockedApps);
                if (killed.Count > 0)
                {
                    lock (_rt.Gate) _rt.State.AddEvent(DeviceEventKind.AppBlocked, $"Gesperrtes Programm beendet: {string.Join(", ", killed)}", child.ChildId);
                    if (DateTime.UtcNow - track.LastBlockedNotice > TimeSpan.FromSeconds(30))
                    {
                        track.LastBlockedNotice = DateTime.UtcNow;
                        await _agents.SendAsync(s.SessionId, new IpcMessage { Type = IpcMessage.Notify, Title = "Programm gesperrt", Text = $"{string.Join(", ", killed)} ist von deinen Eltern gesperrt." });
                    }
                }
            }

            await ApplyAsync(s, child, eval, track, now);

            var lockShown = _agents.Get(s.SessionId)?.LockShown == true;
            if (inUse && eval.Allowed) active.Add(child.ChildId);
            if (lockShown) locked.Add(child.ChildId);
            long usedToday;
            lock (_rt.Gate) usedToday = _rt.State.UsageFor(child.ChildId, eval.Day).TotalSeconds;
            // With several sessions of the same child prefer the one in use.
            if (!live.TryGetValue(child.ChildId, out var prev) || (!prev.Active && inUse))
            {
                live[child.ChildId] = new ChildLiveStatus
                {
                    ChildId = child.ChildId, Allowed = eval.Allowed, RemainingSeconds = child.Paused ? null : eval.RemainingSeconds,
                    Reason = eval.Reason, UsedTodaySeconds = usedToday, Active = inUse && eval.Allowed, LockShown = lockShown, WindowsUser = s.UserName,
                    CurrentApp = inUse && DateTime.UtcNow - track.CurrentAppAt < TimeSpan.FromSeconds(15) ? track.CurrentApp : null,
                };
            }
        }

        lock (_tracksGate)
        {
            var liveSessions = monitored.Select(m => m.Session.SessionId).ToHashSet();
            foreach (var id in _tracks.Keys.Where(id => !liveSessions.Contains(id)).ToList()) _tracks.Remove(id);
        }

        lock (_rt.Gate)
        {
            _rt.ActiveChildren.Clear();
            _rt.ActiveChildren.UnionWith(active);
            _rt.LockedChildren.Clear();
            _rt.LockedChildren.UnionWith(locked);
            _rt.Live.Clear();
            foreach (var (k, v) in live) _rt.Live[k] = v;
            _rt.SaveIfNeeded();
        }
    }

    private async Task ApplyAsync(UserSession s, ChildPolicy child, Evaluation eval, Track track, DateTimeOffset now)
    {
        var agent = _agents.Get(s.SessionId);
        var connected = agent?.Connected == true;

        // Status for the tray, on change or every 5 s.
        if (connected && (track.LastAllowed != eval.Allowed || DateTime.UtcNow - track.LastStatus > TimeSpan.FromSeconds(5)))
        {
            track.LastStatus = DateTime.UtcNow;
            track.LastAllowed = eval.Allowed;
            await _agents.SendAsync(s.SessionId, new IpcMessage
            {
                Type = IpcMessage.Status, ChildName = child.Name, Allowed = eval.Allowed, Paused = child.Paused, RemainingSeconds = eval.RemainingSeconds,
            });
        }

        if (eval.Allowed)
        {
            if (track.BlockedSince is not null || track.LockRequested || agent?.LockShown == true)
            {
                Log.Info($"{child.Name}: wieder freigegeben ({eval.Source})");
                if (connected) await _agents.SendAsync(s.SessionId, new IpcMessage { Type = IpcMessage.HideLock });
                track.LockRequested = false;
                if (agent is not null) agent.LockShown = false;
                lock (_rt.Gate) _rt.State.AddEvent(DeviceEventKind.Unlocked, $"Freigegeben ({SourceText(eval.Source)})", child.ChildId);
            }
            track.BlockedSince = null;
            track.LogoffAt = null;
            track.ExpiredDuringUse = false;
            bool restore;
            lock (_rt.Gate) restore = track.Muted || _rt.State.AudioMutedByPaCo;
            if (restore && connected)
            {
                await _agents.SendAsync(s.SessionId, new IpcMessage { Type = IpcMessage.RestoreAudio });
                track.Muted = false;
                lock (_rt.Gate) { _rt.State.AudioMutedByPaCo = false; _rt.MarkDirty(); }
            }
            if (s.Active && !s.Locked) await WarnAsync(s, child, eval, track);
            track.WasAllowed = true;
            return;
        }

        // ---------- Blocked ----------
        if (track.BlockedSince is null)
        {
            track.BlockedSince = now;
            track.ExpiredDuringUse = track.WasAllowed;
            Log.Info($"{child.Name}: gesperrt ({eval.Reason}), Aktion {child.ExpiryAction}, während Nutzung={track.ExpiredDuringUse}");
        }
        track.WasAllowed = false;

        // Disconnected (fast user switching) or Win+L: nobody can use it; act when it becomes active again.
        if (!s.Active || s.Locked) return;

        var (title, text) = BlockText(child, eval);

        if (connected && !track.Muted)
        {
            await _agents.SendAsync(s.SessionId, new IpcMessage { Type = IpcMessage.Mute });
            track.Muted = true;
            lock (_rt.Gate) { _rt.State.AudioMutedByPaCo = true; _rt.MarkDirty(); }
        }

        if (child.ExpiryAction == ExpiryAction.LockScreen)
        {
            if (connected && !track.LockRequested)
            {
                await _agents.SendAsync(s.SessionId, new IpcMessage { Type = IpcMessage.ShowLock, Title = title, Text = text });
                track.LockRequested = true;
                lock (_rt.Gate) _rt.State.AddEvent(DeviceEventKind.Locked, $"{title} – Sperrbildschirm", child.ChildId);
            }
            else if (!connected && now - track.BlockedSince > AgentGrace)
            {
                await ForceLogoffAsync(s, child, track, "Sperrbildschirm konnte nicht angezeigt werden – Kind wurde abgemeldet.", failure: true);
            }
            return;
        }

        // ExpiryAction.Logoff
        if (track.LogoffAt is null)
        {
            if (track.ExpiredDuringUse)
            {
                track.LogoffAt = now + LogoffDelayAfterExpiry;
                if (connected) await _agents.SendAsync(s.SessionId, new IpcMessage { Type = IpcMessage.ShowLock, Title = title, Text = text, Seconds = (int)LogoffDelayAfterExpiry.TotalSeconds });
            }
            else
            {
                // Logged in although no time is left: short window to type an unlock code, then sign out.
                track.LogoffAt = now + LogoffCountdownAfterLogin;
                if (connected) await _agents.SendAsync(s.SessionId, new IpcMessage { Type = IpcMessage.ShowLock, Title = title, Text = text, Seconds = (int)LogoffCountdownAfterLogin.TotalSeconds });
            }
            track.LockRequested = connected;
        }
        else if (!track.LockRequested && connected)
        {
            var left = (int)Math.Max(1, (track.LogoffAt.Value - now).TotalSeconds);
            await _agents.SendAsync(s.SessionId, new IpcMessage { Type = IpcMessage.ShowLock, Title = title, Text = text, Seconds = left });
            track.LockRequested = true;
        }

        var deadline = track.LogoffAt.Value;
        if (!connected) deadline = Max(deadline, track.BlockedSince.Value + AgentGrace);
        if (now >= deadline)
            await ForceLogoffAsync(s, child, track, $"{title} – abgemeldet.", failure: false);
    }

    private async Task ForceLogoffAsync(UserSession s, ChildPolicy child, Track track, string message, bool failure)
    {
        Log.Warn($"Melde {s.UserName} (Sitzung {s.SessionId}) ab: {message}");
        lock (_rt.Gate) _rt.State.AddEvent(failure ? DeviceEventKind.AgentFailure : DeviceEventKind.ForcedLogoff, message, child.ChildId);
        if (_agents.Get(s.SessionId)?.Connected == true)
            await _agents.SendAsync(s.SessionId, new IpcMessage { Type = IpcMessage.Mute });
        if (!Sessions.Logoff(s.SessionId))
            Log.Error($"WTSLogoffSession fehlgeschlagen: {System.Runtime.InteropServices.Marshal.GetLastWin32Error()}");
        track.LogoffAt = null;
        track.BlockedSince = null;
        track.LockRequested = false;
        lock (_rt.Gate) _rt.SaveIfNeeded(force: true);
    }

    private async Task WarnAsync(UserSession s, ChildPolicy child, Evaluation eval, Track track)
    {
        if (eval.RemainingSeconds is not { } rem || child.Paused) return;
        if (track.WarnDay != eval.Day)
        {
            track.WarnDay = eval.Day;
            track.Warned.Clear();
        }
        foreach (var t in WarnAt)
        {
            if (rem > t || track.Warned.Contains(t)) continue;
            // Mark all larger thresholds too, so a fresh login at 2 min left gives one warning, not three.
            foreach (var u in WarnAt.Where(u => u >= t)) track.Warned.Add(u);
            var minutes = Math.Max(1, (rem + 59) / 60);
            var what = child.ExpiryAction == ExpiryAction.Logoff ? "wirst du abgemeldet" : "wird der Bildschirm gesperrt";
            await _agents.SendAsync(s.SessionId, new IpcMessage
            {
                Type = IpcMessage.Notify,
                Title = $"Noch {minutes} {(minutes == 1 ? "Minute" : "Minuten")}",
                Text = $"Danach {what}. Speichere jetzt dein Spiel oder deine Arbeit.",
            });
            Log.Info($"{child.Name}: Warnung {minutes} Min");
            break;
        }
        // If time was extended above a threshold again, allow warning again later.
        track.Warned.RemoveWhere(t => rem > t + 60);
    }

    private async Task OnAgentMessageAsync(int sessionId, IpcMessage msg)
    {
        Guid childId;
        lock (_tracksGate) childId = _tracks.GetValueOrDefault(sessionId)?.ChildId ?? Guid.Empty;
        var policy = _rt.Policies.Current;
        var child = policy?.Children.FirstOrDefault(c => c.ChildId == childId);

        switch (msg.Type)
        {
            case IpcMessage.Hello:
                Log.Info($"Agent verbunden: Sitzung {sessionId}, PID {msg.Seconds}");
                lock (_tracksGate)
                {
                    // Re-send state to a fresh agent.
                    if (_tracks.TryGetValue(sessionId, out var t)) { t.LockRequested = false; t.Muted = false; t.LastAllowed = null; }
                }
                break;

            case IpcMessage.Usage when child is not null && msg.Samples is { } samples && policy is not null:
                lock (_tracksGate)
                {
                    if (_tracks.TryGetValue(sessionId, out var tr) && samples.Count > 0)
                    {
                        tr.CurrentApp = samples.MaxBy(x => x.Seconds)!.DisplayName;
                        tr.CurrentAppAt = DateTime.UtcNow;
                    }
                }
                lock (_rt.Gate)
                {
                    var now = _rt.Now();
                    var usage = _rt.State.UsageFor(child.ChildId, FamilyClock.Day(now, FamilyClock.Resolve(policy.TimeZoneId)));
                    foreach (var sm in samples.Take(50))
                    {
                        var key = sm.Process.Length > 80 ? sm.Process[..80] : sm.Process;
                        if (!usage.Apps.TryGetValue(key, out var app))
                        {
                            if (usage.Apps.Count >= 300) continue;
                            usage.Apps[key] = app = new AppUsage();
                        }
                        app.DisplayName = sm.DisplayName.Length > 120 ? sm.DisplayName[..120] : sm.DisplayName;
                        app.Seconds += Math.Clamp(sm.Seconds, 0, 10);
                    }
                    _rt.MarkDirty();
                }
                break;

            case IpcMessage.Code when child is not null && policy is not null:
                var (ok, text) = RedeemCode(child, policy, msg.Text ?? "");
                await _agents.SendAsync(sessionId, new IpcMessage { Type = IpcMessage.CodeResult, Flag = ok, Text = text });
                break;

            case IpcMessage.LockState:
                var a = _agents.Get(sessionId);
                if (a is not null) a.LockShown = msg.Flag;
                break;

            case IpcMessage.RequestLogoff:
                Log.Info($"Kind hat Abmelden gewählt (Sitzung {sessionId})");
                Sessions.Logoff(sessionId);
                break;

            case IpcMessage.LogLine:
                Log.Warn($"Agent {sessionId}: {msg.Text}");
                break;
        }
    }

    private (bool Ok, string Text) RedeemCode(ChildPolicy child, PolicyDocument policy, string code)
    {
        lock (_rt.Gate)
        {
            var now = _rt.Now();
            if (!_rt.State.Throttles.TryGetValue(child.ChildId, out var throttle))
                _rt.State.Throttles[child.ChildId] = throttle = new UnlockThrottle();
            if (throttle.RemainingWait(now) is { } wait)
                return (false, $"Zu viele falsche Versuche. Bitte warte {Math.Ceiling(wait.TotalMinutes)} Minute(n).");

            var day = FamilyClock.Day(now, FamilyClock.Resolve(policy.TimeZoneId));
            if (!UnlockCodes.TryValidate(_rt.Device.UnlockKeyBytes, child.ChildId, day, code, out var decoded))
            {
                throttle.RegisterFailure(now);
                _rt.State.AddEvent(DeviceEventKind.UnlockFailed, "Falscher Freischaltcode eingegeben", child.ChildId);
                _rt.SaveIfNeeded(force: true);
                var left = UnlockThrottle.FreeAttempts - throttle.FailedAttempts;
                return (false, left > 0 ? $"Der Code stimmt nicht. Noch {left} Versuch(e)." : "Der Code stimmt nicht. Eingabe vorübergehend gesperrt.");
            }

            var usage = _rt.State.UsageFor(child.ChildId, day);
            if (usage.Codes.Any(c => c.Nonce == decoded.Nonce))
                return (false, "Dieser Code wurde schon benutzt.");

            usage.Codes.Add(new RedeemedCode { Nonce = decoded.Nonce, Minutes = decoded.Minutes, RedeemedUtc = now });
            throttle.RegisterSuccess();
            var what = decoded.Minutes == 0 ? "für den Rest des Tages" : $"für {decoded.Minutes} Minuten";
            _rt.State.AddEvent(DeviceEventKind.Unlocked, $"Freischaltcode eingelöst: {what}", child.ChildId);
            _rt.SaveIfNeeded(force: true);
            return (true, $"Freigeschaltet {what}!");
        }
    }

    private void HandleRevoked(PolicyDocument policy)
    {
        _agents.Retain(new HashSet<int>());
        if (_revokedHandled) return;
        _revokedHandled = true;
        Log.Warn("Gerät wurde auf der Website entfernt – keine Überwachung mehr.");
        foreach (var s in Sessions.Enumerate())
        {
            try { BrowserPolicies.Remove(s.Sid); } catch (Exception ex) { Log.Error("Browser-Regeln entfernen", ex); }
        }
    }

    private static (string Title, string Text) BlockText(ChildPolicy child, Evaluation eval)
    {
        var rule = child.Days.Count == 7 ? child.Days[(int)eval.Day.DayOfWeek] : DayRule.Unrestricted;
        return eval.Reason switch
        {
            BlockReason.LockedByParent => ("Der PC ist gerade gesperrt", $"{child.Name}, deine Eltern haben den PC gesperrt."),
            BlockReason.OutsideWindow => ("Jetzt ist keine PC-Zeit",
                rule.WindowStart is { } a && rule.WindowEnd is { } b
                    ? $"{child.Name}, heute darfst du von {a:HH\\:mm} bis {b:HH\\:mm} Uhr an den PC."
                    : $"{child.Name}, jetzt ist keine PC-Zeit."),
            _ => ("Deine Bildschirmzeit ist aufgebraucht", $"{child.Name}, für heute ist deine Zeit vorbei. Morgen geht es weiter!"),
        };
    }

    private static string SourceText(TimeSource s) => s switch
    {
        TimeSource.Grant => "Zusatzzeit von den Eltern",
        TimeSource.Code => "Freischaltcode",
        TimeSource.Unrestricted => "Überwachung pausiert",
        _ => "reguläre Zeit",
    };

    private static DateTimeOffset Max(DateTimeOffset a, DateTimeOffset b) => a > b ? a : b;
}
