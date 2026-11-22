using System.Diagnostics;
using Microsoft.Extensions.Hosting;
using PaCo.Client.Config;
using PaCo.Client.Native;
using PaCo.Shared;
using PaCo.Shared.I18n;

namespace PaCo.Client.Service;

/// <summary>
/// The enforcement loop (1 Hz). Fail-closed: if the session agent cannot be started or connected, a blocked
/// child is signed out instead of being let through.
/// </summary>
internal sealed class Enforcer : BackgroundService
{
    /// <summary>Two warnings before the time runs out: 5 minutes and 1 minute.</summary>
    private static readonly int[] WarnAt = [300, 60];
    private static readonly TimeSpan AgentGrace = TimeSpan.FromSeconds(25);
    private static readonly TimeSpan LogoffCountdownAfterLogin = TimeSpan.FromSeconds(60);
    private static readonly TimeSpan LogoffDelayAfterExpiry = TimeSpan.FromSeconds(4);

    private sealed class Track
    {
        public bool WasAllowed;
        public DateTimeOffset? BlockedSince;
        public bool ExpiredDuringUse;
        public bool LockRequested;
        public TimeSpan LockRequestedAt = Mono.Never;
        public bool Muted;
        public DateTimeOffset? LogoffAt;
        public double Fraction;
        public DateOnly WarnDay;
        public HashSet<int> Warned = [];
        public TimeSpan LastStatus = Mono.Never;
        public bool? LastAllowed;
        public Guid ChildId;
        public TimeSpan LastBlockedNotice = Mono.Never;
        public string? CurrentApp;
        public TimeSpan CurrentAppAt = Mono.Never;
        public string? SentAlertConfig;
        public TimeSpan LastHelpHint = Mono.Never;
        public string? FocusProcess;
        public string? FocusDescription;
        public string? FocusHost;
        public TimeSpan FocusAt = Mono.Never;
        public HashSet<string> GroupWarned = [];
        public DateOnly GroupWarnDay;
        public TimeSpan LastTabClose = Mono.Never;
        public string? FocusUrl;
        /// <summary>Learning mode: time used up, but learning apps/sites (and the parents' aids) stay usable.</summary>
        public bool Learning;
        public bool BrowsersRestarted;
        public DateOnly BrowserRestartDay;
        public HashSet<Guid> BrowserRestartedFor = [];
        public TimeSpan LastLearningNotice = Mono.Never;
    }

    /// <summary>Groups of the current foreground app/site (empty if unknown or stale).</summary>
    private static List<TimeGroup> FocusGroups(ChildPolicy child, Track track, bool inUse) =>
        inUse && child.Groups.Count > 0 && Mono.Now - track.FocusAt < TimeSpan.FromSeconds(12)
            ? TimeGroups.Matching(child.Groups, track.FocusProcess, track.FocusDescription, track.FocusHost, track.FocusUrl)
            : [];

    /// <summary>Last report time per child|category|keyword|app – the same hit is reported at most every 30 min.</summary>
    private readonly Dictionary<string, TimeSpan> _alertDedup = [];

    private readonly Runtime _rt;
    private readonly AgentManager _agents;
    private readonly Dictionary<int, Track> _tracks = [];
    private readonly Lock _tracksGate = new();
    private TimeSpan _lastBrowserForce = Mono.Never;
    private TimeSpan _lastAppScan = Mono.Never;
    private bool _revokedHandled;

