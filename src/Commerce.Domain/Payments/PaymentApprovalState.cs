namespace Commerce.Domain.Payments;

/// <summary>
/// Terminal-on-write state of a single <see cref="PaymentEntry"/>
/// (commerce-payments design.md "Aggregate shape"). Domain-layer type,
/// distinct from <c>Commerce.Application.Payments.PaymentApprovalOutcome</c>
/// (Unit 3) — Domain never references Application (no other Domain type in
/// this repo does either), so <c>PaymentRecordingService</c> maps the
/// application-layer outcome onto this domain-layer state when constructing
/// an entry.
/// </summary>
public enum PaymentApprovalState
{
    Approved,
    Declined,
    Unavailable
}
