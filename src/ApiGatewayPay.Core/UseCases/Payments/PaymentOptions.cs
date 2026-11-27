namespace ApiGatewayPay.Core.UseCases.Payments;

public sealed class PaymentProcessingOptions
{
    public const string Section = "Payments";

    /// <summary>Primera consulta de estado tras enviar (red de seguridad si no llega webhook).</summary>
    public int FirstInquiryDelaySeconds { get; set; } = 30;
    /// <summary>Consulta tras un envío ambiguo (timeout): pronto, pero dando tiempo al banco a registrar la orden.</summary>
    public int AmbiguousInquiryDelaySeconds { get; set; } = 5;
    public int MaxInquiryDelaySeconds { get; set; } = 3600;
    /// <summary>Tras este número de consultas sin resultado final se alerta para revisión manual.</summary>
    public int InquiryAlertThreshold { get; set; } = 10;
    /// <summary>Reenvíos permitidos (siempre con el mismo EndToEndId) cuando el banco confirma NotFound.</summary>
    public int MaxSubmissionAttempts { get; set; } = 3;
    /// <summary>Un NotFound inmediatamente después de enviar puede ser consistencia eventual del banco.</summary>
    public int NotFoundGraceSeconds { get; set; } = 300;
}
