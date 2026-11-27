namespace ApiGatewayPay.Core.Common;

public enum ErrorKind
{
    Validation,
    Unauthorized,
    Forbidden,
    NotFound,
    Conflict,
    IdempotencyMismatch,
    BusinessRule,
    Unavailable,
}

/// <summary>
/// Error de aplicación con un código estable para el cliente. El mensaje es apto para mostrarse:
/// nunca incluye detalles internos (SQL, stack, hosts, datos de terceros).
/// </summary>
public sealed class AppException(ErrorKind kind, string code, string message) : Exception(message)
{
    public ErrorKind Kind { get; } = kind;
    public string Code { get; } = code;

    public static AppException Validation(string code, string message) => new(ErrorKind.Validation, code, message);
    public static AppException Forbidden(string code, string message) => new(ErrorKind.Forbidden, code, message);
    public static AppException NotFound(string code, string message) => new(ErrorKind.NotFound, code, message);
    public static AppException Conflict(string code, string message) => new(ErrorKind.Conflict, code, message);
    public static AppException BusinessRule(string code, string message) => new(ErrorKind.BusinessRule, code, message);
    public static AppException Unavailable(string code, string message) => new(ErrorKind.Unavailable, code, message);
}
