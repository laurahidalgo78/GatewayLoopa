using ApiGatewayPay.Core.Common;
using static ApiGatewayPay.Core.Domain.Payments.PaymentStatus;

namespace ApiGatewayPay.Core.Domain.Payments;

/// <summary>
/// Transiciones permitidas. Cualquier otra es un error: impide, por ejemplo, confirmar un pago rechazado
/// o reenviar uno ya enviado (doble débito).
/// </summary>
/// <remarks>
/// Unknown = resultado ambiguo (timeout tras enviar). Solo vuelve a Approved (reenvío) cuando una
/// consulta de estado al banco confirma que NO tiene registro del EndToEndId.
/// </remarks>
public static class PaymentStateMachine
{
    private static readonly Dictionary<PaymentStatus, PaymentStatus[]> Allowed = new()
    {
        [Created] = [PendingApproval, Approved, Rejected],
        [PendingApproval] = [Approved, Rejected],
        [Approved] = [Sent, Failed],
        [Sent] = [Confirmed, Failed, Unknown],
        [Unknown] = [Confirmed, Failed, Approved],
        [Confirmed] = [Reversed],
        [Rejected] = [],
        [Failed] = [],
        [Reversed] = [],
    };

    public static bool CanTransition(PaymentStatus from, PaymentStatus to) =>
        Allowed.TryGetValue(from, out var targets) && targets.Contains(to);

    public static void EnsureCanTransition(PaymentStatus from, PaymentStatus to)
    {
        if (!CanTransition(from, to))
            throw AppException.Conflict("invalid_state_transition", $"El pago no puede pasar de {from} a {to}.");
    }

    public static bool IsTerminal(PaymentStatus status) => Allowed[status].Length == 0;

    public static IReadOnlyDictionary<PaymentStatus, PaymentStatus[]> Transitions => Allowed;
}
