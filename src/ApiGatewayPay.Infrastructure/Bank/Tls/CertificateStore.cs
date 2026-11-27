using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using ApiGatewayPay.Core.Ports.Security;
using ApiGatewayPay.Infrastructure.Http;
using Microsoft.Extensions.Logging;

namespace ApiGatewayPay.Infrastructure.Bank.Tls;

public sealed record CertificateInfo(string Integration, string Kind, string Subject, string Thumbprint, DateTime NotAfter);

public sealed record CertificateRegistration(string Integration, ClientCertificateOptions Options);

/// <summary>
/// Carga certificados de cliente (mTLS) y CAs privadas desde el gestor de secretos y los mantiene en caché.
/// <see cref="CertificateMonitor"/> los recarga periódicamente: al rotar el secreto, las conexiones nuevas
/// (los handlers HTTP se reciclan cada 2 min) usan el certificado nuevo sin redespliegue.
/// </summary>
public sealed class CertificateStore(ISecretProvider secrets, IEnumerable<CertificateRegistration> registrations, ILogger<CertificateStore> logger)
{
    private readonly ConcurrentDictionary<string, X509Certificate2> _clientCerts = new();
    private readonly ConcurrentDictionary<string, X509Certificate2> _rootCas = new();
    private readonly ConcurrentDictionary<string, CertificateInfo> _observedServerCerts = new();
    private readonly ConcurrentDictionary<string, ClientCertificateOptions> _registrations =
        new(registrations.Select(r => KeyValuePair.Create(r.Integration, r.Options)));

    public void Register(string integration, ClientCertificateOptions options) => _registrations[integration] = options;

    public X509Certificate2? GetClientCertificate(string integration)
    {
        if (_clientCerts.TryGetValue(integration, out var cached)) return cached;
        if (!_registrations.TryGetValue(integration, out var options) || !options.IsConfigured) return null;
        // Primera carga síncrona (una sola vez); luego el monitor refresca en segundo plano.
        return ReloadClientCertificateAsync(integration, CancellationToken.None).AsTask().GetAwaiter().GetResult();
    }

    public async ValueTask<X509Certificate2?> ReloadClientCertificateAsync(string integration, CancellationToken ct)
    {
        if (!_registrations.TryGetValue(integration, out var o) || !o.IsConfigured) return null;
        X509Certificate2 cert;
        if (!string.IsNullOrEmpty(o.PfxSecretName))
        {
            var pfx = Convert.FromBase64String(await secrets.GetAsync(o.PfxSecretName, ct));
            var password = o.PfxPasswordSecretName is null ? null : await secrets.GetAsync(o.PfxPasswordSecretName, ct);
            cert = X509CertificateLoader.LoadPkcs12(pfx, password, X509KeyStorageFlags.EphemeralKeySet);
        }
        else
        {
            var certPem = await secrets.GetAsync(o.CertPemSecretName!, ct);
            var keyPem = await secrets.GetAsync(o.KeyPemSecretName!, ct);
            using var pem = X509Certificate2.CreateFromPem(certPem, keyPem);
            // En Windows, SChannel requiere la clave en formato PKCS#12 para usarla como certificado cliente.
            cert = X509CertificateLoader.LoadPkcs12(pem.Export(X509ContentType.Pkcs12), null);
        }
        if (!cert.HasPrivateKey) throw new CryptographicException($"El certificado mTLS de {integration} no tiene clave privada.");
        if (_clientCerts.TryGetValue(integration, out var previous) && previous.Thumbprint != cert.Thumbprint)
            logger.LogInformation("Certificado mTLS de {Integration} rotado. Nuevo vence {NotAfter:O}", integration, cert.NotAfter);
        _clientCerts[integration] = cert;
        return cert;
    }

    public X509Certificate2? GetRootCa(string secretName)
    {
        return _rootCas.GetOrAdd(secretName, n =>
            X509Certificate2.CreateFromPem(secrets.GetAsync(n, CancellationToken.None).AsTask().GetAwaiter().GetResult()));
    }

    public void ObserveServerCertificate(string integration, X509Certificate2 cert) =>
        _observedServerCerts[integration] = new(integration, "server", cert.Subject, cert.Thumbprint, cert.NotAfter.ToUniversalTime());

    public IEnumerable<(string Integration, ClientCertificateOptions Options)> Registrations =>
        _registrations.Select(kv => (kv.Key, kv.Value));

    public IReadOnlyList<CertificateInfo> Snapshot() =>
        _clientCerts.Select(kv => new CertificateInfo(kv.Key, "client", kv.Value.Subject, kv.Value.Thumbprint, kv.Value.NotAfter.ToUniversalTime()))
            .Concat(_observedServerCerts.Values).ToList();
}

/// <summary>
/// Validación TLS del servidor: exige cadena válida (o CA privada configurada) y, si hay pins,
/// que alguno coincida con el SPKI de algún certificado de la cadena (pinning con respaldo).
/// </summary>
public static class CertificatePinning
{
    public static string ComputeSpkiPin(X509Certificate2 cert) =>
        Convert.ToBase64String(SHA256.HashData(cert.PublicKey.ExportSubjectPublicKeyInfo()));

    public static bool Validate(string integration, X509Certificate2? cert, X509Chain? chain, System.Net.Security.SslPolicyErrors errors,
        IReadOnlyCollection<string> pins, X509Certificate2? customRoot, CertificateStore store, bool checkRevocation)
    {
        if (cert is null) return false;
        if (customRoot is not null)
        {
            // CA privada: se reconstruye la cadena confiando SOLO en esa raíz.
            using var custom = new X509Chain();
            custom.ChainPolicy.TrustMode = X509ChainTrustMode.CustomRootTrust;
            custom.ChainPolicy.CustomTrustStore.Add(customRoot);
            custom.ChainPolicy.RevocationMode = checkRevocation ? X509RevocationMode.Online : X509RevocationMode.NoCheck;
            if (!custom.Build(cert)) return false;
            if ((errors & ~System.Net.Security.SslPolicyErrors.RemoteCertificateChainErrors) != 0) return false;
            chain = custom;
        }
        else if (errors != System.Net.Security.SslPolicyErrors.None)
        {
            return false;
        }

        store.ObserveServerCertificate(integration, cert);
        if (pins.Count == 0) return true;
        var chainCerts = chain?.ChainElements.Select(e => e.Certificate) ?? [cert];
        return chainCerts.Any(c => pins.Contains(ComputeSpkiPin(c)));
    }
}
