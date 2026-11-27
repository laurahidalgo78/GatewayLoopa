namespace ApiGatewayPay.Core.Common;

/// <summary>
/// Monto en unidades mínimas (centavos) para evitar errores de punto flotante.
/// </summary>
public readonly record struct Money
{
    public long AmountMinor { get; }
    public string Currency { get; }

    public Money(long amountMinor, string currency)
    {
        if (amountMinor <= 0)
            throw AppException.Validation("invalid_amount", "El monto debe ser mayor que cero.");
        if (currency is not { Length: 3 } || !currency.All(char.IsAsciiLetterUpper))
            throw AppException.Validation("invalid_currency", "La moneda debe ser un código ISO 4217.");
        AmountMinor = amountMinor;
        Currency = currency;
    }

    public override string ToString() => $"{AmountMinor} {Currency}";
}
