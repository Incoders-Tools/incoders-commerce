namespace Commerce.Application.Payments;

/// <summary>
/// Fail-closed binding when payment approval configuration is absent or
/// unreachable (commerce-payments design.md "Fail-closed approval", Decision
/// 2). Deliberately the INVERSE of <c>LogOnlyEmailSender</c>'s fail-open
/// substitution — this always returns <see cref="PaymentApprovalOutcome.Unavailable"/>,
/// never <see cref="PaymentApprovalOutcome.Approved"/>.
/// </summary>
public sealed class UnavailablePaymentApproval : IPaymentApprovalGateway
{
    public Task<PaymentApprovalOutcome> Approve(Guid subjectId, decimal amount, string method, CancellationToken ct) =>
        Task.FromResult(PaymentApprovalOutcome.Unavailable);
}
