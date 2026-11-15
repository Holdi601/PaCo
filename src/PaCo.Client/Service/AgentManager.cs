using System.Diagnostics;
using System.IO.Pipes;
using PaCo.Client.Config;
using PaCo.Client.Native;
using PaCo.Shared.I18n;

namespace PaCo.Client.Service;

/// <summary>One session agent (SYSTEM process in a child's session) and its pipe.</summary>
internal sealed class AgentConnection : IDisposable
{
    public required int SessionId { get; init; }

    public required Process Process { get; init; }

    public required NamedPipeServerStream Pipe { get; init; }

    public IpcChannel? Channel { get; set; }

    public TimeSpan StartedUtc { get; } = Mono.Now;

    public bool Connected => Channel is not null && Pipe.IsConnected && !Process.HasExited;

    public bool LockShown { get; set; }

    public void Dispose()
    {
        try { Channel?.Dispose(); } catch { /* ignore */ }
        try { Pipe.Dispose(); } catch { /* ignore */ }
        try { if (!Process.HasExited) Process.Kill(); } catch { /* ignore */ }
        Process.Dispose();
    }
}

/// <summary>Starts, watches and restarts session agents.</summary>
internal sealed class AgentManager(Func<int, IpcMessage, Task> onMessage, Action<int, LocText>? onCrash = null) : IDisposable
{
    private readonly Dictionary<int, AgentConnection> _agents = [];
    private readonly Dictionary<int, (int Failures, TimeSpan NextTry)> _backoff = [];
    private readonly Lock _gate = new();

    public AgentConnection? Get(int sessionId)
    {
        lock (_gate) return _agents.GetValueOrDefault(sessionId);
    }

    /// <summary>Consecutive failures to get an agent running in this session.</summary>
    public int Failures(int sessionId)
    {
        lock (_gate) return _backoff.GetValueOrDefault(sessionId).Failures;
    }

    public void Ensure(int sessionId)
    {
        lock (_gate)
        {
            if (_agents.TryGetValue(sessionId, out var a))
            {
                if (!a.Process.HasExited && (a.Connected || Mono.Now - a.StartedUtc < TimeSpan.FromSeconds(20))) return;
                Log.Warn($"Agent in Sitzung {sessionId} läuft nicht mehr (exited={a.Process.HasExited}) – starte neu");
                if (a.Process.HasExited)
                {
                    int code;
                    try { code = a.Process.ExitCode; } catch { code = -1; }
                    onCrash?.Invoke(sessionId, LocText.Of("Sitzungsprogramm wurde unerwartet beendet (Code {0}) und neu gestartet.", code));
                }
                a.Dispose();
                _agents.Remove(sessionId);
                Fail(sessionId);
            }

            if (_backoff.TryGetValue(sessionId, out var b) && Mono.Now < b.NextTry) return;

            NamedPipeServerStream? pipe = null;
            try
            {
                var name = "PaCo." + Guid.NewGuid().ToString("N");
                pipe = IpcChannel.CreateServer(name);
                var exe = Environment.ProcessPath!;
                var proc = ProcessLauncher.StartAsSystemInSession(sessionId, exe, $"--agent {name}");
                var conn = new AgentConnection { SessionId = sessionId, Process = proc, Pipe = pipe };
                _agents[sessionId] = conn;
                Log.Info($"Agent gestartet in Sitzung {sessionId} (PID {proc.Id})");
                _ = AcceptAsync(conn);
            }
            catch (Exception ex)
            {
                pipe?.Dispose();
                Log.Error($"Agent-Start in Sitzung {sessionId} fehlgeschlagen", ex);
                Fail(sessionId);
            }
        }
    }

    private void Fail(int sessionId)
    {
        var f = _backoff.GetValueOrDefault(sessionId).Failures + 1;
        _backoff[sessionId] = (f, Mono.Now + TimeSpan.FromSeconds(Math.Min(30, 2 * f)));
    }

    private async Task AcceptAsync(AgentConnection conn)
    {
        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            await conn.Pipe.WaitForConnectionAsync(cts.Token);
            conn.Channel = new IpcChannel(conn.Pipe);
            lock (_gate) _backoff.Remove(conn.SessionId);
            while (await conn.Channel.ReceiveAsync() is { } msg)
                await onMessage(conn.SessionId, msg);
        }
        catch (Exception ex) when (ex is OperationCanceledException or IOException or ObjectDisposedException)
        {
            Log.Warn($"Agent-Verbindung Sitzung {conn.SessionId} beendet: {ex.GetType().Name}");
        }
        catch (Exception ex)
        {
            Log.Error($"Agent-Verbindung Sitzung {conn.SessionId}", ex);
        }
    }

    public async Task SendAsync(int sessionId, IpcMessage msg)
    {
        var a = Get(sessionId);
        if (a?.Channel is null) return;
        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            await a.Channel.SendAsync(msg, cts.Token);
        }
        catch (Exception ex)
        {
            Log.Warn($"Senden an Agent {sessionId} ({msg.Type}) fehlgeschlagen: {ex.Message}");
        }
    }

    /// <summary>Stops agents of sessions that no longer exist or are no longer monitored.</summary>
    public void Retain(IReadOnlySet<int> sessions)
    {
        lock (_gate)
        {
            foreach (var id in _agents.Keys.Where(id => !sessions.Contains(id)).ToList())
            {
                Log.Info($"Agent Sitzung {id} beendet (Sitzung weg oder nicht überwacht)");
                _ = _agents[id].Channel?.SendAsync(new IpcMessage { Type = IpcMessage.Exit });
                var a = _agents[id];
                _agents.Remove(id);
                Task.Delay(2000).ContinueWith(_ => a.Dispose());
            }
            foreach (var id in _backoff.Keys.Where(id => !sessions.Contains(id)).ToList()) _backoff.Remove(id);
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            foreach (var a in _agents.Values) a.Dispose();
            _agents.Clear();
        }
    }
}
