using System.Net;
using System.Text;
using ApiGatewayPay.Core.Ports.Bank;
using ApiGatewayPay.Core.Ports.Security;
using ApiGatewayPay.Infrastructure.Bank.Real.Codecs;
using ApiGatewayPay.Infrastructure.Bank.Real.Signing;
using ApiGatewayPay.Infrastructure.Bank.Webhooks;
using ApiGatewayPay.Infrastructure.Http;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Polly.CircuitBreaker;
using Polly.Timeout;

namespace ApiGatewayPay.Infrastructure.Bank.Real;

/// <summary>
/// PLANTILLA de adaptador bancario real. Todo lo específico del banco es configurable:
/// formato (Bank:Real:Format), autenticación (Bank:Real:Auth), firma (Bank:Real:Signing),
/// mTLS + pinning (Bank:Real:Http) y rutas (Bank:Real:Endpoints).
/// </summary>
/// <remarks>
/// Clasificación de resultados de una iniciación de pago (la decisión más delicada del sistema):
/// <list type="bullet">
/// <item>2xx con estado aceptado → Accepted. 2xx con rechazo o 400/422 → Rejected (el banco validó y rechazó).</item>
/// <item>Timeout, conexión cortada, 5xx, 401/403/408/409/429, firma de respuesta inválida → Ambiguous.
/// No se sabe si la orden quedó registrada: la consulta de estado decide; jamás se reenvía a ciegas.</item>
/// </list>
/// </remarks>
public sealed class RealBankAdapter : IBankAdapter
{
    public const string HttpClientName = "bank";
    public const string TokenClientName = "bank-auth";

    private readonly IHttpClientFactory _httpFactory;
    private readonly RealBankOptions _o;
    private readonly IBankCodec _codec;
    private readonly IMessageSigner _signer;
    private readonly IOutboundAuthenticator _auth;
    private readonly WebhookVerifier _webhookVerifier;
    private readonly ILogger<RealBankAdapter> _logger;

    public RealBankAdapter(IHttpClientFactory httpFactory, IOptions<BankOptions> options, ISecretProvider secrets,
        WebhookVerifier webhookVerifier, TimeProvider time, ILogger<RealBankAdapter> logger)
    {
        _httpFactory = httpFactory;
        _o = options.Value.Real;
        Name = options.Value.Name;
        _webhookVerifier = webhookVerifier;
        _logger = logger;
        _codec = _o.Format switch
        {
            "RestJson" => new RestJsonBankCodec(_o.Endpoints),
            "Iso20022" => new Iso20022BankCodec(_o.Endpoints, _o.Iso20022, time),
            "Soap" => new SoapBankCodec(_o.Soap),
            _ => throw new InvalidOperationException($"Formato bancario no soportado: {_o.Format}"),
        };
        _signer = MessageSigner.Create(_o.Signing, secrets, time);
        _auth = OutboundAuthenticator.Create(_o.Auth, secrets, httpFactory, TokenClientName, time);
    }

    public string Name { get; }

    public async Task<BankBalance> GetBalanceAsync(BankAccountRef account, CancellationToken ct)
    {
        var (_, body) = await QueryAsync(_codec.BuildBalance(account), ct);
        return _codec.ParseBalance(account, body);
    }

    public async Task<IReadOnlyList<BankMovement>> GetMovementsAsync(BankAccountRef account, DateOnly from, DateOnly to, CancellationToken ct)
    {
        var (_, body) = await QueryAsync(_codec.BuildMovements(account, from, to), ct);
        return _codec.ParseMovements(body);
    }

