using System.Text.Json.Serialization;
using Commerce.Domain.Stock;

namespace Commerce.Cloud.Api.Persistence;

/// <summary>
/// On-hand of one presentation of the selected branch (derived: SUM of its movements). `BelowMinimum` needs a minimum
/// and `OnHand` strictly under it (negative stock counts); `Shortfall` is minimum - on hand when below.
/// </summary>
public sealed record StockLevelRecord(
    Guid PresentationId,
    Guid ProductId,
    string ProductName,
    string PresentationName,
    string QuantityBehavior,
    Guid UnitId,
    string? IdentificationCode,
    decimal OnHand,
    decimal? MinimumQuantity,
    bool BelowMinimum,
    decimal? Shortfall,
    DateTimeOffset? LastMovementAtUtc);

public sealed record StockLevelFilter(string? Search, bool OnlyBelowMinimum);

/// <summary>One ledger row. `SourceNumber` is the human number of the source document (the `R...` of a reception) when there is one; `BalanceAfter` is the running on-hand after it.</summary>
public sealed record StockMovementRecord(
    Guid Id,
    [property: JsonConverter(typeof(JsonStringEnumConverter))] StockMovementKind Kind,
    decimal Quantity,
    DateTimeOffset OccurredAtUtc,
    string? Reason,
    string? LotCode,
    string? SourceType,
    Guid? SourceId,
    string? SourceNumber,
    Guid? ReversesMovementId,
    Guid? CreatedByUserId,
    decimal BalanceAfter);

public sealed record StockHistoryPage(
    Guid PresentationId, decimal OnHand, int Total, int Page, int PageSize, IReadOnlyList<StockMovementRecord> Items);

public sealed record StockAdjustmentResult(StockMovementRecord Movement, decimal OnHand);

public sealed record StockMinimumRecord(Guid PresentationId, decimal? MinimumQuantity, DateTimeOffset? UpdatedAtUtc);
