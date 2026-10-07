using System.Globalization;
using Commerce.Domain.CashSessions;
using Commerce.Domain.Sales;
using Commerce.Domain.Sync;
using Microsoft.Data.Sqlite;

namespace Commerce.BranchNode;

/// <summary>
/// Cash session storage of the branch database (pos-cash-session): the
/// <c>cash_sessions</c> table, the <c>sale_effects.cash_session_id</c> link, and
/// the atomic open/close operations that write the session row and its outbox
/// envelope in one transaction. Additive and idempotent like the other storage
/// extensions, so a <c>branch.db</c> from before sessions opens and upgrades.
/// "One open session per terminal" is enforced by the database itself with a
/// partial unique index, not only by the code that checks first.
/// </summary>
public sealed partial class BranchSyncStore
{
    private const string CashSessionColumns = """
        session_id, organization_id, branch_id, opened_by_operator_id, opened_at_utc, opening_float,
        closed_at_utc, closed_by_operator_id, sale_count, cash_kept, card_total, qr_total, untendered_total, counted_cash,
        account_total, collected_cash, collected_card, collected_qr, collection_count, cash_withdrawn, cash_deposited,
        cash_movement_count
        """;

    private void EnsureCashSessionStorageExists()
    {
        using (var create = _connection.CreateCommand())
        {
            create.CommandText = """
                CREATE TABLE IF NOT EXISTS cash_sessions (
                    session_id TEXT PRIMARY KEY,
                    organization_id TEXT NOT NULL,
                    branch_id TEXT NOT NULL,
                    opened_by_operator_id TEXT NOT NULL,
                    opened_at_utc TEXT NOT NULL,
                    opening_float TEXT NOT NULL,
                    closed_at_utc TEXT NULL,
                    closed_by_operator_id TEXT NULL,
                    sale_count INTEGER NULL,
                    cash_kept TEXT NULL,
                    card_total TEXT NULL,
                    qr_total TEXT NULL,
                    untendered_total TEXT NULL,
                    counted_cash TEXT NULL
                );
                CREATE UNIQUE INDEX IF NOT EXISTS ux_cash_sessions_one_open
                    ON cash_sessions ((1)) WHERE closed_at_utc IS NULL;
                """;
            create.ExecuteNonQuery();
        }

        EnsureColumns("sale_effects", "cash_session_id");
        // Sales on customers' current accounts: their own total, never part of the cash expected in the drawer.
        EnsureColumns("cash_sessions", "account_total");

        using var index = _connection.CreateCommand();
        index.CommandText = "CREATE INDEX IF NOT EXISTS ix_sale_effects_cash_session ON sale_effects (cash_session_id);";
        index.ExecuteNonQuery();
    }

    /// <summary>The open session of this terminal, or null when none is open.</summary>
    public CashSession? GetOpenCashSession() => ReadCashSession("closed_at_utc IS NULL", null, transaction: null);

    public CashSession? GetCashSession(Guid sessionId) => ReadCashSession("session_id = $id", sessionId, transaction: null);

    /// <summary>
    /// Totals of a session computed from its sales' recorded tenders (null when
    /// the session is unknown). Voided sales do not count. Sales freeze once the
    /// session closes, so the same computation serves the live close dialog and
    /// the recorded close.
    /// </summary>
    public CashSessionSummary? GetCashSessionSummary(Guid sessionId) => SummarizeCashSession(sessionId, transaction: null);

    private bool IsCashSessionOpen(Guid? sessionId, SqliteTransaction transaction)
    {
        if (sessionId is not { } id)
        {
            return false;
        }

        using var command = _connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "SELECT COUNT(1) FROM cash_sessions WHERE session_id = $id AND closed_at_utc IS NULL;";
        command.Parameters.AddWithValue("$id", id.ToString());
        return (long)command.ExecuteScalar()! > 0;
    }

