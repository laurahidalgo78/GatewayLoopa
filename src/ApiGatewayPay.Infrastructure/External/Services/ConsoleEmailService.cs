using ApiGatewayPay.Core.Common;
using ApiGatewayPay.Core.Ports.External;
using Microsoft.Extensions.Logging;

namespace ApiGatewayPay.Infrastructure.External.Services;

/// <summary>Mock de correo: no envía nada, registra el envío con el destinatario enmascarado.</summary>
[ExternalService("email-console")]
public sealed class ConsoleEmailService(ExternalServiceContext context) : IExternalService
{
    public string Name => context.Name;
    public ExternalServiceKind Kind => ExternalServiceKind.Email;

    public Task<ExternalServiceResult> ExecuteAsync(ExternalServiceRequest request, CancellationToken ct)
    {
        if (request.Operation != "send" || !request.Parameters.TryGetValue("to", out var to))
            return Task.FromResult(ExternalServiceResult.Fail("unsupported_operation"));
        context.Logger.LogInformation("[email-console] Correo a {To}: {Subject}", Masking.MaskEmail(to), request.Parameters.GetValueOrDefault("subject"));
        return Task.FromResult(ExternalServiceResult.Ok("console-" + Guid.NewGuid().ToString("N")[..8]));
    }

    public Task<bool> CheckHealthAsync(CancellationToken ct) => Task.FromResult(true);
}
