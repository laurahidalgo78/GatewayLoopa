namespace ApiGatewayPay.Core.Common;

/// <summary>Minimización de datos: solo se exponen o registran los últimos dígitos.</summary>
public static class Masking
{
    public static string Last4(string value) =>
        value.Length <= 4 ? value : value[^4..];

    public static string MaskAccount(string? value)
    {
        if (string.IsNullOrEmpty(value)) return string.Empty;
        return value.Length <= 4 ? new string('*', value.Length) : new string('*', Math.Min(value.Length - 4, 8)) + value[^4..];
    }

    public static string MaskEmail(string? email)
    {
        if (string.IsNullOrEmpty(email)) return string.Empty;
        var at = email.IndexOf('@');
        return at <= 1 ? "***" : $"{email[0]}***{email[at..]}";
    }

    public static string MaskPhone(string? phone) =>
        string.IsNullOrEmpty(phone) ? string.Empty : "***" + Last4(phone);
}