    /// <summary>
    /// Inserts the open session and its outbox envelope in one transaction.
    /// Returns <see cref="CashSessionOpenOutcome.AlreadyOpen"/> with the session
    /// already open (nothing written) when the terminal has one.
    /// </summary>
    public CashSessionOpenResult OpenCashSession(CashSession session, SyncEnvelope envelope)
    {
        lock (_writeGate)
        {
            using var transaction = _connection.BeginTransaction();

            var open = ReadCashSession("closed_at_utc IS NULL", null, transaction);
            if (open is not null)
            {
                transaction.Rollback();
                return new CashSessionOpenResult(CashSessionOpenOutcome.AlreadyOpen, open);
            }

            using (var insert = _connection.CreateCommand())
            {
                insert.Transaction = transaction;
                insert.CommandText = """
                    INSERT INTO cash_sessions
                        (session_id, organization_id, branch_id, opened_by_operator_id, opened_at_utc, opening_float)
                    VALUES ($id, $organizationId, $branchId, $operatorId, $openedAt, $openingFloat);
                    """;
                insert.Parameters.AddWithValue("$id", session.SessionId.ToString());
                insert.Parameters.AddWithValue("$organizationId", session.OrganizationId.ToString());
                insert.Parameters.AddWithValue("$branchId", session.BranchId.ToString());
                insert.Parameters.AddWithValue("$operatorId", session.OpenedByOperatorId.ToString());
                insert.Parameters.AddWithValue("$openedAt", session.OpenedAtUtc.ToString("O"));
                insert.Parameters.AddWithValue("$openingFloat", session.OpeningFloat.ToString(CultureInfo.InvariantCulture));
                insert.ExecuteNonQuery();
            }

            InsertSyncOutboxRow(envelope, transaction);
            transaction.Commit();
            return new CashSessionOpenResult(CashSessionOpenOutcome.Opened, session);
        }
    }

    /// <summary>
    /// Closes the session: computes the totals from its sales, records them with
    /// the counted cash, and queues the envelope built by
    /// <paramref name="buildEnvelope"/> from the closed session, all in one
    /// transaction (a sale commit cannot interleave: writers are serialized).
    /// </summary>
    public CashSessionCloseResult CloseCashSession(
        Guid sessionId,
        Guid closedByOperatorId,
        decimal countedCash,
        DateTimeOffset closedAtUtc,
        Func<CashSession, SyncEnvelope> buildEnvelope)
    {
        lock (_writeGate)
        {
            using var transaction = _connection.BeginTransaction();

            var session = ReadCashSession("session_id = $id", sessionId, transaction);
            if (session is null)
            {
                transaction.Rollback();
                return new CashSessionCloseResult(CashSessionCloseOutcome.NotFound, null);
            }

            if (!session.IsOpen)
            {
                transaction.Rollback();
                return new CashSessionCloseResult(CashSessionCloseOutcome.AlreadyClosed, session);
            }

            var summary = SummarizeCashSession(sessionId, transaction)!;
            using (var update = _connection.CreateCommand())
            {
                update.Transaction = transaction;
                update.CommandText = """
                    UPDATE cash_sessions
                    SET closed_at_utc = $closedAt, closed_by_operator_id = $operatorId, sale_count = $saleCount,
                        cash_kept = $cashKept, card_total = $cardTotal, qr_total = $qrTotal,
                        untendered_total = $untendered, counted_cash = $counted, account_total = $account,
                        collected_cash = $collectedCash, collected_card = $collectedCard, collected_qr = $collectedQr,
                        collection_count = $collectionCount, cash_withdrawn = $withdrawn, cash_deposited = $deposited,
                        cash_movement_count = $movementCount
                    WHERE session_id = $id AND closed_at_utc IS NULL;
                    """;
                update.Parameters.AddWithValue("$id", sessionId.ToString());
                update.Parameters.AddWithValue("$closedAt", closedAtUtc.ToString("O"));
                update.Parameters.AddWithValue("$operatorId", closedByOperatorId.ToString());
                update.Parameters.AddWithValue("$saleCount", summary.SaleCount);
                update.Parameters.AddWithValue("$cashKept", summary.CashKept.ToString(CultureInfo.InvariantCulture));
                update.Parameters.AddWithValue("$cardTotal", summary.CardTotal.ToString(CultureInfo.InvariantCulture));
                update.Parameters.AddWithValue("$qrTotal", summary.QrTotal.ToString(CultureInfo.InvariantCulture));
                update.Parameters.AddWithValue("$untendered", summary.UntenderedTotal.ToString(CultureInfo.InvariantCulture));
                update.Parameters.AddWithValue("$counted", countedCash.ToString(CultureInfo.InvariantCulture));
                update.Parameters.AddWithValue("$account", summary.AccountTotal.ToString(CultureInfo.InvariantCulture));
                update.Parameters.AddWithValue("$collectedCash", summary.CollectedCash.ToString(CultureInfo.InvariantCulture));
                update.Parameters.AddWithValue("$collectedCard", summary.CollectedCard.ToString(CultureInfo.InvariantCulture));
                update.Parameters.AddWithValue("$collectedQr", summary.CollectedQr.ToString(CultureInfo.InvariantCulture));
                update.Parameters.AddWithValue("$collectionCount", summary.CollectionCount.ToString(CultureInfo.InvariantCulture));
                update.Parameters.AddWithValue("$withdrawn", summary.CashWithdrawn.ToString(CultureInfo.InvariantCulture));
                update.Parameters.AddWithValue("$deposited", summary.CashDeposited.ToString(CultureInfo.InvariantCulture));
                update.Parameters.AddWithValue("$movementCount", summary.CashMovementCount.ToString(CultureInfo.InvariantCulture));
                update.ExecuteNonQuery();
            }

            var closed = session with
            {
                Closure = new CashSessionClosure(closedByOperatorId, closedAtUtc, summary, countedCash),
            };
            InsertSyncOutboxRow(buildEnvelope(closed), transaction);
            transaction.Commit();
            return new CashSessionCloseResult(CashSessionCloseOutcome.Closed, closed);
        }
    }

