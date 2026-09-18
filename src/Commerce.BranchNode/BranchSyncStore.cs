using Commerce.Domain.Sync;
using Microsoft.Data.Sqlite;

namespace Commerce.BranchNode;

public sealed record BranchOutboxCommitResult(bool WasNewlyCommitted, SaleEffect Effect);

/// <summary>
/// One row of the minimum-viable cloud->local customer replica (Unit 6;
/// commerce-customer-identity design.md "BranchNode cloud->local customer
/// replication"). A projection, not the full aggregate — `Notes`/
/// `DiscountPercentage`/`PaymentTerms` never leave the server.
/// `OrganizationId` is stamped by the caller (the terminal's own paired
/// organization), not carried on the wire DTO.
/// </summary>
public sealed record CustomerReplica(
    Guid CustomerId,
    Guid OrganizationId,
    string DisplayName,
    string CustomerKind,
    string? TaxId,
    string? Phone,
    string? Locality,
    DateTimeOffset UpdatedAtUtc);

/// <summary>
/// SQLite-owned branch state (ADR-002: only the branch node opens the file).
/// Serialized writer, WAL, `synchronous=FULL` per design.md. Sale effect and
/// outbox row are committed atomically, or neither is committed.
/// </summary>
public sealed class BranchSyncStore : IDisposable
{
    private readonly SqliteConnection _connection;
    private readonly object _writeGate = new();

    public BranchSyncStore(string connectionString)
    {
        _connection = new SqliteConnection(connectionString);
        _connection.Open();

        using (var pragma = _connection.CreateCommand())
        {
            pragma.CommandText = "PRAGMA journal_mode=WAL; PRAGMA synchronous=FULL;";
            pragma.ExecuteNonQuery();
        }

        using var create = _connection.CreateCommand();
        create.CommandText = """
            CREATE TABLE IF NOT EXISTS sale_effects (
                sale_id TEXT PRIMARY KEY,
                branch_id TEXT NOT NULL,
                total_amount TEXT NOT NULL,
                occurred_at_utc TEXT NOT NULL
            );
            CREATE TABLE IF NOT EXISTS outbox (
                operation_id TEXT PRIMARY KEY,
                branch_id TEXT NOT NULL,
                organization_id TEXT NOT NULL,
                aggregate_id TEXT NOT NULL,
                aggregate_version INTEGER NOT NULL,
                actor_id TEXT NOT NULL,
                correlation_id TEXT NOT NULL,
                occurred_at_utc TEXT NOT NULL,
                payload_kind TEXT NOT NULL,
                payload TEXT NOT NULL,
                sale_id TEXT NOT NULL,
                total_amount TEXT NOT NULL,
                status TEXT NOT NULL,
                acknowledged_at_utc TEXT NULL
            );
            CREATE TABLE IF NOT EXISTS inbox (
                operation_id TEXT PRIMARY KEY,
                applied_at_utc TEXT NOT NULL
            );
            CREATE TABLE IF NOT EXISTS customers_replica (
                customer_id TEXT PRIMARY KEY,
                organization_id TEXT NOT NULL,
                display_name TEXT NOT NULL,
                customer_kind TEXT NOT NULL,
                tax_id TEXT NULL,
                phone TEXT NULL,
                locality TEXT NULL,
                updated_at_utc TEXT NOT NULL
            );
            CREATE TABLE IF NOT EXISTS sync_cursors (
                channel TEXT PRIMARY KEY,
                last_synced_utc TEXT NOT NULL
            );
            """;
        create.ExecuteNonQuery();
    }

