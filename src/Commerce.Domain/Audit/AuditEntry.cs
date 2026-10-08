namespace Commerce.Domain.Audit;

/// <summary>
/// Immutable audit record for sensitive authentication, authorization, scope,
/// revocation, and synchronization decisions.
/// The <see cref="ActorKind"/> is required so every call site states who acted;
/// a durable sink stores it as the audit row's actor kind.
/// </summary>
public sealed record AuditEntry(
    Guid ActorId,
    AuditActorKind ActorKind,
    Guid OrganizationId,
    Guid? BranchId,
    string Action,
    string Outcome,
    DateTimeOffset OccurredAtUtc,
    Guid CorrelationId,
    string? Reason = null);
