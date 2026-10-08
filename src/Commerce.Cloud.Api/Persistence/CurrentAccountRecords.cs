using System.Text.Json.Serialization;
using Commerce.Domain.CurrentAccounts;

namespace Commerce.Cloud.Api.Persistence;

/// <summary>Whose account: a supplier or a customer of the organization.</summary>
public sealed record AccountParty(AccountPartyKind Kind, Guid Id)
{
    public static AccountParty Supplier(Guid supplierId) => new(AccountPartyKind.Supplier, supplierId);

    public static AccountParty Customer(Guid customerId) => new(AccountPartyKind.Customer, customerId);

    public static AccountParty Employee(Guid employeeId) => new(AccountPartyKind.Employee, employeeId);

    /// <summary>The audit entity type and the action prefix ("supplier", "customer", "employee").</summary>
    public string EntityType => Kind switch
    {
        AccountPartyKind.Supplier => "supplier",
        AccountPartyKind.Customer => "customer",
        _ => "employee",
    };
}

/// <summary>
/// A movement to append to a current account. `DueOn` null on a supplier Invoice takes the supplier's payment terms.
/// `Direction` is in the party's own terms (see <see cref="PartyAccountRules"/>).
/// </summary>
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

/// <summary>
/// One persisted ledger movement (`current_account_movements`), as returned by the account endpoints. Exactly one of
/// <see cref="SupplierId"/> and <see cref="CustomerId"/> is set: the party the movement belongs to.
/// </summary>
public sealed record AccountMovementRecord(
    Guid Id,
    Guid? SupplierId,
    [property: JsonConverter(typeof(JsonStringEnumConverter))] AccountMovementKind Kind,
    [property: JsonConverter(typeof(JsonStringEnumConverter))] AccountDirection Direction,
    decimal Amount,
    DateOnly OccurredOn,
    DateOnly? DueOn,
    string? DocumentReference,
    string Concept,
    Guid? ReversesMovementId,
    DateTimeOffset CreatedAtUtc,
    Guid CreatedByUserId,
    Guid? CustomerId = null,
    Guid? EmployeeId = null)
{
    [JsonIgnore]
    public AccountParty Party => SupplierId is { } supplier
        ? AccountParty.Supplier(supplier)
        : CustomerId is { } customer ? AccountParty.Customer(customer) : AccountParty.Employee(EmployeeId!.Value);

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

/// <summary>Balance (what the customer owes) and overdue of one customer account, as of a date.</summary>
public sealed record CustomerAccountBalance(Guid CustomerId, decimal Balance, decimal Overdue);

/// <summary>An employee's balance: what the business owes it (negative: what the employee owes the business).</summary>
public sealed record EmployeeAccountBalance(Guid EmployeeId, decimal Balance);
