namespace Commerce.Domain.Audit;

/// <summary>
/// Immutable audit record for sensitive authentication, authorization, scope,
/// revocation, and synchronization decisions.
/// </summary>
public sealed record AuditEntry(
    Guid ActorId,
    Guid OrganizationId,
    Guid? BranchId,
    string Action,
    string Outcome,
    DateTimeOffset OccurredAtUtc,
    Guid CorrelationId,
    string? Reason = null);
