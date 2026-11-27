using ApiGatewayPay.Core.Common;
using ApiGatewayPay.Core.Fraud;
using ApiGatewayPay.Core.Ports.Backend;
using ApiGatewayPay.Core.Ports.Bank;
using ApiGatewayPay.Core.Ports.External;
using ApiGatewayPay.Core.Ports.Messaging;
using ApiGatewayPay.Core.Ports.Persistence;
using ApiGatewayPay.Core.Ports.Security;
using ApiGatewayPay.Core.UseCases.Payments;
using ApiGatewayPay.Core.UseCases.Webhooks;
using ApiGatewayPay.Infrastructure.Backend;
using ApiGatewayPay.Infrastructure.Bank;
using ApiGatewayPay.Infrastructure.Bank.Mock;
using ApiGatewayPay.Infrastructure.Bank.Real;
using ApiGatewayPay.Infrastructure.Bank.Tls;
using ApiGatewayPay.Infrastructure.Bank.Webhooks;
using ApiGatewayPay.Infrastructure.Crypto;
using ApiGatewayPay.Infrastructure.External;
using ApiGatewayPay.Infrastructure.Http;
using ApiGatewayPay.Infrastructure.Messaging;
using ApiGatewayPay.Infrastructure.Persistence;
using ApiGatewayPay.Infrastructure.Platform;
using ApiGatewayPay.Infrastructure.Reconciliation;
using ApiGatewayPay.Infrastructure.Secrets;
using ApiGatewayPay.Infrastructure.Security;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace ApiGatewayPay.Infrastructure;

public static class InfrastructureServiceCollectionExtensions
{
    /// <summary>
    /// Composición de adaptadores. Qué implementación se usa para cada puerto lo decide SOLO la configuración
    /// (Bank:Adapter, Backend:Adapter, Secrets:Provider, Persistence:Provider, ExternalServices:*).
    /// </summary>
    public static IServiceCollection AddInfrastructure(this IServiceCollection services, IConfiguration config)
    {
        services.Configure<PlatformOptions>(config.GetSection(PlatformOptions.Section));
        services.Configure<FeatureFlags>(config.GetSection(FeatureFlags.Section));
        services.Configure<FraudOptions>(config.GetSection(FraudOptions.Section));
        services.Configure<PaymentProcessingOptions>(config.GetSection(PaymentProcessingOptions.Section));
        services.Configure<WebhookSecurityOptions>(config.GetSection(WebhookSecurityOptions.Section));
        services.Configure<BankOptions>(config.GetSection(BankOptions.Section));
        services.Configure<BackendOptions>(config.GetSection(BackendOptions.Section));
        services.Configure<SecretsOptions>(config.GetSection(SecretsOptions.Section));
        services.Configure<CryptoOptions>(config.GetSection(CryptoOptions.Section));
        services.Configure<LedgerOptions>(config.GetSection(LedgerOptions.Section));
        services.Configure<QueueOptions>(config.GetSection(QueueOptions.Section));
        services.Configure<ReconciliationOptions>(config.GetSection(ReconciliationOptions.Section));
        services.AddMemoryCache();

        AddSecrets(services, config);
        services.AddSingleton<CertificateStore>();
        services.AddSingleton<IFieldProtector, AesGcmFieldProtector>();

        AddPersistence(services, config);
        AddBackend(services, config);
        AddBank(services, config);

        services.AddExternalServices(config, typeof(InfrastructureServiceCollectionExtensions).Assembly);
        services.AddSingleton<IAlertNotifier, AlertNotifier>();

        if (config.GetValue("Workers:Enabled", true))
        {
            services.AddHostedService<JobWorker>();
            services.AddHostedService<HousekeepingWorker>();
            services.AddHostedService<ReconciliationWorker>();
            services.AddHostedService<CertificateMonitor>();
        }

        services.AddHealthChecks()
            .AddCheck<DatabaseHealthCheck>("database", tags: ["ready"])
            .AddCheck<KillSwitchHealthCheck>("bank_kill_switch", tags: ["ready"])
            .AddCheck<BackendHealthCheck>("backend", tags: ["ready"])
            .AddCheck<CertificateHealthCheck>("certificates", tags: ["ready"]);
        return services;
    }

