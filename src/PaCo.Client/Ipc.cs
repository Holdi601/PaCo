using System.IO.Pipes;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text;
using PaCo.Shared;

namespace PaCo.Client;

/// <summary>
/// Messages between the service (session 0) and the session agent (SYSTEM, in the child's session).
/// Transport: a named pipe with a random name, ACL = SYSTEM only. The child can neither connect nor squat it.
/// </summary>
internal sealed record IpcMessage
{
    // Service → agent
    public const string Status = "status";
    public const string Notify = "notify";
    public const string ShowLock = "showLock";
    public const string HideLock = "hideLock";
    public const string Mute = "mute";
    public const string RestoreAudio = "restoreAudio";
    public const string CodeResult = "codeResult";
    public const string Exit = "exit";
    public const string AlertConfig = "alertConfig";
    public const string TakeScreenshot = "takeScreenshot";
    public const string CloseTab = "closeTab";

    // Agent → service
    public const string Hello = "hello";
    public const string Usage = "usage";
    public const string Code = "code";
    public const string LockState = "lockState";
    public const string LogLine = "log";
    public const string RequestLogoff = "requestLogoff";
    public const string AlertHits = "alertHits";
    public const string ScreenshotData = "screenshotData";
    public const string Focus = "focus";

    public string Type { get; init; } = "";

    public string? Title { get; init; }

    public string? Text { get; init; }

    public string? ChildName { get; init; }

    /// <summary>The child's UI language (policy), for texts the agent renders itself.</summary>
    public string? Lang { get; init; }

    public long? RemainingSeconds { get; init; }

    public bool Allowed { get; init; }

    public bool Paused { get; init; }

    public bool Flag { get; init; }

    public int? Seconds { get; init; }

    public IReadOnlyList<ForegroundSample>? Samples { get; init; }

    public IReadOnlyList<AlertHitDto>? Hits { get; init; }

    public Guid? RequestId { get; init; }

    /// <summary>Foreground program (process name), its description and – for browsers – the host of the active tab.</summary>
    public string? Process { get; init; }

    public string? Description { get; init; }

    public string? Host { get; init; }
}

internal sealed record AlertHitDto(AlertCategory Category, string Keyword, string Snippet, string App, string? Title, string Source);

internal sealed record ForegroundSample(string Process, string DisplayName, int Seconds, string? WindowTitle);

internal sealed class IpcChannel(Stream stream) : IDisposable
{
    private readonly StreamReader _reader = new(stream, new UTF8Encoding(false), false, 4096, leaveOpen: true);
    private readonly StreamWriter _writer = new(stream, new UTF8Encoding(false), 4096, leaveOpen: true) { AutoFlush = true, NewLine = "\n" };
    private readonly SemaphoreSlim _writeGate = new(1, 1);

    public async Task SendAsync(IpcMessage msg, CancellationToken ct = default)
    {
        await _writeGate.WaitAsync(ct);
        try
        {
            await _writer.WriteLineAsync(PaCoJson.Serialize(msg).AsMemory(), ct);
        }
        finally
        {
            _writeGate.Release();
        }
    }

    /// <summary>Returns null when the other side closed the pipe.</summary>
    public async Task<IpcMessage?> ReceiveAsync(CancellationToken ct = default)
    {
        while (true)
        {
            var line = await _reader.ReadLineAsync(ct);
            if (line is null) return null;
            if (line.Length is 0 or > 8 * 1024 * 1024) continue; // screenshots are a few hundred KB
            try
            {
                return PaCoJson.Deserialize<IpcMessage>(line);
            }
            catch (System.Text.Json.JsonException)
            {
                // ignore malformed line
            }
        }
    }

    public void Dispose()
    {
        _reader.Dispose();
        _writer.Dispose();
        stream.Dispose();
        _writeGate.Dispose();
    }

    public static NamedPipeServerStream CreateServer(string name)
    {
        var sec = new PipeSecurity();
        sec.AddAccessRule(new PipeAccessRule(new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null), PipeAccessRights.FullControl, AccessControlType.Allow));
        return NamedPipeServerStreamAcl.Create(name, PipeDirection.InOut, 1, PipeTransmissionMode.Byte,
            PipeOptions.Asynchronous | PipeOptions.FirstPipeInstance, 64 * 1024, 64 * 1024, sec);
    }
}
