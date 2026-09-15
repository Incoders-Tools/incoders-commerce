namespace Commerce.Domain.Sync;

/// <summary>
/// The durable business effect of a local sale. Branch-owned per ADR-002:
/// committed once, atomically, alongside its outbox synchronization record.
/// </summary>
public sealed record SaleEffect(
    Guid SaleId,
    Guid BranchId,
    decimal TotalAmount,
    DateTimeOffset OccurredAtUtc);
