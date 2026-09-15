namespace Commerce.Domain.Sync;

/// <summary>
/// Envelope shape for every branch/cloud synchronization operation (ADR-003).
/// Carries contract/tenant/branch/aggregate versions, actor, and correlation
/// so idempotency and conflict detection never rely on wall-clock ordering.
/// </summary>
public sealed record SyncEnvelope(
    Guid OperationId,
    int ContractVersion,
    Guid OrganizationId,
    Guid BranchId,
    Guid AggregateId,
    long AggregateVersion,
    Guid ActorId,
    Guid CorrelationId,
    DateTimeOffset OccurredAtUtc,
    string PayloadKind,
    string Payload);
