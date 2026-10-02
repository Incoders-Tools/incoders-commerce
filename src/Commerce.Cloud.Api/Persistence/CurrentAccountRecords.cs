using System.Text.Json.Serialization;
using Commerce.Domain.CurrentAccounts;

namespace Commerce.Cloud.Api.Persistence;

/// <summary>A movement to append to a supplier's current account. `DueOn` null on an Invoice takes the supplier's payment terms.</summary>
public sealed record NewAccountMovement(
    Guid Id,
    AccountMovementKind Kind,
    AccountDirection Direction,
    decimal Amount,
    DateOnly OccurredOn,
    DateOnly? DueOn,
    string? DocumentReference,
    string Concept,
    Guid CreatedByUserId);

/// <summary>One persisted ledger movement (`current_account_movements`), as returned by the account endpoints.</summary>
public sealed record AccountMovementRecord(
    Guid Id,
    Guid SupplierId,
    [property: JsonConverter(typeof(JsonStringEnumConverter))] AccountMovementKind Kind,
    [property: JsonConverter(typeof(JsonStringEnumConverter))] AccountDirection Direction,
    decimal Amount,
    DateOnly OccurredOn,
    DateOnly? DueOn,
    string? DocumentReference,
    string Concept,
    Guid? ReversesMovementId,
    DateTimeOffset CreatedAtUtc,
    Guid CreatedByUserId)
{
    public AccountMovementFact ToFact() => new(Id, Kind, Direction, Amount, OccurredOn, DueOn, ReversesMovementId);
}

public enum ReverseMovementOutcome
{
    Reversed,
    NotFound,
    AlreadyReversed,
    NotReversible,
    BeforeOriginal,
}

/// <summary>Result of reversing a movement: `Reversal` is set only when `Outcome` is <see cref="ReverseMovementOutcome.Reversed"/>.</summary>
public sealed record ReverseMovementResult(ReverseMovementOutcome Outcome, AccountMovementRecord? Reversal = null);

/// <summary>Balance and overdue of one supplier account, as of a date.</summary>
public sealed record SupplierAccountBalance(Guid SupplierId, decimal Balance, decimal Overdue);
