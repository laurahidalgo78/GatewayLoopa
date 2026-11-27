using System.Net;
using ApiGatewayPay.Api.Contracts;
using ApiGatewayPay.Api.Middleware;
using ApiGatewayPay.Api.Security;
using ApiGatewayPay.Core.Common;
using ApiGatewayPay.Core.Ports.Bank;
using ApiGatewayPay.Core.Ports.Persistence;
using ApiGatewayPay.Core.Ports.Security;
using ApiGatewayPay.Core.UseCases.Reconciliation;
using ApiGatewayPay.Core.UseCases.Webhooks;
using ApiGatewayPay.Infrastructure.Persistence;
using ApiGatewayPay.Infrastructure.Security;
using Microsoft.Extensions.Options;

namespace ApiGatewayPay.Api.Endpoints;

public static class PlatformEndpoints
{
    public static void MapWebhookEndpoints(this IEndpointRouteBuilder app)
    {
        // Anónimo a nivel de autenticación de usuario: la autenticidad la da la firma + timestamp + nonce + IP allowlist.
        app.MapPost("/v1/webhooks/bank/{provider}", ReceiveBankWebhook).AllowAnonymous().RequireRateLimiting("webhooks").WithTags("Webhooks");
    }

    public static void MapAdminEndpoints(this IEndpointRouteBuilder app)
    {
        var admin = app.MapGroup("/v1/admin").WithTags("Admin").AddEndpointFilter<ValidationFilter>();

        admin.MapGet("/kill-switch", async (IKillSwitch ks, CancellationToken ct) => TypedResults.Ok(await ks.GetAsync(ct)))
            .RequireAuthorization(Policies.Scope(Scopes.PlatformKillSwitch));
        admin.MapPost("/kill-switch", async (KillSwitchRequest body, HttpContext http, IKillSwitch ks, CancellationToken ct) =>
            {
                await ks.SetAsync(body.Active!.Value, body.Reason, http.GetActor(), ct);
                return TypedResults.Ok(await ks.GetAsync(ct));
            })
            .RequireAuthorization(Policies.Scope(Scopes.PlatformKillSwitch), Policies.StepUp)
            .WithSummary("Activa/desactiva de inmediato la conexión bancaria (incidentes)");

        admin.MapPost("/api-keys", async (CreateApiKeyRequest body, HttpContext http, ApiKeyService keys, CancellationToken ct) =>
            {
                var created = await keys.CreateAsync(body.Name, body.Scopes, body.ExpiresInDays, http.GetActor(), ct);
                return TypedResults.Created($"/v1/admin/api-keys/{created.KeyId}", created); // la clave solo se muestra una vez
            })
            .RequireAuthorization(Policies.Scope(Scopes.AdminApiKeys), Policies.StepUp);
        admin.MapGet("/api-keys", async (HttpContext http, ApiKeyService keys, CancellationToken ct) => TypedResults.Ok(await keys.ListAsync(http.GetActor().TenantId, ct)))
            .RequireAuthorization(Policies.Scope(Scopes.AdminApiKeys));
        admin.MapDelete("/api-keys/{id}", async (string id, HttpContext http, ApiKeyService keys, CancellationToken ct) =>
            {
                await keys.RevokeAsync(id, http.GetActor(), ct);
                return TypedResults.NoContent();
            })
            .RequireAuthorization(Policies.Scope(Scopes.AdminApiKeys), Policies.StepUp);

        admin.MapPost("/reconciliation/runs", async (ReconciliationRequest body, ReconciliationService service, CancellationToken ct) =>
                TypedResults.Ok(await service.RunAsync(body.Date!.Value, ct)))
            .RequireAuthorization(Policies.Scope(Scopes.PlatformReconciliation));
        admin.MapGet("/reconciliation/runs/{id:guid}", async (Guid id, IReconciliationStore store, CancellationToken ct) =>
                await store.GetAsync(id, ct) is { } report ? Results.Ok(report) : throw AppException.NotFound("report_not_found", "Reporte no encontrado."))
            .RequireAuthorization(Policies.Scope(Scopes.PlatformReconciliation));

        admin.MapGet("/ledger/verify", async (string? aggregateType, string? aggregateId, HttpContext http, LedgerVerifier verifier, CancellationToken ct) =>
                TypedResults.Ok(await verifier.VerifyAsync(http.GetActor().TenantId, aggregateType, aggregateId, ct)))
            .RequireAuthorization(Policies.Scope(Scopes.AdminAudit));
    }

    private static async Task<IResult> ReceiveBankWebhook(string provider, HttpContext http, WebhookService service,
        IOptionsMonitor<WebhookSecurityOptions> options, CancellationToken ct)
    {
        var cidrs = options.CurrentValue.AllowedCidrs;
        var ip = http.Connection.RemoteIpAddress;
        if (ip is { IsIPv4MappedToIPv6: true }) ip = ip.MapToIPv4();
        if (cidrs.Count > 0 && (ip is null || !cidrs.Any(c => IPNetwork.Parse(c).Contains(ip))))
        {
            Infrastructure.Telemetry.AppMetrics.WebhookRejected("ip_not_allowed");
            await ProblemWriter.WriteAsync(http, 403, "webhook_rejected", "Origen no permitido.");
            return Results.Empty;
        }

        using var buffer = new MemoryStream();
        await http.Request.Body.CopyToAsync(buffer, ct); // limitado por Kestrel MaxRequestBodySize
        var headers = http.Request.Headers.ToDictionary(h => h.Key, h => h.Value.ToString(), StringComparer.OrdinalIgnoreCase);
        var result = await service.IngestAsync(provider, new BankWebhookEnvelope(headers, buffer.ToArray()), ct);
        return result == WebhookIngestResult.Accepted
            ? TypedResults.Accepted((string?)null, new { status = "accepted" })
            : TypedResults.Ok(new { status = "duplicate" });
    }
}
