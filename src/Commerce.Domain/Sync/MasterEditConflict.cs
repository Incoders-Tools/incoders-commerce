namespace Commerce.Domain.Sync;

/// <summary>
/// A non-commuting concurrent edit on a shared master aggregate. Both
/// confirmed histories are retained; last-write-wins is explicitly rejected
/// (ADR-002). Requires authorized review before resolution.
/// </summary>
public sealed record MasterEditConflict(
    Guid ConflictId,
    Guid AggregateId,
    SyncEnvelope Local,
    SyncEnvelope Remote,
    DateTimeOffset DetectedAtUtc)
{
    public ConflictResolution? Resolution { get; private set; }

    public void Resolve(ConflictResolution resolution) => Resolution = resolution;
}

public sealed record ConflictResolution(
    Guid ResolvedByActorId,
    string Decision,
    DateTimeOffset ResolvedAtUtc);