    private CashSessionSummary? SummarizeCashSession(Guid sessionId, SqliteTransaction? transaction)
    {
        using var find = _connection.CreateCommand();
        find.Transaction = transaction;
        find.CommandText = "SELECT opening_float FROM cash_sessions WHERE session_id = $id;";
        find.Parameters.AddWithValue("$id", sessionId.ToString());
        if (find.ExecuteScalar() is not string openingFloat)
        {
            return null;
        }

        using var command = _connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT total_amount, tender_method, tender_amount_received, tender_change
            FROM sale_effects
            WHERE cash_session_id = $id AND sale_id NOT IN (SELECT sale_id FROM sale_voids);
            """;
        command.Parameters.AddWithValue("$id", sessionId.ToString());

        var sales = new List<CashSessionSale>();
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            var tender = reader.IsDBNull(1)
                ? null
                : new SaleTender(reader.GetString(1), ReadDecimalOrNull(reader, 2), ReadDecimalOrNull(reader, 3));
            sales.Add(new CashSessionSale(decimal.Parse(reader.GetString(0), CultureInfo.InvariantCulture), tender));
        }

        var collections = ReadSessionCollections(sessionId, transaction)
            .Select(row => new CashSessionCollection(row.Amount, new SaleTender(row.Method)));
        return CashSessionMath.Summarize(
            decimal.Parse(openingFloat, CultureInfo.InvariantCulture), sales, collections,
            ReadSessionCashMovements(sessionId, transaction));
    }

    private CashSession? ReadCashSession(string where, Guid? id, SqliteTransaction? transaction)
    {
        using var command = _connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = $"SELECT {CashSessionColumns} FROM cash_sessions WHERE {where};";
        if (id is { } value)
        {
            command.Parameters.AddWithValue("$id", value.ToString());
        }

        using var reader = command.ExecuteReader();
        if (!reader.Read())
        {
            return null;
        }

        var openingFloat = decimal.Parse(reader.GetString(5), CultureInfo.InvariantCulture);
        CashSessionClosure? closure = null;
        if (!reader.IsDBNull(6))
        {
            var summary = new CashSessionSummary(
                openingFloat,
                reader.GetInt32(8),
                decimal.Parse(reader.GetString(9), CultureInfo.InvariantCulture),
                decimal.Parse(reader.GetString(10), CultureInfo.InvariantCulture),
                decimal.Parse(reader.GetString(11), CultureInfo.InvariantCulture),
                decimal.Parse(reader.GetString(12), CultureInfo.InvariantCulture),
                reader.IsDBNull(14) ? 0m : decimal.Parse(reader.GetString(14), CultureInfo.InvariantCulture),
                reader.IsDBNull(15) ? 0m : decimal.Parse(reader.GetString(15), CultureInfo.InvariantCulture),
                reader.IsDBNull(16) ? 0m : decimal.Parse(reader.GetString(16), CultureInfo.InvariantCulture),
                reader.IsDBNull(17) ? 0m : decimal.Parse(reader.GetString(17), CultureInfo.InvariantCulture),
                reader.IsDBNull(18) ? 0 : int.Parse(reader.GetString(18), CultureInfo.InvariantCulture),
                reader.IsDBNull(19) ? 0m : decimal.Parse(reader.GetString(19), CultureInfo.InvariantCulture),
                reader.IsDBNull(20) ? 0m : decimal.Parse(reader.GetString(20), CultureInfo.InvariantCulture),
                reader.IsDBNull(21) ? 0 : int.Parse(reader.GetString(21), CultureInfo.InvariantCulture));
            closure = new CashSessionClosure(
                Guid.Parse(reader.GetString(7)),
                DateTimeOffset.Parse(reader.GetString(6), CultureInfo.InvariantCulture),
                summary,
                decimal.Parse(reader.GetString(13), CultureInfo.InvariantCulture));
        }

        return new CashSession(
            Guid.Parse(reader.GetString(0)),
            Guid.Parse(reader.GetString(1)),
            Guid.Parse(reader.GetString(2)),
            Guid.Parse(reader.GetString(3)),
            DateTimeOffset.Parse(reader.GetString(4), CultureInfo.InvariantCulture),
            openingFloat,
            closure);
    }
}