    public BranchOutboxCommitResult CommitSaleAtomically(SyncEnvelope envelope, SaleEffect effect)
    {
        lock (_writeGate)
        {
            using var transaction = _connection.BeginTransaction();

            var existing = ReadExistingOutboxSale(envelope.OperationId, transaction);
            if (existing is not null)
            {
                transaction.Commit();
                return new BranchOutboxCommitResult(WasNewlyCommitted: false, existing);
            }

            using (var insertSale = _connection.CreateCommand())
            {
                insertSale.Transaction = transaction;
                insertSale.CommandText = """
                    INSERT INTO sale_effects (sale_id, branch_id, total_amount, occurred_at_utc)
                    VALUES ($saleId, $branchId, $totalAmount, $occurredAt);
                    """;
                insertSale.Parameters.AddWithValue("$saleId", effect.SaleId.ToString());
                insertSale.Parameters.AddWithValue("$branchId", effect.BranchId.ToString());
                insertSale.Parameters.AddWithValue("$totalAmount", effect.TotalAmount.ToString());
                insertSale.Parameters.AddWithValue("$occurredAt", effect.OccurredAtUtc.ToString("O"));
                insertSale.ExecuteNonQuery();
            }

            InsertOutboxRow(envelope, effect, transaction);

            transaction.Commit();
            return new BranchOutboxCommitResult(WasNewlyCommitted: true, effect);
        }
    }

    public void SimulateInterruptedCommit(SyncEnvelope envelope, SaleEffect effect)
    {
        lock (_writeGate)
        {
            using var transaction = _connection.BeginTransaction();

            using (var insertSale = _connection.CreateCommand())
            {
                insertSale.Transaction = transaction;
                insertSale.CommandText = """
                    INSERT INTO sale_effects (sale_id, branch_id, total_amount, occurred_at_utc)
                    VALUES ($saleId, $branchId, $totalAmount, $occurredAt);
                    """;
                insertSale.Parameters.AddWithValue("$saleId", effect.SaleId.ToString());
                insertSale.Parameters.AddWithValue("$branchId", effect.BranchId.ToString());
                insertSale.Parameters.AddWithValue("$totalAmount", effect.TotalAmount.ToString());
                insertSale.Parameters.AddWithValue("$occurredAt", effect.OccurredAtUtc.ToString("O"));
                insertSale.ExecuteNonQuery();
            }

            InsertOutboxRow(envelope, effect, transaction);

            // Deliberately abandoned: no Commit(). Disposing an uncommitted
            // SqliteTransaction rolls it back, proving atomicity across the
            // "restart" that reopens the same file with a fresh connection.
        }
    }

    public bool Acknowledge(Guid operationId)
    {
        lock (_writeGate)
        {
            using var command = _connection.CreateCommand();
            command.CommandText = """
                UPDATE outbox SET status = 'Acknowledged', acknowledged_at_utc = $now
                WHERE operation_id = $operationId;
                """;
            command.Parameters.AddWithValue("$operationId", operationId.ToString());
            command.Parameters.AddWithValue("$now", DateTimeOffset.UtcNow.ToString("O"));
            var affected = command.ExecuteNonQuery();

            if (affected > 0)
            {
                return true;
            }

            // Idempotent: acknowledging an already-acknowledged (or unknown-
            // but-previously-seen) operation must not fail the retry.
            return RowExists(operationId);
        }
    }

    public IReadOnlyList<SyncEnvelope> GetPendingOutbox(Guid branchId)
    {
        using var command = _connection.CreateCommand();
        command.CommandText = """
            SELECT operation_id, branch_id, organization_id, aggregate_id, aggregate_version,
                   actor_id, correlation_id, occurred_at_utc, payload_kind, payload
            FROM outbox
            WHERE branch_id = $branchId AND status = 'Pending';
            """;
        command.Parameters.AddWithValue("$branchId", branchId.ToString());

        var results = new List<SyncEnvelope>();
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            results.Add(ReadEnvelope(reader));
        }

