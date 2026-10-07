using System.Globalization;
using Commerce.Domain.Discounts;
using Commerce.Domain.Sales;
using Commerce.Domain.Sync;
using Commerce.Domain.Tenancy;
using Microsoft.Data.Sqlite;

namespace Commerce.BranchNode;

/// <summary>One committed sale as the POS sales history lists it, voided or not.</summary>
public sealed record SaleHistoryEntry(
    Guid SaleId,
    string? Number,
    DateTimeOffset OccurredAtUtc,
    decimal TotalAmount,
    string SaleKind,
    Guid? CustomerId,
    string? CustomerName,
    SaleTender? Tender,
    Guid? CashSessionId,
    int LineCount,
    SaleVoidRecord? Void);

/// <summary>How and why a sale was voided. The sale itself is never changed.</summary>
public sealed record SaleVoidRecord(
    Guid SaleId,
    Guid? CashSessionId,
    DateTimeOffset VoidedAtUtc,
    Guid VoidedByOperatorId,
    DiscountAuthorization Authorization,
    string Reason,
    Guid OperationId);

public enum SaleVoidOutcome
{
    Voided,
    NotFound,
    AlreadyVoided,

    /// <summary>The sale belongs to a closed cash session (or to none): its totals are already closed and reported.</summary>
    SessionNotOpen,
}

public sealed record SaleVoidResult(SaleVoidOutcome Outcome, SaleVoidRecord? Void);

/// <summary>
/// The POS sales history and sale voids. A void is a row of its own in <c>sale_voids</c> (the sale effect, its lines and
/// its outbox envelope are never touched) written atomically with its <c>sale.voided</c> envelope. A voided sale stops
/// counting in its cash session: <see cref="SummarizeCashSession"/> leaves it out, so the expected cash drops by what the
/// sale had put in the drawer. Only a sale of the OPEN session can be voided: a closed session's totals were frozen and
/// sent to the cloud at close.
/// </summary>
public sealed partial class BranchSyncStore
{
    private void EnsureSaleHistoryStorageExists()
    {
        using var create = _connection.CreateCommand();
        create.CommandText = """
            CREATE TABLE IF NOT EXISTS sale_voids (
                sale_id TEXT PRIMARY KEY,
                cash_session_id TEXT NULL,
                voided_at_utc TEXT NOT NULL,
                voided_by_operator_id TEXT NOT NULL,
                auth_method TEXT NOT NULL,
                auth_operator_id TEXT NOT NULL,
                auth_pin_version TEXT NOT NULL,
                reason TEXT NOT NULL,
                operation_id TEXT NOT NULL
            );
            CREATE INDEX IF NOT EXISTS ix_sale_effects_occurred ON sale_effects (occurred_at_utc);
            """;
        create.ExecuteNonQuery();
    }

    /// <summary>
    /// The sales that occurred in [<paramref name="fromUtc"/>, <paramref name="toUtc"/>), newest first, at most
    /// <paramref name="limit"/>, each with its customer's name (when the replica knows it), line count and void.
    /// </summary>
    public IReadOnlyList<SaleHistoryEntry> ListSales(DateTimeOffset fromUtc, DateTimeOffset toUtc, int limit = 500)
    {
        using var command = _connection.CreateCommand();
        // occurred_at_utc is stored round-trip ("O") in UTC, so text order is time order.
        command.CommandText = """
            SELECT s.sale_id, s.occurred_at_utc, s.total_amount, s.sale_kind, s.customer_id, c.display_name,
                   s.tender_method, s.tender_amount_received, s.tender_change, s.cash_session_id,
                   s.branch_code, s.register_number, s.sale_sequence,
                   (SELECT COUNT(1) FROM sale_lines l WHERE l.sale_id = s.sale_id),
                   v.cash_session_id, v.voided_at_utc, v.voided_by_operator_id, v.auth_method, v.auth_operator_id,
                   v.auth_pin_version, v.reason, v.operation_id, v.sale_id
            FROM sale_effects s
            LEFT JOIN customers_replica c ON c.customer_id = s.customer_id
            LEFT JOIN sale_voids v ON v.sale_id = s.sale_id
            WHERE s.occurred_at_utc >= $from AND s.occurred_at_utc < $to
            ORDER BY s.occurred_at_utc DESC, s.sale_sequence DESC
            LIMIT $limit;
            """;
        command.Parameters.AddWithValue("$from", fromUtc.ToUniversalTime().ToString("O"));
        command.Parameters.AddWithValue("$to", toUtc.ToUniversalTime().ToString("O"));
        command.Parameters.AddWithValue("$limit", limit);

        var results = new List<SaleHistoryEntry>();
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            var saleId = Guid.Parse(reader.GetString(0));
            results.Add(new SaleHistoryEntry(
                saleId,
                FormatSaleNumber(reader, 10),
                DateTimeOffset.Parse(reader.GetString(1), CultureInfo.InvariantCulture),
                decimal.Parse(reader.GetString(2), CultureInfo.InvariantCulture),
                reader.GetString(3),
                reader.IsDBNull(4) ? null : Guid.Parse(reader.GetString(4)),
                reader.IsDBNull(5) ? null : reader.GetString(5),
                reader.IsDBNull(6) ? null : new SaleTender(reader.GetString(6), ReadDecimalOrNull(reader, 7), ReadDecimalOrNull(reader, 8)),
                reader.IsDBNull(9) ? null : Guid.Parse(reader.GetString(9)),
                reader.GetInt32(13),
                reader.IsDBNull(22) ? null : ReadSaleVoid(saleId, reader, 14)));
        }

