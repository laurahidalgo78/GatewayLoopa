using ApiGatewayPay.Core.Common;
using ApiGatewayPay.Core.Domain.Payments;
using ApiGatewayPay.Core.Fraud;
using ApiGatewayPay.Core.Ports.Persistence;
using static ApiGatewayPay.Core.Domain.Payments.PaymentStatus;

namespace ApiGatewayPay.Tests.Unit;

public class PaymentStateMachineTests
{
    [Theory]
    [InlineData(Created, Approved)]
    [InlineData(Created, PendingApproval)]
    [InlineData(PendingApproval, Approved)]
    [InlineData(Approved, Sent)]
    [InlineData(Sent, Unknown)]
    [InlineData(Unknown, Confirmed)]
    [InlineData(Unknown, Approved)]
    [InlineData(Confirmed, Reversed)]
    public void Allows_valid_transitions(PaymentStatus from, PaymentStatus to) =>
        Assert.True(PaymentStateMachine.CanTransition(from, to));

    [Theory]
    [InlineData(Created, Sent)]           // no se envía sin aprobación
    [InlineData(PendingApproval, Sent)]
    [InlineData(Sent, Approved)]          // un pago enviado no vuelve a la cola (reenvío ciego)
    [InlineData(Confirmed, Failed)]
    [InlineData(Rejected, Approved)]
    [InlineData(Failed, Sent)]
    [InlineData(Reversed, Confirmed)]
    public void Rejects_invalid_transitions(PaymentStatus from, PaymentStatus to)
    {
        Assert.False(PaymentStateMachine.CanTransition(from, to));
        var ex = Assert.Throws<AppException>(() => PaymentStateMachine.EnsureCanTransition(from, to));
        Assert.Equal("invalid_state_transition", ex.Code);
    }

    [Theory]
    [InlineData(Rejected)]
    [InlineData(Failed)]
    [InlineData(Reversed)]
    public void Terminal_states_have_no_exit(PaymentStatus status) => Assert.True(PaymentStateMachine.IsTerminal(status));
}

public class PaymentMakerCheckerTests
{
    private static readonly DateTime Now = new(2026, 1, 1, 12, 0, 0, DateTimeKind.Utc);
    private static AuditActor User(string id, bool mfa = true, string tenant = "t1") => new(id, ActorType.User, tenant, "10.0.0.1", "ua", "corr-123456", mfa);

    private static Payment NewPendingPayment()
    {
        var (p, _) = Payment.Create(Guid.NewGuid(), User("maker"), Guid.NewGuid(), new Money(100, "COP"), "ref", "key-1234567890123456", "hash", Now);
        p.RequireApproval(["high_amount"], Now);
        return p;
    }

    [Fact]
    public void Maker_cannot_approve_own_payment()
    {
        var ex = Assert.Throws<AppException>(() => NewPendingPayment().Approve(User("maker"), Now));
        Assert.Equal("maker_checker_violation", ex.Code);
    }

    [Fact]
    public void Approval_requires_strong_authentication()
    {
        var ex = Assert.Throws<AppException>(() => NewPendingPayment().Approve(User("checker", mfa: false), Now));
        Assert.Equal("step_up_required", ex.Code);
    }

    [Fact]
    public void Api_keys_can_never_approve()
    {
        var machine = new AuditActor("apikey:1", ActorType.ApiKey, "t1", null, null, "corr-123456", true);
        Assert.Throws<AppException>(() => NewPendingPayment().Approve(machine, Now));
    }

    [Fact]
    public void Checker_from_other_tenant_sees_not_found()
    {
        var ex = Assert.Throws<AppException>(() => NewPendingPayment().Approve(User("checker", tenant: "t2"), Now));
        Assert.Equal(ErrorKind.NotFound, ex.Kind);
    }

    [Fact]
    public void Every_mutation_increments_version_for_the_audit_chain()
    {
        var p = NewPendingPayment();
        Assert.Equal(2, p.Version);
        p.Approve(User("checker"), Now);
        p.MarkSent(Now);
        Assert.Equal(4, p.Version);
        Assert.Equal(1, p.SubmissionAttempts);
        Assert.Equal(p.Id.ToString("N"), p.EndToEndId);
    }

    [Theory]
    [InlineData(0, "COP")]
    [InlineData(-5, "COP")]
    [InlineData(10, "cop")]
    [InlineData(10, "COPX")]
    public void Money_rejects_invalid_values(long amount, string currency) =>
        Assert.Throws<AppException>(() => new Money(amount, currency));
}

public class FraudEngineTests
{
    private static FraudEngine Engine() => new(new StaticMonitor<FraudOptions>(new FraudOptions
    {
        Limits = { ["COP"] = new CurrencyLimits { MaxPerTransactionMinor = 1_000_000, DailyMaxMinor = 2_000_000, ApprovalThresholdMinor = 500_000, FirstPaymentReviewMinor = 100_000 } },
        VelocityMaxPayments = 3,
        AnomalyFactor = 5,
        AnomalyMinHistory = 3,
    }));

    private static TenantPaymentStats Stats(int count = 0, long sum = 0, long[]? recent = null, bool paid = true) => new(count, sum, recent ?? [], paid);

    [Fact]
    public void Blocks_unconfigured_currency() =>
        Assert.Equal(FraudOutcome.Block, Engine().Evaluate(new Money(10, "EUR"), Stats()).Outcome);

    [Fact]
    public void Blocks_over_per_transaction_limit() =>
        Assert.Contains("per_transaction_limit", Engine().Evaluate(new Money(1_000_001, "COP"), Stats()).Reasons);

    [Fact]
    public void Blocks_over_daily_limit() =>
        Assert.Contains("daily_limit", Engine().Evaluate(new Money(600_000, "COP"), Stats(sum: 1_500_000)).Reasons);

    [Fact]
    public void Blocks_on_velocity() =>
        Assert.Contains("velocity_limit", Engine().Evaluate(new Money(10, "COP"), Stats(count: 3)).Reasons);

    [Fact]
    public void High_amount_requires_approval()
    {
        var d = Engine().Evaluate(new Money(500_000, "COP"), Stats());
        Assert.Equal(FraudOutcome.RequireApproval, d.Outcome);
        Assert.Contains("high_amount", d.Reasons);
    }

    [Fact]
    public void Anomalous_amount_requires_approval() =>
        Assert.Contains("amount_anomaly", Engine().Evaluate(new Money(60_000, "COP"), Stats(recent: [10_000, 10_000, 10_000])).Reasons);

    [Fact]
    public void First_payment_to_beneficiary_over_threshold_requires_approval() =>
        Assert.Contains("first_payment_to_beneficiary", Engine().Evaluate(new Money(150_000, "COP"), Stats(paid: false)).Reasons);

    [Fact]
    public void Normal_payment_is_allowed() =>
        Assert.Equal(FraudOutcome.Allow, Engine().Evaluate(new Money(50_000, "COP"), Stats(recent: [40_000, 50_000, 60_000])).Outcome);
}
