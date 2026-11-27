using ApiGatewayPay.Core.Fraud;
using ApiGatewayPay.Core.Ports.Messaging;
using ApiGatewayPay.Core.UseCases.Accounts;
using ApiGatewayPay.Core.UseCases.Beneficiaries;
using ApiGatewayPay.Core.UseCases.Payments;
using ApiGatewayPay.Core.UseCases.Reconciliation;
using ApiGatewayPay.Core.UseCases.Webhooks;
using Microsoft.Extensions.DependencyInjection;

namespace ApiGatewayPay.Core;

public static class CoreServiceCollectionExtensions
{
    public static IServiceCollection AddCore(this IServiceCollection services)
    {
        services.AddSingleton(TimeProvider.System);
        services.AddSingleton<FraudEngine>();
        services.AddScoped<PaymentService>();
        services.AddScoped<BeneficiaryService>();
        services.AddScoped<AccountService>();
        services.AddScoped<WebhookService>();
        services.AddScoped<ReconciliationService>();
        services.AddScoped<IJobHandler, SubmitPaymentHandler>();
        services.AddScoped<IJobHandler, PaymentInquiryHandler>();
        services.AddScoped<IJobHandler, BackendNotificationHandler>();
        services.AddScoped<IJobHandler, ProcessBankWebhookHandler>();
        return services;
    }
}
