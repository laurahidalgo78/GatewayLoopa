namespace ApiGatewayPay.Infrastructure.Http;

/// <summary>Política de salida por integración. Cada integración tiene la suya: nada se comparte.</summary>
public sealed class OutboundHttpOptions
{
    /// <summary>Lista blanca de hosts (exactos o "*.dominio.com"). Vacía = se bloquea todo.</summary>
    public List<string> AllowedHosts { get; set; } = [];
    public bool RequireHttps { get; set; } = true;
    /// <summary>Solo para desarrollo local (mocks en localhost). Prohibido en producción.</summary>
    public bool AllowPrivateNetworks { get; set; }
    public int TimeoutSeconds { get; set; } = 30;
    public int AttemptTimeoutSeconds { get; set; } = 10;
    public int MaxRetries { get; set; } = 2;
    public double CircuitBreakerFailureRatio { get; set; } = 0.5;
    public int CircuitBreakerMinimumThroughput { get; set; } = 10;
    public int CircuitBreakerBreakSeconds { get; set; } = 30;
    /// <summary>Pins SHA-256 (base64) del SubjectPublicKeyInfo de algún certificado de la cadena. Incluya un pin de respaldo.</summary>
    public List<string> PinnedSpkiSha256 { get; set; } = [];
    /// <summary>CA privada del banco (PEM en el gestor de secretos). Si se define, solo se confía en ella.</summary>
    public string? CustomRootCaSecretName { get; set; }
    public ClientCertificateOptions? ClientCertificate { get; set; }
    public bool CheckCertificateRevocation { get; set; } = true;
    public long MaxResponseBytes { get; set; } = 1_048_576;
}

/// <summary>Certificado de cliente para mTLS, siempre desde el gestor de secretos.</summary>
public sealed class ClientCertificateOptions
{
    public string? PfxSecretName { get; set; }
    public string? PfxPasswordSecretName { get; set; }
    public string? CertPemSecretName { get; set; }
    public string? KeyPemSecretName { get; set; }
    public int ExpiryWarningDays { get; set; } = 30;
    public int ExpiryCriticalDays { get; set; } = 7;

    public bool IsConfigured => !string.IsNullOrEmpty(PfxSecretName) || !string.IsNullOrEmpty(CertPemSecretName);
}

public sealed class OutboundRequestBlockedException(string reason) : HttpRequestException($"Solicitud saliente bloqueada: {reason}");
