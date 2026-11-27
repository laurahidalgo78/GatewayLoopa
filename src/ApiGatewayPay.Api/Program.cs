using System.Net;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading.RateLimiting;
using ApiGatewayPay.Api.Endpoints;
using ApiGatewayPay.Api.Middleware;
using ApiGatewayPay.Api.Security;
using ApiGatewayPay.Core;
using ApiGatewayPay.Infrastructure;
using ApiGatewayPay.Infrastructure.Http;
using ApiGatewayPay.Infrastructure.Persistence;
using ApiGatewayPay.Infrastructure.Platform;
using ApiGatewayPay.Infrastructure.Telemetry;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Diagnostics.HealthChecks;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.AspNetCore.Http.Json;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Tokens;
using OpenTelemetry.Metrics;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;

if (args.Contains("--print-sqlserver-schema"))
{
    // Utilidad: dotnet run --project src/ApiGatewayPay.Api -- --print-sqlserver-schema > db/sqlserver/schema.sql
    Console.WriteLine(SchemaInitializer.GenerateSqlServerScript());
    return;
}

var builder = WebApplication.CreateBuilder(args);
var config = builder.Configuration;

// Fail-fast: no arranca con configuración que mezcle entornos o deje mocks en producción.
EnvironmentGuard.EnsureValid(config);

// ---------- Logs estructurados (JSON) con correlationId en el scope ----------
builder.Logging.ClearProviders();
builder.Logging.AddJsonConsole(o =>
{
    o.IncludeScopes = true;
    o.UseUtcTimestamp = true;
    o.TimestampFormat = "yyyy-MM-ddTHH:mm:ss.fffZ";
});

// ---------- Kestrel: límites de recursos (API4) ----------
builder.WebHost.ConfigureKestrel(k =>
{
    k.AddServerHeader = false;
    k.Limits.MaxRequestBodySize = config.GetValue("Security:MaxRequestBodyBytes", 64 * 1024);
    k.Limits.MaxRequestHeadersTotalSize = 32 * 1024;
    k.Limits.MaxRequestLineSize = 8 * 1024;
    k.Limits.RequestHeadersTimeout = TimeSpan.FromSeconds(15);
    k.Limits.KeepAliveTimeout = TimeSpan.FromSeconds(60);
});

// ---------- JSON estricto: sin campos desconocidos, sin duplicados, enums como texto ----------
builder.Services.Configure<JsonOptions>(o =>
{
    o.SerializerOptions.UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow;
    // Los valores Web por defecto aceptan "100" como número; en montos eso se prohíbe explícitamente.
    o.SerializerOptions.NumberHandling = JsonNumberHandling.Strict;
    o.SerializerOptions.AllowDuplicateProperties = false;
    o.SerializerOptions.MaxDepth = 16;
    o.SerializerOptions.Converters.Add(new JsonStringEnumConverter(JsonNamingPolicy.SnakeCaseLower, allowIntegerValues: false));
});
builder.Services.Configure<RouteHandlerOptions>(o => o.ThrowOnBadRequest = true);
builder.Services.AddExceptionHandler<GlobalExceptionHandler>();
builder.Services.AddProblemDetails();

// ---------- Núcleo + adaptadores ----------
builder.Services.AddCore();
builder.Services.AddInfrastructure(config);

// ---------- Autenticación: JWT (OIDC o emisor local) + API keys ----------
builder.Services.Configure<AuthOptions>(config.GetSection(AuthOptions.Section));
var auth = config.GetSection(AuthOptions.Section).Get<AuthOptions>() ?? new();
const string SmartScheme = "Smart";
builder.Services
    .AddAuthentication(SmartScheme)
    .AddPolicyScheme(SmartScheme, "JWT o API key", o => o.ForwardDefaultSelector = ctx =>
        ctx.Request.Headers.ContainsKey(ApiKeyAuthenticationHandler.HeaderName) ? ApiKeyAuthenticationHandler.SchemeName : JwtBearerDefaults.AuthenticationScheme)
    .AddScheme<Microsoft.AspNetCore.Authentication.AuthenticationSchemeOptions, ApiKeyAuthenticationHandler>(ApiKeyAuthenticationHandler.SchemeName, null)
    .AddJwtBearer(o =>
    {
        o.MapInboundClaims = false;
        o.TokenValidationParameters = new TokenValidationParameters
        {
            ValidateIssuer = true,
            ValidateAudience = true,
            ValidateLifetime = true,
            ValidateIssuerSigningKey = true,
            RequireExpirationTime = true,
            RequireSignedTokens = true,
            ClockSkew = TimeSpan.FromSeconds(30),
            // Lista cerrada de algoritmos: bloquea "none" y la confusión HS256/RS256.
            ValidAlgorithms = [SecurityAlgorithms.RsaSha256, SecurityAlgorithms.RsaSsaPssSha256, SecurityAlgorithms.EcdsaSha256],
            NameClaimType = "sub",
        };
        if (auth.Mode == "Oidc")
        {
            o.Authority = auth.Oidc.Authority;
            o.Audience = auth.Oidc.Audience;
            o.RequireHttpsMetadata = auth.Oidc.RequireHttpsMetadata;
            o.TokenValidationParameters.ValidAudience = auth.Oidc.Audience;
        }
        else
        {
            o.TokenValidationParameters.ValidIssuer = auth.Local.Issuer;
            o.TokenValidationParameters.ValidAudience = auth.Local.Audience;
        }
    });
