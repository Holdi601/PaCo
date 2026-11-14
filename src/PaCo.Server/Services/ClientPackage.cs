using System.Security.Cryptography;
using Microsoft.Extensions.Options;
using PaCo.Shared;

namespace PaCo.Server.Services;

/// <summary>
/// The Windows client offered for download and for updates. Two sources: the build bundled in the Docker image
/// (/app/client) and builds fetched from GitHub releases (DataDir/client-updates). The newest version wins.
/// </summary>
public sealed class ClientPackage(IOptions<ServerOptions> options)
{
    private readonly Lock _gate = new();
    private (string Path, DateTime Write, string Sha)? _shaCache;

    public sealed record Build(string Path, string Version, string Source);

    public string UpdatesDir => Path.Combine(options.Value.DataDir, "client-updates");

    private Build? Bundled
    {
        get
        {
            var exe = Path.GetFullPath(options.Value.ClientPath);
            if (!File.Exists(exe)) return null;
            var vfile = Path.Combine(Path.GetDirectoryName(exe)!, "version.txt");
            var v = File.Exists(vfile) ? File.ReadAllText(vfile).Trim() : "0.0.0";
            return new Build(exe, v, "Docker-Image");
        }
    }

    private IEnumerable<Build> Downloaded
    {
        get
        {
            if (!Directory.Exists(UpdatesDir)) yield break;
            foreach (var f in Directory.GetFiles(UpdatesDir, "PaCo-Setup-*.exe"))
            {
                var v = Path.GetFileNameWithoutExtension(f)["PaCo-Setup-".Length..];
                if (System.Version.TryParse(v, out _)) yield return new Build(f, v, "GitHub");
            }
        }
    }

    /// <summary>The newest available build.</summary>
    public Build? Current =>
        Downloaded.Append(Bundled).Where(b => b is not null).Select(b => b!)
            .OrderByDescending(b => System.Version.Parse(b.Version)).FirstOrDefault();

    public bool Available => Current is not null;

    public string? Version => Current?.Version;

    /// <summary>SHA-256 (hex) of the current build, cached per file.</summary>
    public string? Sha256
    {
        get
        {
            var b = Current;
            if (b is null) return null;
            var write = File.GetLastWriteTimeUtc(b.Path);
            lock (_gate)
            {
                if (_shaCache is { } c && c.Path == b.Path && c.Write == write) return c.Sha;
                using var fs = File.OpenRead(b.Path);
                var sha = Convert.ToHexStringLower(SHA256.HashData(fs));
                _shaCache = (b.Path, write, sha);
                return sha;
            }
        }
    }

    public long Size => Current is { } b ? new FileInfo(b.Path).Length : 0;

    /// <summary>Installer with embedded server URL and one-time pairing code.</summary>
    public Stream OpenPersonalised(string serverUrl, string pairingCode)
    {
        var trailer = SetupPayload.Encode(new SetupPayload { ServerUrl = serverUrl, PairingCode = pairingCode });
        return new ConcatStream(File.OpenRead(Current!.Path), trailer);
    }

    /// <summary>Plain client exe (used by installed clients for self-update).</summary>
    public Stream OpenPlain() => File.OpenRead(Current!.Path);

    /// <summary>Read-only stream = file followed by a small byte array, without copying the exe into memory.</summary>
    private sealed class ConcatStream(FileStream file, byte[] tail) : Stream
    {
        private long _pos;

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => file.Length + tail.Length;
        public override long Position { get => _pos; set => throw new NotSupportedException(); }

        public override int Read(byte[] buffer, int offset, int count) => Read(buffer.AsSpan(offset, count));

        public override int Read(Span<byte> buffer)
        {
            int n;
            if (_pos < file.Length)
            {
                n = file.Read(buffer);
            }
            else
            {
                var tpos = (int)(_pos - file.Length);
                n = Math.Min(buffer.Length, tail.Length - tpos);
                if (n <= 0) return 0;
                tail.AsSpan(tpos, n).CopyTo(buffer);
            }
            _pos += n;
            return n;
        }

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct = default)
        {
            if (_pos < file.Length)
            {
                var n = await file.ReadAsync(buffer, ct);
                _pos += n;
                return n;
            }
            return Read(buffer.Span);
        }

        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken ct) =>
            ReadAsync(buffer.AsMemory(offset, count), ct).AsTask();

        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        protected override void Dispose(bool disposing)
        {
            if (disposing) file.Dispose();
            base.Dispose(disposing);
        }
    }
}