        return results;
    }

    /// <summary>The void of a sale, or null when it was not voided.</summary>
    public SaleVoidRecord? GetSaleVoid(Guid saleId) => ReadSaleVoid(saleId, transaction: null);

    /// <summary>
    /// Voids a sale of the open cash session: records the void built by <paramref name="buildVoid"/> and queues the
    /// envelope built by <paramref name="buildEnvelope"/> in one transaction. Nothing is written when the sale is unknown,
    /// already voided, or not in the open session.
    /// </summary>
    public SaleVoidResult VoidSale(
        Guid saleId,
        Func<SaleEffect, SaleVoidRecord> buildVoid,
        Func<SaleEffect, SaleVoidRecord, SyncEnvelope> buildEnvelope)
    {
        lock (_writeGate)
        {
            using var transaction = _connection.BeginTransaction();

            using (var find = _connection.CreateCommand())
            {
                find.Transaction = transaction;
                find.CommandText = "SELECT branch_id FROM sale_effects WHERE sale_id = $saleId;";
                find.Parameters.AddWithValue("$saleId", saleId.ToString());
                if (find.ExecuteScalar() is not string branchId
                    || ReadSaleEffect(saleId, Guid.Parse(branchId), transaction) is not { } sale)
                {
                    transaction.Rollback();
                    return new SaleVoidResult(SaleVoidOutcome.NotFound, null);
                }

                if (ReadSaleVoid(saleId, transaction) is { } existing)
                {
                    transaction.Rollback();
                    return new SaleVoidResult(SaleVoidOutcome.AlreadyVoided, existing);
                }

                if (!IsCashSessionOpen(sale.CashSessionId, transaction))
                {
                    transaction.Rollback();
                    return new SaleVoidResult(SaleVoidOutcome.SessionNotOpen, null);
                }

                var record = buildVoid(sale);
                using (var insert = _connection.CreateCommand())
                {
                    insert.Transaction = transaction;
                    insert.CommandText = """
                        INSERT INTO sale_voids
                            (sale_id, cash_session_id, voided_at_utc, voided_by_operator_id, auth_method, auth_operator_id,
                             auth_pin_version, reason, operation_id)
                        VALUES ($saleId, $session, $at, $operator, $method, $authOperator, $pinVersion, $reason, $operation);
                        """;
                    insert.Parameters.AddWithValue("$saleId", saleId.ToString());
                    insert.Parameters.AddWithValue("$session", (object?)record.CashSessionId?.ToString() ?? DBNull.Value);
                    insert.Parameters.AddWithValue("$at", record.VoidedAtUtc.ToUniversalTime().ToString("O"));
                    insert.Parameters.AddWithValue("$operator", record.VoidedByOperatorId.ToString());
                    insert.Parameters.AddWithValue("$method", record.Authorization.Method);
                    insert.Parameters.AddWithValue("$authOperator", record.Authorization.OperatorId.ToString());
                    insert.Parameters.AddWithValue("$pinVersion", record.Authorization.PinVersion.ToString(CultureInfo.InvariantCulture));
                    insert.Parameters.AddWithValue("$reason", record.Reason);
                    insert.Parameters.AddWithValue("$operation", record.OperationId.ToString());
                    insert.ExecuteNonQuery();
                }

                InsertSyncOutboxRow(buildEnvelope(sale, record), transaction);
                transaction.Commit();
                return new SaleVoidResult(SaleVoidOutcome.Voided, record);
            }
        }
    }

    private SaleVoidRecord? ReadSaleVoid(Guid saleId, SqliteTransaction? transaction)
    {
        using var command = _connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT cash_session_id, voided_at_utc, voided_by_operator_id, auth_method, auth_operator_id, auth_pin_version,
                   reason, operation_id
            FROM sale_voids WHERE sale_id = $saleId;
            """;
        command.Parameters.AddWithValue("$saleId", saleId.ToString());
        using var reader = command.ExecuteReader();
        return reader.Read() ? ReadSaleVoid(saleId, reader, 0) : null;
    }

    private static SaleVoidRecord ReadSaleVoid(Guid saleId, SqliteDataReader reader, int first) => new(
        saleId,
        reader.IsDBNull(first) ? null : Guid.Parse(reader.GetString(first)),
        DateTimeOffset.Parse(reader.GetString(first + 1), CultureInfo.InvariantCulture),
        Guid.Parse(reader.GetString(first + 2)),
        new DiscountAuthorization(
            reader.GetString(first + 3),
            Guid.Parse(reader.GetString(first + 4)),
            long.Parse(reader.GetString(first + 5), CultureInfo.InvariantCulture)),
        reader.GetString(first + 6),
        Guid.Parse(reader.GetString(first + 7)));

    /// <summary>"V01-C2-125" from the three number columns starting at <paramref name="first"/>; null for an unnumbered sale.</summary>
    private static string? FormatSaleNumber(SqliteDataReader reader, int first)
    {
        if (reader.IsDBNull(first) || reader.IsDBNull(first + 1) || reader.IsDBNull(first + 2))
        {
            return null;
        }

        try
        {
            return new SaleNumber(new BranchCode(reader.GetInt32(first)), new RegisterNumber(reader.GetInt32(first + 1)), reader.GetInt32(first + 2))
                .Format();
        }
        catch (ArgumentOutOfRangeException)
        {
            return null;
        }
    }
}