if (auth.Mode == "Local")
{
    builder.Services.AddSingleton<LocalSigningKey>();
    builder.Services.AddScoped<LocalTokenService>();
    builder.Services.AddOptions<JwtBearerOptions>(JwtBearerDefaults.AuthenticationScheme)
        .Configure<LocalSigningKey>((o, key) => o.TokenValidationParameters.IssuerSigningKey = key.Key);
}
else
{
    // Las claves se obtienen del JWKS del IdP. Solo un token con auth_time (sesión interactiva) es de un humano;
    // los de client credentials se auditan como máquina y nunca cumplen step-up ni maker-checker.
    builder.Services.AddOptions<JwtBearerOptions>(JwtBearerDefaults.AuthenticationScheme).Configure(o =>
        o.Events = new JwtBearerEvents
        {
            OnTokenValidated = ctx =>
            {
                if (ctx.Principal?.Identity is System.Security.Claims.ClaimsIdentity id)
                {
                    foreach (var claim in id.FindAll(ClaimTypesEx.ActorType).ToList()) id.RemoveClaim(claim); // el IdP no decide este claim
                    id.AddClaim(new System.Security.Claims.Claim(ClaimTypesEx.ActorType, id.HasClaim(c => c.Type == "auth_time") ? "user" : "machine"));
                }
                return Task.CompletedTask;
            },
        });
}

// ---------- Autorización: deny-by-default, scopes por endpoint, step-up ----------
builder.Services.AddAuthorization(Policies.Register);
builder.Services.AddSingleton<IAuthorizationHandler, ScopeHandler>();
builder.Services.AddSingleton<IAuthorizationHandler, StepUpHandler>();
builder.Services.AddSingleton<Microsoft.AspNetCore.Authorization.IAuthorizationMiddlewareResultHandler, ProblemAuthorizationResultHandler>();

// ---------- Rate limiting (API4/API6) ----------
builder.Services.AddRateLimiter(o =>
{
    var rl = config.GetSection("RateLimiting");
    o.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
    o.OnRejected = async (ctx, _) =>
    {
        ctx.HttpContext.Response.Headers.RetryAfter = "60";
        await ProblemWriter.WriteAsync(ctx.HttpContext, 429, "rate_limited", "Demasiadas solicitudes. Intente más tarde.");
    };
    o.GlobalLimiter = PartitionedRateLimiter.Create<HttpContext, string>(ctx =>
        RateLimitPartition.GetTokenBucketLimiter(PartitionKey(ctx), _ => new TokenBucketRateLimiterOptions
        {
            TokenLimit = rl.GetValue("GlobalPerMinute", 300),
            TokensPerPeriod = rl.GetValue("GlobalPerMinute", 300),
            ReplenishmentPeriod = TimeSpan.FromMinutes(1),
            QueueLimit = 0,
        }));
    o.AddPolicy("auth", ctx => RateLimitPartition.GetFixedWindowLimiter("auth:" + ctx.Connection.RemoteIpAddress,
        _ => new FixedWindowRateLimiterOptions { PermitLimit = rl.GetValue("AuthPerMinute", 10), Window = TimeSpan.FromMinutes(1), QueueLimit = 0 }));
    o.AddPolicy("money", ctx => RateLimitPartition.GetSlidingWindowLimiter("money:" + PartitionKey(ctx),
        _ => new SlidingWindowRateLimiterOptions { PermitLimit = rl.GetValue("MoneyPerMinute", 30), Window = TimeSpan.FromMinutes(1), SegmentsPerWindow = 6, QueueLimit = 0 }));
    o.AddPolicy("webhooks", ctx => RateLimitPartition.GetFixedWindowLimiter("wh:" + ctx.Connection.RemoteIpAddress,
        _ => new FixedWindowRateLimiterOptions { PermitLimit = rl.GetValue("WebhooksPerMinute", 600), Window = TimeSpan.FromMinutes(1), QueueLimit = 0 }));

    static string PartitionKey(HttpContext ctx) =>
        ctx.User.FindFirst("sub")?.Value is { } sub ? "sub:" + sub : "ip:" + ctx.Connection.RemoteIpAddress;
});

// ---------- CORS restrictivo (vacío = ningún origen permitido) ----------
var origins = config.GetSection("Cors:AllowedOrigins").Get<string[]>() ?? [];
builder.Services.AddCors(o => o.AddDefaultPolicy(p =>
{
    if (origins.Length == 0) return;
    p.WithOrigins(origins).WithMethods("GET", "POST", "DELETE")
        .WithHeaders("Authorization", "Content-Type", "Idempotency-Key", CorrelationContext.HeaderName)
        .WithExposedHeaders(CorrelationContext.HeaderName, "Idempotent-Replayed")
        .SetPreflightMaxAge(TimeSpan.FromMinutes(10));
}));

