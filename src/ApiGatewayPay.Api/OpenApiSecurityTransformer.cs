using Microsoft.AspNetCore.OpenApi;
using Microsoft.OpenApi;

namespace ApiGatewayPay.Api;

/// <summary>Declara los esquemas de seguridad en el documento OpenAPI generado.</summary>
public sealed class OpenApiSecurityTransformer : IOpenApiDocumentTransformer
{
    public Task TransformAsync(OpenApiDocument document, OpenApiDocumentTransformerContext context, CancellationToken cancellationToken)
    {
        document.Info = new OpenApiInfo
        {
            Title = "ApiGatewayPay",
            Version = "v1",
            Description = "API segura de pagos y conexión bancaria. Errores en formato RFC 9457 (application/problem+json).",
        };
        document.Components ??= new OpenApiComponents();
        document.Components.SecuritySchemes = new Dictionary<string, IOpenApiSecurityScheme>
        {
            ["Bearer"] = new OpenApiSecurityScheme { Type = SecuritySchemeType.Http, Scheme = "bearer", BearerFormat = "JWT", Description = "Access token OAuth2/OIDC (vida corta)." },
            ["ApiKey"] = new OpenApiSecurityScheme { Type = SecuritySchemeType.ApiKey, In = ParameterLocation.Header, Name = "X-Api-Key", Description = "API key de cliente máquina." },
        };
        document.Security =
        [
            new OpenApiSecurityRequirement { [new OpenApiSecuritySchemeReference("Bearer", document)] = [] },
            new OpenApiSecurityRequirement { [new OpenApiSecuritySchemeReference("ApiKey", document)] = [] },
        ];
        return Task.CompletedTask;
    }
}
