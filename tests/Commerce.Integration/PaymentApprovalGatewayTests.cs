using Commerce.Application.Payments;

namespace Commerce.Integration;

/// <summary>
/// Covers Unit 3 task 3.2 (commerce-payments design.md "Fail-closed
/// approval"): with no gateway bound (or unreachable), the resolved
/// <see cref="IPaymentApprovalGateway"/> is
/// <see cref="UnavailablePaymentApproval"/>, and its outcome is
/// <see cref="PaymentApprovalOutcome.Unavailable"/> — never
/// <see cref="PaymentApprovalOutcome.Approved"/>, never a silent zero.
/// </summary>
public sealed class PaymentApprovalGatewayTests
{
    [Fact]
    public async Task UnavailablePaymentApproval_Approve_AlwaysReturnsUnavailable()
    {
        IPaymentApprovalGateway gateway = new UnavailablePaymentApproval();

        var outcome = await gateway.Approve(
            Guid.NewGuid(), 50m, "Cash", CancellationToken.None);

        Assert.Equal(PaymentApprovalOutcome.Unavailable, outcome);
    }
}
