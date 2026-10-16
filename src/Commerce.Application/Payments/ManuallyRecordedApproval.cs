namespace Commerce.Application.Payments;

/// <summary>
/// The only real implementation this phase (commerce-payments design.md
/// "Fail-closed approval" row): a staff attestation that cash/credit is
/// already physically in hand. Always <see cref="PaymentApprovalOutcome.Approved"/>,
/// immediately, with no external call.
/// </summary>
public sealed class ManuallyRecordedApproval : IPaymentApprovalGateway
{
    public Task<PaymentApprovalOutcome> Approve(Guid subjectId, decimal amount, string method, CancellationToken ct) =>
        Task.FromResult(PaymentApprovalOutcome.Approved);
}
