using System.Security.Cryptography.X509Certificates;
using Microsoft.Extensions.Options;

namespace PaCo.Server.Services;

/// <summary>
/// Loads the Let's Encrypt PEM files (renewed by acme.sh) and reloads them when they change on disk, so a
/// renewal never needs a restart.
/// </summary>
public sealed class TlsCertificateProvider(IOptions<ServerOptions> options, ILogger<TlsCertificateProvider> log)
{
    private readonly Lock _gate = new();
    private X509Certificate2? _cert;
    private DateTime _loadedWrite;
    private DateTime _nextCheck;

    public bool Enabled => !string.IsNullOrWhiteSpace(options.Value.TlsCertPath) && !string.IsNullOrWhiteSpace(options.Value.TlsKeyPath);

    public X509Certificate2 Current
    {
        get
        {
            lock (_gate)
            {
                var now = DateTime.UtcNow;
                if (_cert is not null && now < _nextCheck) return _cert;
                _nextCheck = now.AddMinutes(10);

                var certPath = options.Value.TlsCertPath!;
                var keyPath = options.Value.TlsKeyPath!;
                var write = new[] { File.GetLastWriteTimeUtc(certPath), File.GetLastWriteTimeUtc(keyPath) }.Max();
                if (_cert is not null && write == _loadedWrite) return _cert;

                try
                {
                    var cert = X509Certificate2.CreateFromPemFile(certPath, keyPath);
                    _cert = cert;
                    _loadedWrite = write;
                    log.LogInformation("TLS-Zertifikat geladen: {Subject}, gültig bis {Expiry:u}", cert.Subject, cert.NotAfter);
                }
                catch (Exception ex) when (_cert is not null)
                {
                    log.LogError(ex, "Neues TLS-Zertifikat konnte nicht geladen werden – altes bleibt aktiv.");
                }
                return _cert!;
            }
        }
    }
}
