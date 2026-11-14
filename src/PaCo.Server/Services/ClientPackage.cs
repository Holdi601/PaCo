using Microsoft.Extensions.Options;
using PaCo.Shared;

namespace PaCo.Server.Services;

/// <summary>The Windows client installer offered for download, personalised with server URL and pairing code.</summary>
public sealed class ClientPackage(IOptions<ServerOptions> options)
{
    private string ExePath => Path.GetFullPath(options.Value.ClientPath);

    public bool Available => File.Exists(ExePath);

    /// <summary>Version from "version.txt" next to the exe (written by the Docker build).</summary>
    public string? Version
    {
        get
        {
            var file = Path.Combine(Path.GetDirectoryName(ExePath)!, "version.txt");
            return File.Exists(file) ? File.ReadAllText(file).Trim() : null;
        }
    }

    /// <summary>Installer with embedded server URL and one-time pairing code.</summary>
    public Stream OpenPersonalised(string serverUrl, string pairingCode)
    {
        var trailer = SetupPayload.Encode(new SetupPayload { ServerUrl = serverUrl, PairingCode = pairingCode });
        return new ConcatStream(File.OpenRead(ExePath), trailer);
    }

    /// <summary>Plain client exe (used by installed clients for self-update).</summary>
    public Stream OpenPlain() => File.OpenRead(ExePath);

    /// <summary>Read-only stream = file followed by a small byte array, without copying the 70 MB exe into memory.</summary>
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
