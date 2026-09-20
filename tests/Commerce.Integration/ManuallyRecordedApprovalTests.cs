using Commerce.Application.Payments;

namespace Commerce.Integration;

/// <summary>
/// Covers Unit 3 task 3.4 (commerce-payments design.md "Fail-closed approval"
/// row — "a manually-recorded payment is created already Approved because the
/// cash is physically in the drawer"): staff attestation returns
/// <see cref="PaymentApprovalOutcome.Approved"/> immediately, with no
/// external call.
/// </summary>
public sealed class ManuallyRecordedApprovalTests
{
    [Fact]
    public async Task Approve_AlwaysReturnsApproved_NoExternalCall()
    {
        IPaymentApprovalGateway gateway = new ManuallyRecordedApproval();

        var outcome = await gateway.Approve(Guid.NewGuid(), 50m, "Cash", CancellationToken.None);

        Assert.Equal(PaymentApprovalOutcome.Approved, outcome);
    }
}
