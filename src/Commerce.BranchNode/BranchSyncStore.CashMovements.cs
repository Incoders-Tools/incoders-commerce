using System.Globalization;
using Commerce.Domain.CashSessions;
using Commerce.Domain.Discounts;
using Commerce.Domain.Sync;
using Microsoft.Data.Sqlite;

namespace Commerce.BranchNode;

/// <summary>A withdrawal or deposit of cash of a session, outside a sale (<see cref="CashMovement"/>).</summary>
public sealed record CashMovementRecord(
    Guid MovementId,
    Guid CashSessionId,
    string Kind,
    string Counterpart,
    decimal Amount,
    string Reason,
    DateTimeOffset OccurredAtUtc,
    Guid OperatorId,
    DiscountAuthorization? Authorization)
{
    public string Description => CashMovement.Describe(Kind, Counterpart);
}

public enum CashMovementOutcome
{
    Recorded,
    NoOpenCashSession,

    /// <summary>A withdrawal larger than the cash the drawer should hold.</summary>
    ExceedsExpectedCash,
}

/// <summary>The outcome of a cash movement; <see cref="Movement"/> is set when it was recorded.</summary>
public sealed record CashMovementResult(CashMovementOutcome Outcome, CashMovementRecord? Movement);

/// <summary>
/// Cash movements of the drawer (<c>cash_movements</c>): recorded with their <c>cash-movement.recorded</c> envelope in one
/// transaction, only in the open session, and never more out than the drawer should hold. They are not voided: a mistake
/// is corrected with the opposite movement, so the drawer's history stays as it happened.
/// </summary>
public sealed partial class BranchSyncStore
{
    private void EnsureCashMovementStorageExists()
    {
        using (var create = _connection.CreateCommand())
        {
            create.CommandText = """
                CREATE TABLE IF NOT EXISTS cash_movements (
                    movement_id TEXT PRIMARY KEY,
                    cash_session_id TEXT NOT NULL,
                    kind TEXT NOT NULL,
                    counterpart TEXT NOT NULL,
                    amount TEXT NOT NULL,
                    reason TEXT NOT NULL,
                    occurred_at_utc TEXT NOT NULL,
                    operator_id TEXT NOT NULL,
                    auth_method TEXT NULL,
                    auth_operator_id TEXT NULL,
                    auth_pin_version TEXT NULL,
                    operation_id TEXT NOT NULL
                );
                CREATE INDEX IF NOT EXISTS ix_cash_movements_session ON cash_movements (cash_session_id);
                CREATE INDEX IF NOT EXISTS ix_cash_movements_occurred ON cash_movements (occurred_at_utc);
                """;
            create.ExecuteNonQuery();
        }

        EnsureColumns("cash_sessions", "cash_withdrawn", "cash_deposited", "cash_movement_count");
    }

    /// <summary>Records a movement of the open session and queues its envelope, in one transaction.</summary>
    public CashMovementOutcome RecordCashMovement(CashMovementRecord movement, SyncEnvelope envelope)
    {
        lock (_writeGate)
        {
            using var transaction = _connection.BeginTransaction();
            if (!IsCashSessionOpen(movement.CashSessionId, transaction))
            {
                transaction.Rollback();
                return CashMovementOutcome.NoOpenCashSession;
            }

            if (movement.Kind == CashMovement.Withdrawal
                && movement.Amount > SummarizeCashSession(movement.CashSessionId, transaction)!.ExpectedCash)
            {
                transaction.Rollback();
                return CashMovementOutcome.ExceedsExpectedCash;
            }

            using (var insert = _connection.CreateCommand())
            {
                insert.Transaction = transaction;
                insert.CommandText = """
                    INSERT INTO cash_movements
                        (movement_id, cash_session_id, kind, counterpart, amount, reason, occurred_at_utc, operator_id,
                         auth_method, auth_operator_id, auth_pin_version, operation_id)
                    VALUES ($id, $session, $kind, $counterpart, $amount, $reason, $at, $operator, $authMethod, $authOperator,
                            $pinVersion, $operation);
                    """;
                insert.Parameters.AddWithValue("$id", movement.MovementId.ToString());
                insert.Parameters.AddWithValue("$session", movement.CashSessionId.ToString());
                insert.Parameters.AddWithValue("$kind", movement.Kind);
                insert.Parameters.AddWithValue("$counterpart", movement.Counterpart);
                insert.Parameters.AddWithValue("$amount", movement.Amount.ToString(CultureInfo.InvariantCulture));
                insert.Parameters.AddWithValue("$reason", movement.Reason);
                insert.Parameters.AddWithValue("$at", movement.OccurredAtUtc.ToUniversalTime().ToString("O"));
                insert.Parameters.AddWithValue("$operator", movement.OperatorId.ToString());
                insert.Parameters.AddWithValue("$authMethod", (object?)movement.Authorization?.Method ?? DBNull.Value);
                insert.Parameters.AddWithValue("$authOperator", (object?)movement.Authorization?.OperatorId.ToString() ?? DBNull.Value);
                insert.Parameters.AddWithValue(
                    "$pinVersion", (object?)movement.Authorization?.PinVersion.ToString(CultureInfo.InvariantCulture) ?? DBNull.Value);
                insert.Parameters.AddWithValue("$operation", envelope.OperationId.ToString());
                insert.ExecuteNonQuery();
            }

            InsertSyncOutboxRow(envelope, transaction);
            transaction.Commit();
            return CashMovementOutcome.Recorded;
        }
    }

    /// <summary>The cash movements in [<paramref name="fromUtc"/>, <paramref name="toUtc"/>), newest first.</summary>
    public IReadOnlyList<CashMovementRecord> ListCashMovements(DateTimeOffset fromUtc, DateTimeOffset toUtc) =>
        ReadCashMovements(
            "occurred_at_utc >= $from AND occurred_at_utc < $to", null,
            ("$from", fromUtc.ToUniversalTime().ToString("O")), ("$to", toUtc.ToUniversalTime().ToString("O")));

    private List<CashSessionCashMovement> ReadSessionCashMovements(Guid sessionId, SqliteTransaction? transaction) =>
        ReadCashMovements("cash_session_id = $id", transaction, ("$id", sessionId.ToString()))
            .Select(movement => new CashSessionCashMovement(movement.Kind, movement.Amount))
            .ToList();

    private List<CashMovementRecord> ReadCashMovements(string where, SqliteTransaction? transaction, params (string Name, object Value)[] parameters)
    {
        using var command = _connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = $"""
            SELECT movement_id, cash_session_id, kind, counterpart, amount, reason, occurred_at_utc, operator_id,
                   auth_method, auth_operator_id, auth_pin_version
            FROM cash_movements
            WHERE {where}
            ORDER BY occurred_at_utc DESC;
            """;
        foreach (var (name, value) in parameters)
        {
            command.Parameters.AddWithValue(name, value);
        }

        var movements = new List<CashMovementRecord>();
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            movements.Add(new CashMovementRecord(
                Guid.Parse(reader.GetString(0)),
                Guid.Parse(reader.GetString(1)),
                reader.GetString(2),
                reader.GetString(3),
                decimal.Parse(reader.GetString(4), CultureInfo.InvariantCulture),
                reader.GetString(5),
                DateTimeOffset.Parse(reader.GetString(6), CultureInfo.InvariantCulture),
                Guid.Parse(reader.GetString(7)),
                reader.IsDBNull(8)
                    ? null
                    : new DiscountAuthorization(
                        reader.GetString(8), Guid.Parse(reader.GetString(9)), long.Parse(reader.GetString(10), CultureInfo.InvariantCulture))));
        }

        return movements;
    }
}