    private static void AddSecrets(IServiceCollection services, IConfiguration config)
    {
        var options = config.GetSection(SecretsOptions.Section).Get<SecretsOptions>() ?? new();
        if (options.Provider == "Vault")
            services.AddHardenedHttpClient(VaultSecretProvider.HttpClientName,
                (config.GetSection("Secrets:Vault:Http").Get<OutboundHttpOptions>() ?? new()).WithHostsOf(options.Vault.Address));

        services.AddSingleton<ISecretProvider>(sp =>
        {
            ISecretProvider inner = options.Provider switch
            {
                "Config" => new ConfigurationSecretProvider(config),
                "Environment" => new EnvironmentVariableSecretProvider(),
                "File" => ActivatorUtilities.CreateInstance<FileSecretProvider>(sp),
                "Vault" => ActivatorUtilities.CreateInstance<VaultSecretProvider>(sp),
                _ => throw new InvalidOperationException($"Secrets:Provider desconocido: {options.Provider}"),
            };
            var cached = new CachingSecretProvider(inner, sp.GetRequiredService<IOptions<SecretsOptions>>(), sp.GetRequiredService<TimeProvider>());
            return new EnvironmentScopedSecretProvider(cached, sp.GetRequiredService<IOptions<PlatformOptions>>());
        });
    }

    private static void AddPersistence(IServiceCollection services, IConfiguration config)
    {
        var provider = config["Persistence:Provider"] ?? "Sqlite";
        services.AddDbContext<AppDbContext>((sp, o) =>
        {
            var secretName = config["Persistence:ConnectionStringSecretName"];
            var cs = !string.IsNullOrEmpty(secretName)
                ? sp.GetRequiredService<ISecretProvider>().GetAsync(secretName, CancellationToken.None).AsTask().GetAwaiter().GetResult()
                : config["Persistence:ConnectionString"] ?? "Data Source=apigatewaypay.db";
            // Sin reintentos automáticos de SaveChanges: un reintento tras un commit con resultado incierto podría duplicar escrituras.
            _ = provider switch
            {
                "Sqlite" => o.UseSqlite(cs),
                "SqlServer" => o.UseSqlServer(cs),
                _ => throw new InvalidOperationException($"Persistence:Provider desconocido: {provider}"),
            };
        });
        services.AddScoped<IPaymentRepository, EfPaymentRepository>();
        services.AddScoped<IBeneficiaryRepository, EfBeneficiaryRepository>();
        services.AddScoped<ILedger, EfLedger>();
        services.AddScoped<IUnitOfWork, EfUnitOfWork>();
        services.AddScoped<IWebhookInbox, EfWebhookInbox>();
        services.AddScoped<IReconciliationStore, EfReconciliationStore>();
        services.AddScoped<IJobQueue, OutboxJobQueue>();
        services.AddScoped<IKillSwitch, KillSwitch>();
        services.AddScoped<LedgerVerifier>();
        services.AddScoped<ApiKeyService>();
        services.AddScoped<RefreshTokenService>();
    }

    private static void AddBackend(IServiceCollection services, IConfiguration config)
    {
        var options = config.GetSection(BackendOptions.Section).Get<BackendOptions>() ?? new();
        switch (options.Adapter)
        {
            case "Mock":
                services.AddSingleton<IBackendGateway, MockBackendAdapter>();
                break;
            case "Http":
                services.AddHardenedHttpClient(HttpBackendAdapter.HttpClientName, options.Http.Http.WithHostsOf(options.Http.BaseUrl));
                services.AddHardenedHttpClient(HttpBackendAdapter.TokenClientName, options.Http.Http.WithHostsOf(options.Http.Auth.TokenUrl));
                services.AddSingleton<IBackendGateway, HttpBackendAdapter>();
                break;
            default:
                throw new InvalidOperationException($"Backend:Adapter desconocido: {options.Adapter}");
        }
    }

    private static void AddBank(IServiceCollection services, IConfiguration config)
    {
        var options = config.GetSection(BankOptions.Section).Get<BankOptions>() ?? new();
        services.AddSingleton<WebhookVerifier>();
        switch (options.Adapter)
        {
            case "Mock":
                services.AddSingleton<MockBankState>();
                services.AddSingleton<IBankAdapter, MockBankAdapter>();
                services.AddHardenedHttpClient(MockBankWebhookSimulator.HttpClientName, options.Mock.Webhooks.Http.WithHostsOf(options.Mock.Webhooks.TargetUrl));
                if (options.Mock.Webhooks.Enabled) services.AddHostedService<MockBankWebhookSimulator>();
                break;
            case "Real":
                services.AddHardenedHttpClient(RealBankAdapter.HttpClientName, options.Real.Http.WithHostsOf(options.Real.BaseUrl));
                services.AddHardenedHttpClient(RealBankAdapter.TokenClientName, options.Real.Http.WithHostsOf(options.Real.Auth.TokenUrl));
                services.AddSingleton<IBankAdapter, RealBankAdapter>();
                break;
            default:
                throw new InvalidOperationException($"Bank:Adapter desconocido: {options.Adapter}");
        }
    }
}
