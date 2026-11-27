using ApiGatewayPay.Api.Contracts;
using ApiGatewayPay.Api.Security;
using ApiGatewayPay.Core.Common;
using ApiGatewayPay.Core.Domain.Payments;
using ApiGatewayPay.Core.UseCases.Accounts;
using ApiGatewayPay.Core.UseCases.Beneficiaries;
using ApiGatewayPay.Core.UseCases.Payments;
using Microsoft.AspNetCore.Mvc;

namespace ApiGatewayPay.Api.Endpoints;

public static class BusinessEndpoints
{
    public static void MapBusinessEndpoints(this IEndpointRouteBuilder app)
    {
        var payments = app.MapGroup("/v1/payments").WithTags("Payments").AddEndpointFilter<ValidationFilter>();
        payments.MapPost("/", CreatePayment)
            .RequireAuthorization(Policies.Scope(Scopes.PaymentsWrite)).RequireRateLimiting("money")
            .WithSummary("Crea un pago (requiere Idempotency-Key)");
        payments.MapGet("/", ListPayments).RequireAuthorization(Policies.Scope(Scopes.PaymentsRead));
        payments.MapGet("/{id:guid}", GetPayment).RequireAuthorization(Policies.Scope(Scopes.PaymentsRead));
        payments.MapPost("/{id:guid}/approve", ApprovePayment)
            .RequireAuthorization(Policies.Scope(Scopes.PaymentsApprove), Policies.StepUp).RequireRateLimiting("money")
            .WithSummary("Aprueba un pago pendiente (maker-checker + MFA reciente)");
        payments.MapPost("/{id:guid}/reject", RejectPayment)
            .RequireAuthorization(Policies.Scope(Scopes.PaymentsApprove), Policies.StepUp);

        var beneficiaries = app.MapGroup("/v1/beneficiaries").WithTags("Beneficiaries").AddEndpointFilter<ValidationFilter>();
        beneficiaries.MapPost("/", RegisterBeneficiary)
            .RequireAuthorization(Policies.Scope(Scopes.BeneficiariesWrite), Policies.StepUp).RequireRateLimiting("money")
            .WithSummary("Registra una cuenta destino en la lista blanca (MFA reciente)");
        beneficiaries.MapGet("/", ListBeneficiaries).RequireAuthorization(Policies.Scope(Scopes.BeneficiariesRead));
        beneficiaries.MapPost("/{id:guid}/approve", ApproveBeneficiary)
            .RequireAuthorization(Policies.Scope(Scopes.BeneficiariesApprove), Policies.StepUp);

        var accounts = app.MapGroup("/v1/accounts").WithTags("Accounts").RequireAuthorization(Policies.Scope(Scopes.AccountsRead));
        accounts.MapGet("/", ListAccounts);
        accounts.MapGet("/{accountRef}/balance", GetBalance);
        accounts.MapGet("/{accountRef}/movements", GetMovements);
    }

    private static async Task<IResult> CreatePayment(CreatePaymentRequest body, [FromHeader(Name = "Idempotency-Key")] string? idempotencyKey,
        HttpContext http, PaymentService service, CancellationToken ct)
    {
        if (!Patterns.IsValidIdempotencyKey(idempotencyKey))
            throw AppException.Validation("invalid_idempotency_key", "Idempotency-Key es obligatoria (16-64 caracteres [A-Za-z0-9_-]).");
        var result = await service.CreateAsync(new CreatePaymentCommand(body.BeneficiaryId!.Value, body.AmountMinor, body.Currency, body.Reference, idempotencyKey!),
            http.GetActor(), ct);
        if (result.Replayed) http.Response.Headers["Idempotent-Replayed"] = "true";
        return TypedResults.Created($"/v1/payments/{result.Payment.Id}", PaymentResponse.From(result.Payment));
    }

    private static async Task<IResult> ListPayments(HttpContext http, PaymentService service, PaymentStatus? status, int? limit, string? cursor, CancellationToken ct)
    {
        var page = await service.ListAsync(http.GetActor(), status, limit ?? 25, cursor, ct);
        return TypedResults.Ok(new PaymentListResponse(page.Items.Select(PaymentResponse.From).ToList(), page.NextCursor));
    }

    private static async Task<IResult> GetPayment(Guid id, HttpContext http, PaymentService service, CancellationToken ct) =>
        TypedResults.Ok(PaymentResponse.From(await service.GetAsync(id, http.GetActor(), ct)));

    private static async Task<IResult> ApprovePayment(Guid id, HttpContext http, PaymentService service, CancellationToken ct) =>
        TypedResults.Ok(PaymentResponse.From(await service.ApproveAsync(id, http.GetActor(), ct)));

    private static async Task<IResult> RejectPayment(Guid id, RejectPaymentRequest body, HttpContext http, PaymentService service, CancellationToken ct) =>
        TypedResults.Ok(PaymentResponse.From(await service.RejectAsync(id, body.ReasonCode, http.GetActor(), ct)));

    private static async Task<IResult> RegisterBeneficiary(RegisterBeneficiaryRequest body, HttpContext http, BeneficiaryService service, CancellationToken ct)
    {
        var b = await service.RegisterAsync(new RegisterBeneficiaryCommand(body.DisplayName, body.BankCode, body.AccountType!.Value, body.AccountNumber), http.GetActor(), ct);
        return TypedResults.Created($"/v1/beneficiaries/{b.Id}", BeneficiaryResponse.From(b));
    }

    private static async Task<IResult> ListBeneficiaries(HttpContext http, BeneficiaryService service, CancellationToken ct) =>
        TypedResults.Ok((await service.ListAsync(http.GetActor(), ct)).Select(BeneficiaryResponse.From));

    private static async Task<IResult> ApproveBeneficiary(Guid id, HttpContext http, BeneficiaryService service, CancellationToken ct) =>
        TypedResults.Ok(BeneficiaryResponse.From(await service.ApproveAsync(id, http.GetActor(), ct)));

    private static async Task<IResult> ListAccounts(HttpContext http, AccountService service, CancellationToken ct) =>
        TypedResults.Ok((await service.ListAsync(http.GetActor(), ct)).Select(AccountResponse.From));

    private static async Task<IResult> GetBalance(string accountRef, HttpContext http, AccountService service, CancellationToken ct)
    {
        var balance = await service.GetBalanceAsync(accountRef, http.GetActor(), ct);
        return TypedResults.Ok(new BalanceResponse(accountRef, balance.Currency, balance.AvailableMinor, balance.LedgerMinor, balance.AsOf));
    }

    private static async Task<IResult> GetMovements(string accountRef, DateOnly from, DateOnly to, HttpContext http, AccountService service, CancellationToken ct)
    {
        var movements = await service.GetMovementsAsync(accountRef, from, to, http.GetActor(), ct);
        return TypedResults.Ok(movements.Take(1000).Select(m => new MovementResponse(m.MovementId, m.BookedAt, m.AmountMinor, m.Currency, m.Direction, m.Description)));
    }
}