        return results;
    }

    public InboundApplyResult ApplyInbound(SyncEnvelope envelope)
    {
        lock (_writeGate)
        {
            using var transaction = _connection.BeginTransaction();

            using (var exists = _connection.CreateCommand())
            {
                exists.Transaction = transaction;
                exists.CommandText = "SELECT COUNT(1) FROM inbox WHERE operation_id = $operationId;";
                exists.Parameters.AddWithValue("$operationId", envelope.OperationId.ToString());
                var count = (long)exists.ExecuteScalar()!;
                if (count > 0)
                {
                    transaction.Commit();
                    return new InboundApplyResult(InboundApplyOutcome.DuplicateIgnored, envelope.OperationId);
                }
            }

            using (var insert = _connection.CreateCommand())
            {
                insert.Transaction = transaction;
                insert.CommandText = """
                    INSERT INTO inbox (operation_id, applied_at_utc) VALUES ($operationId, $now);
                    """;
                insert.Parameters.AddWithValue("$operationId", envelope.OperationId.ToString());
                insert.Parameters.AddWithValue("$now", DateTimeOffset.UtcNow.ToString("O"));
                insert.ExecuteNonQuery();
            }

            transaction.Commit();
            return new InboundApplyResult(InboundApplyOutcome.Applied, envelope.OperationId);
        }
    }

    public SyncStatusSnapshot GetStatus(Guid branchId, bool isOffline)
    {
        using var pendingCommand = _connection.CreateCommand();
        pendingCommand.CommandText = "SELECT COUNT(1) FROM outbox WHERE branch_id = $branchId AND status = 'Pending';";
        pendingCommand.Parameters.AddWithValue("$branchId", branchId.ToString());
        var pendingCount = (int)(long)pendingCommand.ExecuteScalar()!;

        using var lastAckCommand = _connection.CreateCommand();
        lastAckCommand.CommandText = """
            SELECT MAX(acknowledged_at_utc) FROM outbox
            WHERE branch_id = $branchId AND status = 'Acknowledged';
            """;
        lastAckCommand.Parameters.AddWithValue("$branchId", branchId.ToString());
        var lastAckRaw = lastAckCommand.ExecuteScalar();

        DateTimeOffset? lastAcknowledgedUtc = lastAckRaw is null or DBNull
            ? null
            : DateTimeOffset.Parse((string)lastAckRaw);

        return new SyncStatusSnapshot(branchId, lastAcknowledgedUtc, pendingCount, isOffline);
    }

    // --- Minimum-viable cloud->local customer replica (Unit 6) --------------

    public const string CustomersChannel = "customers";

    /// <summary>
    /// Upserts each row in its own transaction. Idempotent: re-applying the
    /// same rows never duplicates them, and a changed row replaces the
    /// existing one rather than adding a second.
    /// </summary>
    public void UpsertCustomers(IReadOnlyList<CustomerReplica> customers)
    {
        lock (_writeGate)
        {
            using var transaction = _connection.BeginTransaction();
            foreach (var customer in customers)
            {
                UpsertCustomerRow(customer, transaction);
            }
            transaction.Commit();
        }
    }

    public void RemoveCustomers(IReadOnlyList<Guid> customerIds)
    {
        lock (_writeGate)
        {
            using var transaction = _connection.BeginTransaction();
            foreach (var customerId in customerIds)
            {
                DeleteCustomerRow(customerId, transaction);
            }
            transaction.Commit();
        }
    }

    public IReadOnlyList<CustomerReplica> ListCustomers()
    {
        using var command = _connection.CreateCommand();
        command.CommandText = """
            SELECT customer_id, organization_id, display_name, customer_kind, tax_id, phone, locality, updated_at_utc
            FROM customers_replica;
            """;

        var results = new List<CustomerReplica>();
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            results.Add(ReadCustomerReplica(reader));
        }
        return results;
    }

    public DateTimeOffset? GetCustomersCursor()
    {
        using var command = _connection.CreateCommand();
        command.CommandText = "SELECT last_synced_utc FROM sync_cursors WHERE channel = $channel;";
        command.Parameters.AddWithValue("$channel", CustomersChannel);

        var raw = command.ExecuteScalar();
        return raw is null or DBNull ? null : DateTimeOffset.Parse((string)raw);
    }

    public void SetCustomersCursor(DateTimeOffset value)
    {
        lock (_writeGate)
        {
            using var transaction = _connection.BeginTransaction();
            UpsertCursor(CustomersChannel, value, transaction);
            transaction.Commit();
        }
    }

    /// <summary>
    /// The real pull entry point (design.md "(c) the pull runs inside
    /// MainWindow.SyncButton_Click"): upserts, disabled-id removals, and the
    /// cursor advance happen in ONE transaction, so a failure never leaves
    /// the replica ahead of the cursor or vice versa.
    /// </summary>
    public void ApplyCustomerSync(
        IReadOnlyList<CustomerReplica> customers, IReadOnlyList<Guid> disabledIds, DateTimeOffset serverTimeUtc)
    {
        lock (_writeGate)
        {
            using var transaction = _connection.BeginTransaction();
            foreach (var customer in customers)
            {
                UpsertCustomerRow(customer, transaction);
            }
            foreach (var customerId in disabledIds)
            {
                DeleteCustomerRow(customerId, transaction);
            }
            UpsertCursor(CustomersChannel, serverTimeUtc, transaction);
            transaction.Commit();
        }
    }

    /// <summary>
    /// Test-only atomicity proof, mirroring <see cref="SimulateInterruptedCommit"/>:
    /// performs the same writes as <see cref="ApplyCustomerSync"/> but never
    /// commits. Disposing an uncommitted <see cref="SqliteTransaction"/> rolls
    /// it back, proving that an interrupted pull leaves both the replica and
    /// the cursor byte-identical across a restart.
    /// </summary>
    public void SimulateInterruptedCustomerSync(
        IReadOnlyList<CustomerReplica> customers, IReadOnlyList<Guid> disabledIds, DateTimeOffset serverTimeUtc)
    {
        lock (_writeGate)
        {
            using var transaction = _connection.BeginTransaction();
            foreach (var customer in customers)
            {
                UpsertCustomerRow(customer, transaction);
            }
            foreach (var customerId in disabledIds)
            {
                DeleteCustomerRow(customerId, transaction);
            }
            UpsertCursor(CustomersChannel, serverTimeUtc, transaction);
            // Deliberately abandoned: no Commit().
        }
    }

    private void UpsertCustomerRow(CustomerReplica customer, SqliteTransaction transaction)
    {
        using var command = _connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            INSERT INTO customers_replica
                (customer_id, organization_id, display_name, customer_kind, tax_id, phone, locality, updated_at_utc)
            VALUES ($customerId, $organizationId, $displayName, $customerKind, $taxId, $phone, $locality, $updatedAt)
            ON CONFLICT(customer_id) DO UPDATE SET
                organization_id = excluded.organization_id,
                display_name    = excluded.display_name,
                customer_kind   = excluded.customer_kind,
                tax_id          = excluded.tax_id,
                phone           = excluded.phone,
                locality        = excluded.locality,
                updated_at_utc  = excluded.updated_at_utc;
            """;
        command.Parameters.AddWithValue("$customerId", customer.CustomerId.ToString());
        command.Parameters.AddWithValue("$organizationId", customer.OrganizationId.ToString());
        command.Parameters.AddWithValue("$displayName", customer.DisplayName);
        command.Parameters.AddWithValue("$customerKind", customer.CustomerKind);
        command.Parameters.AddWithValue("$taxId", (object?)customer.TaxId ?? DBNull.Value);
        command.Parameters.AddWithValue("$phone", (object?)customer.Phone ?? DBNull.Value);
        command.Parameters.AddWithValue("$locality", (object?)customer.Locality ?? DBNull.Value);
        command.Parameters.AddWithValue("$updatedAt", customer.UpdatedAtUtc.ToString("O"));
        command.ExecuteNonQuery();
    }

    private void DeleteCustomerRow(Guid customerId, SqliteTransaction transaction)
    {
        using var command = _connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "DELETE FROM customers_replica WHERE customer_id = $customerId;";
        command.Parameters.AddWithValue("$customerId", customerId.ToString());
        command.ExecuteNonQuery();
    }

    private void UpsertCursor(string channel, DateTimeOffset value, SqliteTransaction transaction)
    {
        using var command = _connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            INSERT INTO sync_cursors (channel, last_synced_utc) VALUES ($channel, $value)
            ON CONFLICT(channel) DO UPDATE SET last_synced_utc = excluded.last_synced_utc;
            """;
        command.Parameters.AddWithValue("$channel", channel);
        command.Parameters.AddWithValue("$value", value.ToString("O"));
        command.ExecuteNonQuery();
    }

    private static CustomerReplica ReadCustomerReplica(SqliteDataReader reader) => new(
        CustomerId: Guid.Parse(reader.GetString(0)),
        OrganizationId: Guid.Parse(reader.GetString(1)),
        DisplayName: reader.GetString(2),
        CustomerKind: reader.GetString(3),
        TaxId: reader.IsDBNull(4) ? null : reader.GetString(4),
        Phone: reader.IsDBNull(5) ? null : reader.GetString(5),
        Locality: reader.IsDBNull(6) ? null : reader.GetString(6),
        UpdatedAtUtc: DateTimeOffset.Parse(reader.GetString(7)));

    private SaleEffect? ReadExistingOutboxSale(Guid operationId, SqliteTransaction transaction)
    {
        using var command = _connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT sale_id, branch_id, total_amount, occurred_at_utc FROM outbox
            WHERE operation_id = $operationId;
            """;
        command.Parameters.AddWithValue("$operationId", operationId.ToString());

        using var reader = command.ExecuteReader();
        if (!reader.Read())
        {
            return null;
        }

        return new SaleEffect(
            Guid.Parse(reader.GetString(0)),
            Guid.Parse(reader.GetString(1)),
            decimal.Parse(reader.GetString(2)),
            DateTimeOffset.Parse(reader.GetString(3)));
    }

    private void InsertOutboxRow(SyncEnvelope envelope, SaleEffect effect, SqliteTransaction transaction)
    {
        using var insertOutbox = _connection.CreateCommand();
        insertOutbox.Transaction = transaction;
        insertOutbox.CommandText = """
            INSERT INTO outbox (
                operation_id, branch_id, organization_id, aggregate_id, aggregate_version,
                actor_id, correlation_id, occurred_at_utc, payload_kind, payload,
                sale_id, total_amount, status, acknowledged_at_utc)
            VALUES (
                $operationId, $branchId, $organizationId, $aggregateId, $aggregateVersion,
                $actorId, $correlationId, $occurredAt, $payloadKind, $payload,
                $saleId, $totalAmount, 'Pending', NULL);
            """;
        insertOutbox.Parameters.AddWithValue("$operationId", envelope.OperationId.ToString());
        insertOutbox.Parameters.AddWithValue("$branchId", envelope.BranchId.ToString());
        insertOutbox.Parameters.AddWithValue("$organizationId", envelope.OrganizationId.ToString());
        insertOutbox.Parameters.AddWithValue("$aggregateId", envelope.AggregateId.ToString());
        insertOutbox.Parameters.AddWithValue("$aggregateVersion", envelope.AggregateVersion);
        insertOutbox.Parameters.AddWithValue("$actorId", envelope.ActorId.ToString());
        insertOutbox.Parameters.AddWithValue("$correlationId", envelope.CorrelationId.ToString());
        insertOutbox.Parameters.AddWithValue("$occurredAt", envelope.OccurredAtUtc.ToString("O"));
        insertOutbox.Parameters.AddWithValue("$payloadKind", envelope.PayloadKind);
        insertOutbox.Parameters.AddWithValue("$payload", envelope.Payload);
        insertOutbox.Parameters.AddWithValue("$saleId", effect.SaleId.ToString());
        insertOutbox.Parameters.AddWithValue("$totalAmount", effect.TotalAmount.ToString());
        insertOutbox.ExecuteNonQuery();
    }

    private bool RowExists(Guid operationId)
    {
        using var command = _connection.CreateCommand();
        command.CommandText = "SELECT COUNT(1) FROM outbox WHERE operation_id = $operationId;";
        command.Parameters.AddWithValue("$operationId", operationId.ToString());
        return (long)command.ExecuteScalar()! > 0;
    }

    private static SyncEnvelope ReadEnvelope(SqliteDataReader reader) => new(
        OperationId: Guid.Parse(reader.GetString(0)),
        ContractVersion: 1,
        BranchId: Guid.Parse(reader.GetString(1)),
        OrganizationId: Guid.Parse(reader.GetString(2)),
        AggregateId: Guid.Parse(reader.GetString(3)),
        AggregateVersion: reader.GetInt64(4),
        ActorId: Guid.Parse(reader.GetString(5)),
        CorrelationId: Guid.Parse(reader.GetString(6)),
        OccurredAtUtc: DateTimeOffset.Parse(reader.GetString(7)),
        PayloadKind: reader.GetString(8),
        Payload: reader.GetString(9));

    public void Dispose()
    {
        _connection.Dispose();
    }
}
