using System.ComponentModel.DataAnnotations;
using ApiGatewayPay.Api.Middleware;

namespace ApiGatewayPay.Api.Contracts;

/// <summary>Valida DataAnnotations de los DTOs antes del handler. Los errores no repiten el valor recibido.</summary>
public sealed class ValidationFilter : IEndpointFilter
{
    public async ValueTask<object?> InvokeAsync(EndpointFilterInvocationContext context, EndpointFilterDelegate next)
    {
        foreach (var argument in context.Arguments)
        {
            if (argument is null || argument.GetType().Namespace != typeof(ValidationFilter).Namespace) continue;
            var results = new List<ValidationResult>();
            if (!Validator.TryValidateObject(argument, new ValidationContext(argument), results, validateAllProperties: true))
            {
                var errors = results
                    .SelectMany(r => r.MemberNames.DefaultIfEmpty("body").Select(m => (Member: char.ToLowerInvariant(m[0]) + m[1..], Message: "Valor inválido.")))
                    .GroupBy(e => e.Member)
                    .ToDictionary(g => g.Key, g => g.Select(e => e.Message).Distinct().ToArray());
                await ProblemWriter.WriteAsync(context.HttpContext, 400, "validation_failed", "La petición contiene campos inválidos.", errors);
                return Results.Empty;
            }
        }
        return await next(context);
    }
}