    public async Task<BankPaymentSubmission> InitiatePaymentAsync(BankPaymentInstruction instruction, CancellationToken ct)
    {
        var request = _codec.BuildInitiatePayment(instruction);
        HttpResponseMessage response;
        try
        {
            response = await SendAsync(request, ct, idempotencyKey: instruction.EndToEndId);
        }
        catch (BrokenCircuitException)
        {
            // El circuito abierto corta ANTES de enviar: no hubo contacto con el banco, pero se reporta como
            // ambiguo para que el flujo único (consulta de estado) lo resuelva sin ramas especiales.
            return new BankPaymentSubmission(BankSubmissionOutcome.Ambiguous, null, "circuit_open");
        }
        catch (Exception ex) when (ex is HttpRequestException or TimeoutRejectedException or TaskCanceledException or IOException)
        {
            _logger.LogWarning("Iniciación de pago {EndToEndId} ambigua: {ErrorType}", instruction.EndToEndId, ex.GetType().Name);
            return new BankPaymentSubmission(BankSubmissionOutcome.Ambiguous, null, "transport_" + ex.GetType().Name);
        }

        using (response)
        {
            var bytes = await response.Content.ReadAsByteArrayAsync(ct);
            var status = (int)response.StatusCode;
            if (status is 400 or 422)
            {
                try { return _codec.ParseInitiatePayment(status, Encoding.UTF8.GetString(bytes)) with { Outcome = BankSubmissionOutcome.Rejected }; }
                catch (BankIntegrityException) { return new BankPaymentSubmission(BankSubmissionOutcome.Rejected, null, "bank_validation"); }
            }
            if (!response.IsSuccessStatusCode)
                return new BankPaymentSubmission(BankSubmissionOutcome.Ambiguous, null, "http_" + status);
            try
            {
                await _signer.VerifyResponseAsync(response, bytes, ct);
                return _codec.ParseInitiatePayment(status, Encoding.UTF8.GetString(bytes));
            }
            catch (BankIntegrityException ex)
            {
                _logger.LogError("Respuesta de iniciación no confiable para {EndToEndId}: {Reason}", instruction.EndToEndId, ex.Message);
                return new BankPaymentSubmission(BankSubmissionOutcome.Ambiguous, null, "response_integrity");
            }
        }
    }

    public async Task<BankPaymentStatusInfo> GetPaymentStatusAsync(string endToEndId, CancellationToken ct)
    {
        var (status, body) = await QueryAsync(_codec.BuildPaymentStatus(endToEndId), ct, allowNotFound: true);
        return status == HttpStatusCode.NotFound
            ? new BankPaymentStatusInfo(endToEndId, BankPaymentState.NotFound, null, null)
            : _codec.ParsePaymentStatus(endToEndId, body);
    }

    public async Task<BankStatement> GetStatementAsync(DateOnly date, CancellationToken ct)
    {
        var (_, body) = await QueryAsync(_codec.BuildStatement(date), ct);
        return _codec.ParseStatement(date, body);
    }

    public async Task<BankWebhookNotification> VerifyAndParseWebhookAsync(BankWebhookEnvelope envelope, CancellationToken ct)
    {
        var (timestamp, nonce) = await _webhookVerifier.VerifyAsync(envelope, ct);
        BankWebhookPayload payload;
        try { payload = _codec.ParseWebhook(envelope.Body); }
        catch (BankIntegrityException) { throw new WebhookVerificationException("invalid_payload"); }
        return new BankWebhookNotification(payload.EventId, payload.Type, nonce, timestamp, payload.EndToEndId, payload.State, payload.BankReference, payload.ReasonCode);
    }

    private async Task<(HttpStatusCode Status, string Body)> QueryAsync(BankHttpRequest request, CancellationToken ct, bool allowNotFound = false)
    {
        HttpResponseMessage response;
        try
        {
            response = await SendAsync(request, ct);
        }
        catch (Exception ex) when (ex is HttpRequestException or BrokenCircuitException or TimeoutRejectedException or TaskCanceledException or IOException)
        {
            throw new BankUnavailableException("Banco no disponible.", ex);
        }
        using (response)
        {
            var bytes = await response.Content.ReadAsByteArrayAsync(ct);
            if (allowNotFound && response.StatusCode == HttpStatusCode.NotFound) return (response.StatusCode, "");
            if (!response.IsSuccessStatusCode) throw new BankUnavailableException($"El banco respondió {(int)response.StatusCode}.");
            await _signer.VerifyResponseAsync(response, bytes, ct);
            return (response.StatusCode, Encoding.UTF8.GetString(bytes));
        }
    }

    private async Task<HttpResponseMessage> SendAsync(BankHttpRequest r, CancellationToken ct, string? idempotencyKey = null)
    {
        var uri = new Uri(new Uri(_o.BaseUrl.TrimEnd('/') + "/"), r.PathAndQuery.TrimStart('/'));
        using var message = new HttpRequestMessage(r.Method, uri);
        var body = r.Body is null ? [] : Encoding.UTF8.GetBytes(r.Body);
        if (r.Body is not null)
        {
            message.Content = new ByteArrayContent(body);
            message.Content.Headers.TryAddWithoutValidation("Content-Type", r.ContentType);
        }
        if (r.SoapAction is not null) message.Headers.TryAddWithoutValidation("SOAPAction", r.SoapAction);
        if (idempotencyKey is not null) message.Headers.TryAddWithoutValidation("Idempotency-Key", idempotencyKey);
        message.Options.Set(HardenedHttpClientExtensions.Retryable, r.Retryable);
        await _auth.ApplyAsync(message, ct);
        await _signer.SignAsync(message, body, ct);
        return await _httpFactory.CreateClient(HttpClientName).SendAsync(message, ct);
    }
}
