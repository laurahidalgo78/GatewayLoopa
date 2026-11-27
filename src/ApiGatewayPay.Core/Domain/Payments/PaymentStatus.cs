namespace ApiGatewayPay.Core.Domain.Payments;

public enum PaymentStatus
{
    Created,
    PendingApproval,
    Approved,
    Rejected,
    Sent,
    Unknown,
    Confirmed,
    Failed,
    Reversed,
}