    public Enforcer(Runtime rt)
    {
        _rt = rt;
        _agents = new AgentManager(OnAgentMessageAsync, (sessionId, message) =>
        {
            Guid? child;
            lock (_tracksGate) child = _tracks.GetValueOrDefault(sessionId)?.ChildId;
            lock (_rt.Gate) _rt.State.AddEvent(DeviceEventKind.AgentCrashed, message, child);
        });
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

    private TimeSpan _lastUnmanagedCheck = Mono.Never;

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

        // Accounts that are no longer child accounts (re-assigned on the website): take PaCo's policies away again.
        if (Mono.Now - _lastUnmanagedCheck > TimeSpan.FromMinutes(1))
        {
            _lastUnmanagedCheck = Mono.Now;
            foreach (var s in sessions.Where(s => policy.ChildForSid(s.Sid) is null))
            {
                try
                {
                    if (!AccountPolicies.IsManaged(s.Sid)) continue;
                    AccountPolicies.Remove(s.Sid);
                    BrowserPolicies.Remove(s.Sid);
                }
                catch (Exception ex) { Log.Error($"Richtlinien für {s.UserName} entfernen", ex); }
            }
        }

        DateTimeOffset now;
        lock (_rt.Gate) now = _rt.Now();

        var forceBrowser = Mono.Now - _lastBrowserForce > TimeSpan.FromMinutes(1);
        if (forceBrowser) _lastBrowserForce = Mono.Now;
        var scanApps = Mono.Now - _lastAppScan > TimeSpan.FromSeconds(2);
        if (scanApps) _lastAppScan = Mono.Now;

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
                        // Learning time (a "free" group in the foreground) uses up no PC time; every group the
                        // foreground app/site belongs to counts its own time.
                        var groups = FocusGroups(child, track, inUse);
                        if (!groups.Any(g => g.Kind == TimeGroupKind.Free)) TimeRules.Charge(usage, eval, whole);
                        foreach (var g in groups) usage.Groups[g.Id] = usage.Groups.GetValueOrDefault(g.Id) + whole;
                        track.Fraction -= whole;
                        _rt.MarkDirty();
                    }
                    eval = TimeRules.Evaluate(child, usage, now, tz);
                }
            }

            // Groups whose time is used up today: their programs are closed, their sites blocked until midnight.
            var exhausted = new List<TimeGroup>();
            if (!child.Paused && child.Groups.Count > 0)
            {
                lock (_rt.Gate)
                {
                    var usageNow = _rt.State.UsageFor(child.ChildId, eval.Day);
                    exhausted = child.Groups.Where(g => TimeGroups.SecondsLeft(g, usageNow, child.GroupSecondsElsewhere, eval.Day) == 0).ToList();
                }
                await GroupNoticesAsync(s, child, track, eval, inUse, exhausted);
            }
            if (exhausted.Any(g => g.Sites.Count > 0) && s.Active && !s.Locked) RestartBrowsersFor(s, child, track, exhausted, eval.Day);
            var web = exhausted.Count == 0 ? child.Web
                : child.Web with { BlockedSites = child.Web.BlockedSites.Concat(exhausted.SelectMany(g => g.Sites)).Distinct(StringComparer.OrdinalIgnoreCase).ToList() };
            // Learning mode: only learning sites and the single pages/videos the parents allowed open in the browser.
            if (track.Learning) web = web with { AllowOnlySites = TimeGroups.LearningAllowList(child) };
            if (forceBrowser) LearningLinks.Sync(s.SessionId, child);
            var blockedApps = exhausted.Count == 0 ? child.BlockedApps
                : child.BlockedApps.Concat(exhausted.SelectMany(g => g.Apps)).Distinct(StringComparer.OrdinalIgnoreCase).ToList();

            try
            {
                BrowserPolicies.Apply(s.Sid, web, forceBrowser);
                AccountPolicies.Apply(s.Sid, child, forceBrowser);
            }
            catch (Exception ex)
            {
                if (forceBrowser) Log.Error($"Browser-Regeln für {s.UserName}", ex);
            }

            if (scanApps && !child.Paused && blockedApps.Count > 0)
            {
                var killed = AppBlocker.Enforce(s.SessionId, blockedApps);
                if (killed.Count > 0)
                {
                    lock (_rt.Gate) _rt.State.AddEvent(DeviceEventKind.AppBlocked, LocText.Of("Gesperrtes Programm beendet: {0}", string.Join(", ", killed)), child.ChildId);
                    if (Mono.Now - track.LastBlockedNotice > TimeSpan.FromSeconds(30))
                    {
                        track.LastBlockedNotice = Mono.Now;
                        await _agents.SendAsync(s.SessionId, new IpcMessage { Type = IpcMessage.Notify, Title = L.In(child.Language, "Programm gesperrt"), Text = L.In(child.Language, "{0} ist von deinen Eltern gesperrt.", string.Join(", ", killed)) });
                    }
                }
            }

            // Alarm keywords → agent (on change or after an agent restart).
            var alertJson = PaCoJson.Serialize(child.Alerts);
            if (_agents.Get(s.SessionId)?.Connected == true && track.SentAlertConfig != alertJson)
            {
                await _agents.SendAsync(s.SessionId, new IpcMessage { Type = IpcMessage.AlertConfig, Text = alertJson });
                track.SentAlertConfig = alertJson;
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
                    CurrentApp = inUse && Mono.Now - track.CurrentAppAt < TimeSpan.FromSeconds(15) ? track.CurrentApp : null,
                };
            }
        }

        // Screenshot requests: capture in the child's active session, otherwise report "not available".
        while (_rt.ScreenshotRequests.TryDequeue(out var req))
        {
            var target = monitored.FirstOrDefault(m => m.Child.ChildId == req.ChildId && m.Session.Active && !m.Session.Locked);
            if (target.Child is not null && _agents.Get(target.Session.SessionId)?.Connected == true)
            {
                Log.Info($"Bildschirmfoto angefordert für {target.Child.Name}");
                await _agents.SendAsync(target.Session.SessionId, new IpcMessage
                {
                    Type = IpcMessage.TakeScreenshot, RequestId = req.RequestId, Flag = target.Child.NotifyChildOnScreenshot,
                });
            }
            else
            {
                _rt.ScreenshotUploads.Enqueue((req.RequestId, []));
                _rt.RequestSyncNow();
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
        if (connected && (track.LastAllowed != eval.Allowed || Mono.Now - track.LastStatus > TimeSpan.FromSeconds(5)))
        {
            track.LastStatus = Mono.Now;
            track.LastAllowed = eval.Allowed;
            await _agents.SendAsync(s.SessionId, new IpcMessage
            {
                Type = IpcMessage.Status, ChildName = child.Name, Lang = child.Language, Allowed = eval.Allowed, Paused = child.Paused, RemainingSeconds = eval.RemainingSeconds,
            });
        }

        if (eval.Allowed)
        {
            if (track.Learning)
            {
                track.Learning = false;
                if (connected) await _agents.SendAsync(s.SessionId, new IpcMessage { Type = IpcMessage.LearningMode, Flag = false });
            }
            if (track.BlockedSince is not null || track.LockRequested || agent?.LockShown == true)
            {
                Log.Info($"{child.Name}: wieder freigegeben ({eval.Source})");
                if (connected) await _agents.SendAsync(s.SessionId, new IpcMessage { Type = IpcMessage.HideLock });
                track.LockRequested = false;
                if (agent is not null) agent.LockShown = false;
                lock (_rt.Gate) _rt.State.AddEvent(DeviceEventKind.Unlocked, SourceText(eval.Source), child.ChildId);
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

        // Learning mode needs a working agent (it reports the windows); otherwise back to the normal lock (fail closed).
        var learnable = connected && TimeGroups.LearningModeAvailable(child, eval);
        if (track.Learning && !learnable)
        {
            track.Learning = false;
            track.LockRequested = false;
            track.BlockedSince = now; // fresh grace for the lock screen, e.g. while the agent restarts
            if (connected) await _agents.SendAsync(s.SessionId, new IpcMessage { Type = IpcMessage.LearningMode, Flag = false });
            Log.Info($"{child.Name}: Lernmodus beendet ({eval.Reason})");
        }
        if (track.Learning)
        {
            await LearningTickAsync(s, child, track, agent!);
            return;
        }

        var (title, text) = BlockText(child, eval);
        var lang = child.Language;
        var learning = learnable ? LearningLabel(child) : null;

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
                await _agents.SendAsync(s.SessionId, new IpcMessage { Type = IpcMessage.ShowLock, Lang = lang, Title = title, Text = text, Learning = learning });
                track.LockRequested = true;
                track.LockRequestedAt = Mono.Now;
                lock (_rt.Gate) _rt.State.AddEvent(DeviceEventKind.Locked, EventText(eval.Reason, logoff: false), child.ChildId);
            }
            else if (connected && track.LockRequested && agent?.LockShown != true && Mono.Since(track.LockRequestedAt) > LockConfirmTimeout)
            {
                // The agent is connected but could not show (or keep) the lock screen: sign out instead (fail closed).
                await ForceLogoffAsync(s, child, track, LocText.Of("Sperrbildschirm konnte nicht angezeigt werden – Kind wurde abgemeldet."), failure: true);
            }
            else if (!connected && now - track.BlockedSince > AgentGrace)
            {
                await ForceLogoffAsync(s, child, track, LocText.Of("Sperrbildschirm konnte nicht angezeigt werden – Kind wurde abgemeldet."), failure: true);
            }
            return;
        }

        // ExpiryAction.Logoff
        if (track.LogoffAt is null)
        {
            if (track.ExpiredDuringUse)
            {
                track.LogoffAt = now + LogoffDelayAfterExpiry;
                if (connected) await _agents.SendAsync(s.SessionId, new IpcMessage { Type = IpcMessage.ShowLock, Lang = lang, Title = title, Text = text, Learning = learning, Seconds = (int)LogoffDelayAfterExpiry.TotalSeconds });
            }
            else
            {
                // Logged in although no time is left: short window to type an unlock code, then sign out.
                track.LogoffAt = now + LogoffCountdownAfterLogin;
                if (connected) await _agents.SendAsync(s.SessionId, new IpcMessage { Type = IpcMessage.ShowLock, Lang = lang, Title = title, Text = text, Learning = learning, Seconds = (int)LogoffCountdownAfterLogin.TotalSeconds });
            }
            track.LockRequested = connected;
        }
        else if (!track.LockRequested && connected)
        {
            var left = (int)Math.Max(1, (track.LogoffAt.Value - now).TotalSeconds);
            await _agents.SendAsync(s.SessionId, new IpcMessage { Type = IpcMessage.ShowLock, Lang = lang, Title = title, Text = text, Learning = learning, Seconds = left });
            track.LockRequested = true;
        }

        var deadline = track.LogoffAt.Value;
        if (!connected) deadline = Max(deadline, track.BlockedSince.Value + AgentGrace);
        if (now >= deadline)
            await ForceLogoffAsync(s, child, track, EventText(eval.Reason, logoff: true), failure: false);
    }

    private async Task ForceLogoffAsync(UserSession s, ChildPolicy child, Track track, LocText message, bool failure)
    {
        Log.Warn($"Melde {s.UserName} (Sitzung {s.SessionId}) ab: {message.German}");
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

    /// <summary>Warnings 5 and 1 minute before a group's time runs out; closes the tab of a used-up site.</summary>
    private async Task GroupNoticesAsync(UserSession s, ChildPolicy child, Track track, Evaluation eval, bool inUse, List<TimeGroup> exhausted)
    {
        if (_agents.Get(s.SessionId)?.Connected != true) return;
        var lang = child.Language;
        if (track.GroupWarnDay != eval.Day)
        {
            track.GroupWarnDay = eval.Day;
            track.GroupWarned.Clear();
        }
        foreach (var g in FocusGroups(child, track, inUse).Where(g => g.Kind == TimeGroupKind.Limit))
        {
            long? left;
            lock (_rt.Gate) left = TimeGroups.SecondsLeft(g, _rt.State.UsageFor(child.ChildId, eval.Day), child.GroupSecondsElsewhere, eval.Day);
            if (left is not { } l) continue;
            int[] thresholds = [300, 60];
            foreach (var t in thresholds)
            {
                if (l > t || l == 0 || track.GroupWarned.Contains($"{g.Id}|{t}")) continue;
                // One warning per threshold reached; a start at 50 s left marks both thresholds at once.
                foreach (var u in thresholds.Where(u => l <= u)) track.GroupWarned.Add($"{g.Id}|{u}");
                await _agents.SendAsync(s.SessionId, new IpcMessage
                {
                    Type = IpcMessage.Notify,
                    Title = L.In(lang, "Noch {0} Min für „{1}“", Math.Max(1, (l + 59) / 60), g.Name),
                    Text = L.In(lang, "Danach ist „{0}“ für heute vorbei.", g.Name),
                });
                break;
            }
        }
        // Used-up site still open in the active tab → close the tab (Chromium browsers also get the site blocked).
        if (inUse && track.FocusHost is { } host && Mono.Now - track.FocusAt < TimeSpan.FromSeconds(12)
            && exhausted.FirstOrDefault(g => g.Sites.Any(site => TimeGroups.SiteMatches(host, site))) is { } done
            && Mono.Now - track.LastTabClose > TimeSpan.FromSeconds(5))
        {
            track.LastTabClose = Mono.Now;
            var site = done.Sites.First(x => TimeGroups.SiteMatches(host, x));
            await _agents.SendAsync(s.SessionId, new IpcMessage { Type = IpcMessage.CloseTab, Host = site });
            await _agents.SendAsync(s.SessionId, new IpcMessage
            {
                Type = IpcMessage.Notify,
                Title = L.In(lang, "„{0}“ ist für heute vorbei", done.Name),
                Text = L.In(lang, "Deine Zeit für „{0}“ ist für heute aufgebraucht.", done.Name),
            });
            lock (_rt.Gate) _rt.State.AddEvent(DeviceEventKind.AppBlocked, LocText.Of("Zeit für „{0}“ aufgebraucht – {1} geschlossen", done.Name, host), child.ChildId);
        }
    }

    /// <summary>The agent must confirm the lock screen within this time, otherwise the child is signed out.</summary>
    private static readonly TimeSpan LockConfirmTimeout = TimeSpan.FromSeconds(20);

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
            var lang = child.Language;
            await _agents.SendAsync(s.SessionId, new IpcMessage
            {
                Type = IpcMessage.Notify,
                Title = minutes == 1 ? L.In(lang, "Noch 1 Minute") : L.In(lang, "Noch {0} Minuten", minutes),
                Text = child.ExpiryAction == ExpiryAction.Logoff
                    ? L.In(lang, "Danach wirst du abgemeldet. Speichere jetzt dein Spiel oder deine Arbeit.")
                    : L.In(lang, "Danach wird der Bildschirm gesperrt. Speichere jetzt dein Spiel oder deine Arbeit."),
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
                    if (_tracks.TryGetValue(sessionId, out var t)) { t.LockRequested = false; t.Muted = false; t.LastAllowed = null; t.SentAlertConfig = null; }
                }
                break;

            case IpcMessage.Usage when child is not null && msg.Samples is { } rawSamples && policy is not null:
                var samples = rawSamples.Where(x => x is not null && !string.IsNullOrEmpty(x.Process) && x.DisplayName is not null).Take(50).ToList();
                lock (_tracksGate)
                {
                    if (_tracks.TryGetValue(sessionId, out var tr) && samples.Count > 0)
                    {
                        tr.CurrentApp = samples.MaxBy(x => x.Seconds)!.DisplayName;
                        tr.CurrentAppAt = Mono.Now;
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

            case IpcMessage.Focus:
                lock (_tracksGate)
                {
                    if (_tracks.TryGetValue(sessionId, out var ft))
                    {
                        ft.FocusProcess = Cut(msg.Process ?? "", 80) is { Length: > 0 } p ? p.ToLowerInvariant() : null;
                        ft.FocusDescription = msg.Description is null ? null : Cut(msg.Description, 120);
                        ft.FocusHost = msg.Host is null ? null : Cut(msg.Host, 253).ToLowerInvariant();
                        ft.FocusUrl = msg.Url is null || ft.FocusHost is null ? null : Cut(msg.Url, 2000);
                        ft.FocusAt = Mono.Now;
                    }
                }
                break;

            case IpcMessage.Code when child is not null && policy is not null:
                var (ok, text) = RedeemCode(child, policy, msg.Text ?? "");
                await _agents.SendAsync(sessionId, new IpcMessage { Type = IpcMessage.CodeResult, Flag = ok, Text = text });
                break;

            case IpcMessage.StartLearning when child is not null && policy is not null:
                await StartLearningAsync(sessionId, child, policy);
                break;

            case IpcMessage.Windows when child is not null && msg.WindowProcesses is { } windows:
                await CloseNonLearningAsync(sessionId, child, windows);
                break;

            case IpcMessage.LockState:
                var a = _agents.Get(sessionId);
                if (a is not null) a.LockShown = msg.Flag;
                break;

            case IpcMessage.AlertHits when child is not null && msg.Hits is { } rawHits:
                var hits = rawHits.Where(h => h is not null && Enum.IsDefined(h.Category) && h.Keyword is not null && h.App is not null
                    && h.Snippet is not null && h.Source is not null).Take(20).ToList();
                if (hits.Count > 0) await HandleAlertsAsync(sessionId, child, hits);
                break;

            case IpcMessage.ScreenshotData when msg.RequestId is { } rid:
                byte[] jpeg;
                try { jpeg = Convert.FromBase64String(msg.Text ?? ""); } catch (FormatException) { jpeg = []; }
                Log.Info($"Bildschirmfoto aufgenommen ({jpeg.Length / 1024} KB)");
                _rt.ScreenshotUploads.Enqueue((rid, jpeg));
                _rt.RequestSyncNow();
                break;

            case IpcMessage.RequestLogoff:
                Log.Info($"Kind hat Abmelden gewählt (Sitzung {sessionId})");
                Sessions.Logoff(sessionId);
                break;

            case IpcMessage.LogLine:
                var line = Cut(msg.Text ?? "", 500);
                Log.Warn($"Agent {sessionId}: {line}");
                _rt.ReportError($"Sitzungsprogramm: {line}");
                break;
        }
    }

    /// <summary>
    /// A group with websites is used up: its sites are blocked from now on, but a tab in the background (music, audio
    /// book) would keep playing. The browser is restarted once per group and day; restored tabs then hit the block.
    /// </summary>
    private void RestartBrowsersFor(UserSession s, ChildPolicy child, Track track, List<TimeGroup> exhausted, DateOnly day)
    {
        if (track.BrowserRestartDay != day)
        {
            track.BrowserRestartDay = day;
            track.BrowserRestartedFor.Clear();
        }
        var fresh = exhausted.Where(g => g.Sites.Count > 0 && track.BrowserRestartedFor.Add(g.Id)).ToList();
        if (fresh.Count == 0 || Sessions.SidOf(s.SessionId) is not { } sid) return;
        var closed = LearningEnforcer.KillBrowsers(s.SessionId, sid);
        if (closed.Count == 0) return;
        var names = string.Join(", ", fresh.Select(g => g.Name));
        Log.Info($"{child.Name}: Browser beendet, Zeit für {names} aufgebraucht");
        lock (_rt.Gate) _rt.State.AddEvent(DeviceEventKind.AppBlocked, LocText.Of("Zeit für „{0}“ aufgebraucht – Browser neu gestartet, damit nichts im Hintergrund weiterläuft", names), child.ChildId);
        _ = _agents.SendAsync(s.SessionId, new IpcMessage
        {
            Type = IpcMessage.Notify,
            Title = L.In(child.Language, "„{0}“ ist für heute vorbei", names),
            Text = L.In(child.Language, "Der Browser wurde geschlossen. Du kannst ihn wieder öffnen – nur diese Seiten bleiben für heute gesperrt."),
        });
    }

    /// <summary>The child pressed "Lernen": re-checked here – the lock screen only asks.</summary>
    private async Task StartLearningAsync(int sessionId, ChildPolicy child, PolicyDocument policy)
    {
        Evaluation eval;
        lock (_rt.Gate)
        {
            var now = _rt.Now();
            var tz = FamilyClock.Resolve(policy.TimeZoneId);
            eval = TimeRules.Evaluate(child, _rt.State.UsageFor(child.ChildId, FamilyClock.Day(now, tz)), now, tz);
        }
        Track? track;
        lock (_tracksGate) _tracks.TryGetValue(sessionId, out track);
        if (track is null || _agents.Get(sessionId)?.Connected != true || !TimeGroups.LearningModeAvailable(child, eval)) return;

        track.Learning = true;
        track.BrowsersRestarted = false;
        track.LockRequested = false;
        track.LogoffAt = null;
        Log.Info($"{child.Name}: Lernmodus gestartet");
        lock (_rt.Gate)
        {
            _rt.State.AddEvent(DeviceEventKind.Info, LocText.Of("Lernmodus gestartet – nur Lern-Apps, Lern-Websites und Hilfsmittel"), child.ChildId);
            _rt.State.AudioMutedByPaCo = false;
            _rt.MarkDirty();
        }
        await _agents.SendAsync(sessionId, new IpcMessage { Type = IpcMessage.LearningMode, Flag = true });
        await _agents.SendAsync(sessionId, new IpcMessage { Type = IpcMessage.HideLock });
        await _agents.SendAsync(sessionId, new IpcMessage { Type = IpcMessage.RestoreAudio });
        track.Muted = false;
        await _agents.SendAsync(sessionId, new IpcMessage
        {
            Type = IpcMessage.Notify,
            Title = L.In(child.Language, "Lernmodus"),
            Text = L.In(child.Language, "Jetzt gehen nur deine Lern-Apps, Lern-Websites und Hilfsmittel. Alles andere wird geschlossen. Freigegebene Seiten und Videos findest du im Ordner „Lernen“ auf dem Desktop."),
        });
    }

    /// <summary>Learning mode, every tick: lock screen stays hidden; a tab with a page that is not allowed is closed.</summary>
    private async Task LearningTickAsync(UserSession s, ChildPolicy child, Track track, AgentConnection agent)
    {
        if (agent.LockShown) await _agents.SendAsync(s.SessionId, new IpcMessage { Type = IpcMessage.HideLock });
        if (track.FocusHost is { } host && Mono.Now - track.FocusAt < TimeSpan.FromSeconds(12)
            && !TimeGroups.IsLearningAddress(child, host, track.FocusUrl) && Mono.Now - track.LastTabClose > TimeSpan.FromSeconds(5))
        {
            track.LastTabClose = Mono.Now;
            await _agents.SendAsync(s.SessionId, new IpcMessage { Type = IpcMessage.CloseTab, Url = track.FocusUrl });
            await LearningNoticeAsync(s.SessionId, child, track, L.In(child.Language, "Diese Seite ist im Lernmodus nicht erlaubt."));
        }
    }

    /// <summary>Learning mode: closes programs with windows that are neither learning apps nor aids.</summary>
    private async Task CloseNonLearningAsync(int sessionId, ChildPolicy child, IReadOnlyList<WindowProcess> windows)
    {
        Track? track;
        lock (_tracksGate) _tracks.TryGetValue(sessionId, out track);
        if (track is not { Learning: true }) return;
        var sid = Sessions.SidOf(sessionId);
        if (sid is null) return;
        var browsersOk = TimeGroups.LearningSites(child).Count + TimeGroups.LearningPages(child).Count > 0;
        var first = !track.BrowsersRestarted;
        var closed = LearningEnforcer.Close(sessionId, sid, windows.Where(w => w is not null).Take(200), child, browsersOk, first);
        // Hidden players (music, audio books) and – once at the start – all browsers, so no old tab keeps playing.
        closed.AddRange(LearningEnforcer.CloseBackground(sessionId, sid, child, first));
        closed = closed.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        track.BrowsersRestarted = true;
        if (closed.Count == 0) return;
        lock (_rt.Gate) _rt.State.AddEvent(DeviceEventKind.AppBlocked, LocText.Of("Im Lernmodus geschlossen: {0}", string.Join(", ", closed)), child.ChildId);
        await LearningNoticeAsync(sessionId, child, track, L.In(child.Language, "{0} ist im Lernmodus nicht erlaubt.", string.Join(", ", closed)));
    }

    private async Task LearningNoticeAsync(int sessionId, ChildPolicy child, Track track, string text)
    {
        if (Mono.Now - track.LastLearningNotice < TimeSpan.FromSeconds(20)) return;
        track.LastLearningNotice = Mono.Now;
        await _agents.SendAsync(sessionId, new IpcMessage { Type = IpcMessage.Notify, Title = L.In(child.Language, "Nur zum Lernen"), Text = text });
    }

    /// <summary>Names of the learning groups for the lock screen ("Lernen geht weiter: Mathe, Englisch").</summary>
    private static string LearningLabel(ChildPolicy child)
    {
        var names = string.Join(", ", TimeGroups.LearningGroups(child).Select(g => g.Name));
        return names.Length <= 120 ? names : names[..117] + "…";
    }

    private async Task HandleAlertsAsync(int sessionId, ChildPolicy child, IReadOnlyList<AlertHitDto> hits)
    {
        var fresh = new List<AlertReport>();
        lock (_rt.Gate)
        {
            var now = _rt.Now();
            foreach (var h in hits.Take(20))
            {
                var key = $"{child.ChildId:N}|{h.Category}|{h.Keyword}|{h.App}".ToLowerInvariant();
                if (_alertDedup.TryGetValue(key, out var last) && Mono.Since(last) < TimeSpan.FromMinutes(30)) continue;
                _alertDedup[key] = Mono.Now;
                var report = new AlertReport
                {
                    ChildId = child.ChildId, TimeUtc = now, Category = h.Category, Keyword = Cut(h.Keyword, 100), App = Cut(h.App, 80),
                    WindowTitle = h.Title is null ? null : Cut(h.Title, 200), Snippet = Cut(h.Snippet, 300), Source = Cut(h.Source, 40),
                };
                _rt.State.PendingAlerts.Add(report);
                fresh.Add(report);
            }
            if (_rt.State.PendingAlerts.Count > 300) _rt.State.PendingAlerts.RemoveRange(0, _rt.State.PendingAlerts.Count - 300);
            if (_alertDedup.Count > 2000) _alertDedup.Clear();
            if (fresh.Count > 0) _rt.SaveIfNeeded(force: true);
        }
        if (fresh.Count == 0) return;
        Log.Warn($"Alarm für {child.Name}: {string.Join(", ", fresh.Select(f => $"{f.Category}/{f.Keyword} in {f.App}"))}");
        _rt.RequestSyncNow();

        // Self-harm: gently show the child where to get help (at most every 30 minutes).
        if (child.Alerts.ShowHelpToChild && fresh.Any(f => f.Category == AlertCategory.SelfHarm))
        {
            Track? track;
            lock (_tracksGate) _tracks.TryGetValue(sessionId, out track);
            if (track is not null && Mono.Since(track.LastHelpHint) > TimeSpan.FromMinutes(30))
            {
                track.LastHelpHint = Mono.Now;
                await _agents.SendAsync(sessionId, new IpcMessage { Type = IpcMessage.Notify, Title = L.In(child.Language, "Du bist nicht allein 💙"), Text = SharedText.T(child.Language, AlertKeywords.HelpText) });
            }
        }
    }

    private static string Cut(string s, int max) => s.Length <= max ? s : s[..max];

    private (bool Ok, string Text) RedeemCode(ChildPolicy child, PolicyDocument policy, string code)
    {
        lock (_rt.Gate)
        {
            var now = _rt.Now();
            if (!_rt.State.Throttles.TryGetValue(child.ChildId, out var throttle))
                _rt.State.Throttles[child.ChildId] = throttle = new UnlockThrottle();
            if (throttle.RemainingWait(now) is { } wait)
                return (false, L.In(child.Language, "Zu viele falsche Versuche. Bitte warte {0} Minute(n).", Math.Ceiling(wait.TotalMinutes)));

            var day = FamilyClock.Day(now, FamilyClock.Resolve(policy.TimeZoneId));
            var key = string.IsNullOrEmpty(child.UnlockKey) ? _rt.Device.UnlockKeyBytes : Convert.FromBase64String(child.UnlockKey);
            if (!UnlockCodes.TryValidate(key, child.ChildId, day, code, out var decoded))
            {
                throttle.RegisterFailure(now);
                _rt.State.AddEvent(DeviceEventKind.UnlockFailed, LocText.Of("Falscher Freischaltcode eingegeben"), child.ChildId);
                _rt.SaveIfNeeded(force: true);
                var left = UnlockThrottle.FreeAttempts - throttle.FailedAttempts;
                return (false, left > 0 ? L.In(child.Language, "Der Code stimmt nicht. Noch {0} Versuch(e).", left) : L.In(child.Language, "Der Code stimmt nicht. Eingabe vorübergehend gesperrt."));
            }

            var usage = _rt.State.UsageFor(child.ChildId, day);
            if (usage.Codes.Any(c => c.Nonce == decoded.Nonce))
                return (false, L.In(child.Language, "Dieser Code wurde schon benutzt."));

            usage.Codes.Add(new RedeemedCode { Nonce = decoded.Nonce, Minutes = decoded.Minutes, RedeemedUtc = now });
            throttle.RegisterSuccess();
            _rt.State.AddEvent(DeviceEventKind.Unlocked, decoded.Minutes == 0
                ? LocText.Of("Freischaltcode eingelöst: für den Rest des Tages")
                : LocText.Of("Freischaltcode eingelöst: für {0} Minuten", decoded.Minutes), child.ChildId);
            _rt.SaveIfNeeded(force: true);
            return (true, decoded.Minutes == 0
                ? L.In(child.Language, "Freigeschaltet für den Rest des Tages!")
                : L.In(child.Language, "Freigeschaltet für {0} Minuten!", decoded.Minutes));
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
            try { AccountPolicies.Remove(s.Sid); } catch (Exception ex) { Log.Error("Konto-Richtlinien entfernen", ex); }
        }
    }

    private static (string Title, string Text) BlockText(ChildPolicy child, Evaluation eval)
    {
        var rule = child.Days.Count == 7 ? child.Days[(int)eval.Day.DayOfWeek] : DayRule.Unrestricted;
        var l = child.Language;
        var culture = Languages.Culture(l);
        return eval.Reason switch
        {
            BlockReason.LockedByParent => (L.In(l, "Der PC ist gerade gesperrt"), L.In(l, "{0}, deine Eltern haben den PC gesperrt.", child.Name)),
            BlockReason.OutsideWindow => (L.In(l, "Jetzt ist keine PC-Zeit"),
                rule.WindowStart is { } a && rule.WindowEnd is { } b
                    ? L.In(l, "{0}, heute darfst du von {1} bis {2} Uhr an den PC.", child.Name, a.ToString("t", culture), b.ToString("t", culture))
                    : L.In(l, "{0}, jetzt ist keine PC-Zeit.", child.Name)),
            _ => (L.In(l, "Deine Bildschirmzeit ist aufgebraucht"), L.In(l, "{0}, für heute ist deine Zeit vorbei. Morgen geht es weiter!", child.Name)),
        };
    }

    /// <summary>Event text for the parents (translated on the website).</summary>
    private static LocText EventText(BlockReason reason, bool logoff) => (reason, logoff) switch
    {
        (BlockReason.LockedByParent, false) => LocText.Of("Von den Eltern gesperrt – Sperrbildschirm"),
        (BlockReason.LockedByParent, true) => LocText.Of("Von den Eltern gesperrt – abgemeldet."),
        (BlockReason.OutsideWindow, false) => LocText.Of("Außerhalb der PC-Zeit – Sperrbildschirm"),
        (BlockReason.OutsideWindow, true) => LocText.Of("Außerhalb der PC-Zeit – abgemeldet."),
        (_, false) => LocText.Of("Bildschirmzeit aufgebraucht – Sperrbildschirm"),
        (_, true) => LocText.Of("Bildschirmzeit aufgebraucht – abgemeldet."),
    };

    private static LocText SourceText(TimeSource s) => s switch
    {
        TimeSource.Grant => LocText.Of("Freigegeben (Zusatzzeit von den Eltern)"),
        TimeSource.Code => LocText.Of("Freigegeben (Freischaltcode)"),
        TimeSource.Unrestricted => LocText.Of("Freigegeben (Überwachung pausiert)"),
        _ => LocText.Of("Freigegeben (reguläre Zeit)"),
    };

    private static DateTimeOffset Max(DateTimeOffset a, DateTimeOffset b) => a > b ? a : b;
}
