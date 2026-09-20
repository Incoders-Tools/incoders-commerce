namespace Commerce.Application.Payments;

/// <summary>
/// Fail-closed: <see cref="PaymentApprovalOutcome.Unavailable"/> is a
/// first-class outcome, never coerced to <see cref="PaymentApprovalOutcome.Approved"/>
/// (commerce-payments design.md "Fail-closed approval", Decision 2).
/// </summary>
public enum PaymentApprovalOutcome
{
    Approved,
    Declined,
    Unavailable
}

/// <summary>
/// The provider boundary. Exactly one implementation ships this phase
/// (<see cref="ManuallyRecordedApproval"/>); absent/unreachable configuration
/// binds <see cref="UnavailablePaymentApproval"/> instead — never a silent
/// approval, never a silent zero.
/// </summary>
public interface IPaymentApprovalGateway
{
    Task<PaymentApprovalOutcome> Approve(Guid subjectId, decimal amount, string method, CancellationToken ct);
}