// ---------- Proxies de confianza (X-Forwarded-For solo desde ellos) ----------
var trustedNetworks = config.GetSection("Network:TrustedProxyNetworks").Get<string[]>() ?? [];
builder.Services.Configure<ForwardedHeadersOptions>(o =>
{
    o.ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto;
    o.ForwardLimit = 1;
    o.KnownIPNetworks.Clear();
    o.KnownProxies.Clear();
    foreach (var n in trustedNetworks) o.KnownIPNetworks.Add(System.Net.IPNetwork.Parse(n));
});

// ---------- Observabilidad: métricas y trazas OpenTelemetry (OTLP) ----------
var otlp = config["Telemetry:OtlpEndpoint"];
builder.Services.AddOpenTelemetry()
    .ConfigureResource(r => r.AddService("apigatewaypay", serviceVersion: typeof(Program).Assembly.GetName().Version?.ToString()))
    .WithMetrics(m =>
    {
        m.AddMeter(AppMetrics.MeterName).AddAspNetCoreInstrumentation().AddHttpClientInstrumentation();
        if (!string.IsNullOrEmpty(otlp)) m.AddOtlpExporter(e => e.Endpoint = new Uri(otlp));
    })
    .WithTracing(t =>
    {
        t.AddAspNetCoreInstrumentation()
         .AddHttpClientInstrumentation(h => h.EnrichWithHttpRequestMessage = (activity, request) =>
             activity.SetTag("url.full", $"{request.RequestUri?.Scheme}://{request.RequestUri?.Host}{OutboundLoggingHandler.RedactPath(request.RequestUri?.AbsolutePath)}"));
        if (!string.IsNullOrEmpty(otlp)) t.AddOtlpExporter(e => e.Endpoint = new Uri(otlp));
    });

var openApiEnabled = config.GetValue("OpenApi:Enabled", false);
if (openApiEnabled) builder.Services.AddOpenApi("v1", o => o.AddDocumentTransformer<ApiGatewayPay.Api.OpenApiSecurityTransformer>());

var app = builder.Build();

if (config.GetValue<bool>("Persistence:AutoCreateSchema"))
{
    using var scope = app.Services.CreateScope();
    await SchemaInitializer.EnsureCreatedAsync(scope.ServiceProvider.GetRequiredService<AppDbContext>());
}

// ---------- Pipeline (el orden importa) ----------
if (trustedNetworks.Length > 0) app.UseForwardedHeaders();
app.UseMiddleware<CorrelationIdMiddleware>();
app.UseExceptionHandler();
app.UseMiddleware<SecurityHeadersMiddleware>();
app.UseMiddleware<RequestSizeLimitMiddleware>();
if (config.GetValue<bool>("Security:RequireHttps"))
{
    app.UseHsts();
    app.UseHttpsRedirection();
}
app.UseStatusCodePages(async ctx =>
{
    var status = ctx.HttpContext.Response.StatusCode;
    await ProblemWriter.WriteAsync(ctx.HttpContext, status, status switch
    {
        404 => "not_found", 405 => "method_not_allowed", 415 => "unsupported_media_type", _ => "http_" + status,
    }, "La solicitud no pudo procesarse.");
});
app.UseCors();
app.UseAuthentication();
app.UseRateLimiter();
app.UseAuthorization();

// ---------- Endpoints ----------
app.MapHealthChecks("/health", new HealthCheckOptions { Predicate = _ => false }).AllowAnonymous();
app.MapHealthChecks("/ready", new HealthCheckOptions
{
    Predicate = c => c.Tags.Contains("ready"),
    ResultStatusCodes = { [HealthStatus.Healthy] = 200, [HealthStatus.Degraded] = 200, [HealthStatus.Unhealthy] = 503 },
    ResponseWriter = async (ctx, report) =>
    {
        ctx.Response.ContentType = "application/json";
        await ctx.Response.WriteAsync(JsonSerializer.Serialize(new
        {
            status = report.Status.ToString(),
            checks = report.Entries.Select(e => new { name = e.Key, status = e.Value.Status.ToString(), detail = e.Value.Description }),
        }));
    },
}).AllowAnonymous();

app.MapBusinessEndpoints();
app.MapWebhookEndpoints();
app.MapAdminEndpoints();
if (auth.Mode == "Local") app.MapLocalAuthEndpoints();
if (openApiEnabled) app.MapOpenApi("/openapi/{documentName}.json").AllowAnonymous();

app.Lifetime.ApplicationStarted.Register(() => app.Logger.LogInformation(
    "ApiGatewayPay iniciado. Entorno={Environment} Banco={Bank} Backend={Backend} Auth={Auth}",
    app.Services.GetRequiredService<IOptions<PlatformOptions>>().Value.Environment, config["Bank:Adapter"], config["Backend:Adapter"], auth.Mode));

await app.RunAsync();

public partial class Program;
