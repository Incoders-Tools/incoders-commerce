using Commerce.Domain.Payments;
using Commerce.Domain.Sync;
using Commerce.Domain.Sync.Payloads;
using Microsoft.Data.Sqlite;

namespace Commerce.BranchNode;

public sealed record BranchOutboxCommitResult(bool WasNewlyCommitted, SaleEffect Effect);

/// <summary>
/// One row of the `payment_outbox` table (commerce-payments design.md
/// "Storage shape of the parallel effect path"): only the generic
/// <see cref="SyncEnvelope"/> columns — no payment-specific column at all.
/// </summary>
public sealed record PaymentOutboxRow(
    Guid OperationId,
    Guid BranchId,
    Guid OrganizationId,
    Guid AggregateId,
    long AggregateVersion,
    Guid ActorId,
    Guid CorrelationId,
    DateTimeOffset OccurredAtUtc,
    string PayloadKind,
    string Payload,
    string Status);

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
/// One row of the cloud->local catalog+price replica (commerce-pricing-engine
/// design.md "BranchNode replication: one channel, not two"). Combines the
/// catalog projection and the currently-effective price in one wire shape so
/// the two can never be replicated out of step with each other. `UnitPrice`/
/// `EffectiveFrom` are null when the presentation has no effective price yet
/// (design.md "no zero fallback").
/// </summary>
public sealed record CatalogPriceReplicaItem(
    Guid PresentationId,
    Guid OrganizationId,
    Guid ProductId,
    string ProductName,
    string PresentationName,
    string? IdentificationCode,
    string QuantityBehavior,
    Guid UnitId,
    decimal? UnitPrice,
    DateOnly? EffectiveFrom,
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
                occurred_at_utc TEXT NOT NULL,
                sale_kind TEXT NOT NULL DEFAULT 'Manual'
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
            CREATE TABLE IF NOT EXISTS catalog_replica (
                presentation_id TEXT PRIMARY KEY,
                organization_id TEXT NOT NULL,
                product_id TEXT NOT NULL,
                product_name TEXT NOT NULL,
                presentation_name TEXT NOT NULL,
                identification_code TEXT NULL,
                quantity_behavior TEXT NOT NULL,
                unit_id TEXT NOT NULL,
                updated_at_utc TEXT NOT NULL
            );
            CREATE UNIQUE INDEX IF NOT EXISTS catalog_replica_code_uk
                ON catalog_replica (organization_id, identification_code)
                WHERE identification_code IS NOT NULL;
            CREATE TABLE IF NOT EXISTS price_replica (
                presentation_id TEXT PRIMARY KEY,
                organization_id TEXT NOT NULL,
                unit_price TEXT NOT NULL,
                effective_from TEXT NOT NULL,
                updated_at_utc TEXT NOT NULL
            );
            CREATE TABLE IF NOT EXISTS sale_lines (
                sale_id TEXT NOT NULL,
                line_number INTEGER NOT NULL,
                presentation_id TEXT NOT NULL,
                identification_code TEXT NULL,
                product_name TEXT NOT NULL,
                presentation_name TEXT NOT NULL,
                quantity TEXT NOT NULL,
                unit_price TEXT NOT NULL,
                line_total TEXT NOT NULL,
                PRIMARY KEY (sale_id, line_number)
            );
            """;
        create.ExecuteNonQuery();

        // commerce-payments design.md "Storage shape of the parallel effect
        // path": appended to the SAME constructor DDL block, additively.
        // ZERO edits above this point to outbox/sale_effects/sale_lines/inbox.
        // payment_outbox carries ONLY generic SyncEnvelope columns — no
        // sale_id, no total_amount, no payment_id: this is the shape Phase F
        // consolidates `outbox` into.
        using var createPayments = _connection.CreateCommand();
        createPayments.CommandText = """
            CREATE TABLE IF NOT EXISTS payment_effects (
                entry_id TEXT PRIMARY KEY, branch_id TEXT NOT NULL, subject_kind TEXT NOT NULL,
                subject_id TEXT NOT NULL, method TEXT NOT NULL, amount TEXT NOT NULL,
                entry_kind TEXT NOT NULL, reverses_entry_id TEXT NULL, occurred_at_utc TEXT NOT NULL
            );
            CREATE TABLE IF NOT EXISTS payment_outbox (
                operation_id TEXT PRIMARY KEY, branch_id TEXT NOT NULL, organization_id TEXT NOT NULL,
                aggregate_id TEXT NOT NULL, aggregate_version INTEGER NOT NULL, actor_id TEXT NOT NULL,
                correlation_id TEXT NOT NULL, occurred_at_utc TEXT NOT NULL, payload_kind TEXT NOT NULL,
                payload TEXT NOT NULL, status TEXT NOT NULL, acknowledged_at_utc TEXT NULL
            );
            """;
        createPayments.ExecuteNonQuery();

        // commerce-sync-ownership design.md "Outbox generalization": appended
        // to the SAME constructor DDL block, additively. ZERO edits above
        // this point to outbox/sale_effects/sale_lines/inbox. sync_outbox
        // carries ONLY generic SyncEnvelope columns plus the three retry
        // columns — EVERY new write (including "sale") lands here; the
        // legacy `outbox` table is drained by GetPendingOutbox but never
        // written again. inbound_orders is the materialization target for
        // the "order" IInboundEffectHandler (Unit 3).
        using var createSyncOutbox = _connection.CreateCommand();
        createSyncOutbox.CommandText = """
            CREATE TABLE IF NOT EXISTS sync_outbox (
                operation_id TEXT PRIMARY KEY, branch_id TEXT NOT NULL,
                organization_id TEXT NOT NULL, aggregate_id TEXT NOT NULL,
                aggregate_version INTEGER NOT NULL, actor_id TEXT NOT NULL,
                correlation_id TEXT NOT NULL, occurred_at_utc TEXT NOT NULL,
                payload_kind TEXT NOT NULL,
                payload TEXT NOT NULL CHECK (json_valid(payload) AND payload <> '{}'),
                status TEXT NOT NULL, acknowledged_at_utc TEXT NULL,
                attempt_count INTEGER NOT NULL DEFAULT 0,
                last_attempt_at_utc TEXT NULL,
                last_error TEXT NULL
            );
            CREATE TABLE IF NOT EXISTS inbound_orders (
                order_id TEXT PRIMARY KEY, organization_id TEXT NOT NULL,
                destination_branch_id TEXT NOT NULL, payload TEXT NOT NULL,
                materialized_at_utc TEXT NOT NULL
            );
            """;
        createSyncOutbox.ExecuteNonQuery();

        EnsureSaleKindColumnExists();
    }

    /// <summary>
    /// Task 7.1: a `branch.db` file created by an EARLIER version of this
    /// application already has a `sale_effects` table with no `sale_kind`
    /// column (`CREATE TABLE IF NOT EXISTS` is a no-op against it). SQLite
    /// has no `ADD COLUMN IF NOT EXISTS`, so this checks `PRAGMA table_info`
    /// explicitly and adds the column only when missing. The column's own
    /// `DEFAULT 'Manual'` then makes every pre-existing row read as a manual
    /// sale — which is exactly what it was — with no data migration required.
    /// </summary>
    private void EnsureSaleKindColumnExists()
    {
        using var check = _connection.CreateCommand();
        check.CommandText = "PRAGMA table_info(sale_effects);";
        using var reader = check.ExecuteReader();
        while (reader.Read())
        {
            if (string.Equals(reader.GetString(1), "sale_kind", StringComparison.Ordinal))
            {
                return;
            }
        }
        reader.Close();

        using var alter = _connection.CreateCommand();
        alter.CommandText = "ALTER TABLE sale_effects ADD COLUMN sale_kind TEXT NOT NULL DEFAULT 'Manual';";
        alter.ExecuteNonQuery();
    }

    public BranchOutboxCommitResult CommitSaleAtomically(SyncEnvelope envelope, SaleEffect effect) =>
        CommitSaleAtomicallyCore(envelope, effect with { SaleKind = "Manual" }, lines: []);

    /// <summary>
    /// Task 7.3 (GREEN): the scan-composed sale counterpart to
    /// <see cref="CommitSaleAtomically"/> — same atomic commit path,
    /// `SaleKind = "Scanned"`, and its lines persisted to `sale_lines` in the
    /// SAME transaction as the sale effect and outbox row (design.md "POS
    /// scan-to-sell": `sale_effects(sale_kind='Scanned') + sale_lines [1 tx]`).
    /// </summary>
    public BranchOutboxCommitResult CommitScannedSaleAtomically(SyncEnvelope envelope, SaleEffect effect, IReadOnlyList<SaleLine> lines) =>
        CommitSaleAtomicallyCore(envelope, effect with { SaleKind = "Scanned" }, lines);

    private BranchOutboxCommitResult CommitSaleAtomicallyCore(SyncEnvelope envelope, SaleEffect effect, IReadOnlyList<SaleLine> lines)
    {
        lock (_writeGate)
        {
            using var transaction = _connection.BeginTransaction();

            var existing = ReadExistingSyncOutboxSale(envelope.OperationId, envelope.BranchId, transaction);
            if (existing is not null)
            {
                transaction.Commit();
                return new BranchOutboxCommitResult(WasNewlyCommitted: false, existing);
            }

            InsertSaleEffectRow(effect, transaction);
            foreach (var line in lines)
            {
                InsertSaleLineRow(line, transaction);
            }

            InsertSyncOutboxRow(envelope, transaction);

            transaction.Commit();
            return new BranchOutboxCommitResult(WasNewlyCommitted: true, effect);
        }
    }

    public void SimulateInterruptedCommit(SyncEnvelope envelope, SaleEffect effect)
    {
        lock (_writeGate)
        {
            using var transaction = _connection.BeginTransaction();

            InsertSaleEffectRow(effect with { SaleKind = "Manual" }, transaction);

            InsertSyncOutboxRow(envelope, transaction);

            // Deliberately abandoned: no Commit(). Disposing an uncommitted
            // SqliteTransaction rolls it back, proving atomicity across the
            // "restart" that reopens the same file with a fresh connection.
        }
    }

    private void InsertSaleEffectRow(SaleEffect effect, SqliteTransaction transaction)
    {
        using var insertSale = _connection.CreateCommand();
        insertSale.Transaction = transaction;
        insertSale.CommandText = """
            INSERT INTO sale_effects (sale_id, branch_id, total_amount, occurred_at_utc, sale_kind)
            VALUES ($saleId, $branchId, $totalAmount, $occurredAt, $saleKind);
            """;
        insertSale.Parameters.AddWithValue("$saleId", effect.SaleId.ToString());
        insertSale.Parameters.AddWithValue("$branchId", effect.BranchId.ToString());
        insertSale.Parameters.AddWithValue("$totalAmount", effect.TotalAmount.ToString());
        insertSale.Parameters.AddWithValue("$occurredAt", effect.OccurredAtUtc.ToString("O"));
        insertSale.Parameters.AddWithValue("$saleKind", effect.SaleKind);
        insertSale.ExecuteNonQuery();
    }

    private void InsertSaleLineRow(SaleLine line, SqliteTransaction transaction)
    {
        using var insertLine = _connection.CreateCommand();
        insertLine.Transaction = transaction;
        insertLine.CommandText = """
            INSERT INTO sale_lines
                (sale_id, line_number, presentation_id, identification_code, product_name,
                 presentation_name, quantity, unit_price, line_total)
            VALUES ($saleId, $lineNumber, $presentationId, $identificationCode, $productName,
                    $presentationName, $quantity, $unitPrice, $lineTotal);
            """;
        insertLine.Parameters.AddWithValue("$saleId", line.SaleId.ToString());
        insertLine.Parameters.AddWithValue("$lineNumber", line.LineNumber);
        insertLine.Parameters.AddWithValue("$presentationId", line.PresentationId.ToString());
        insertLine.Parameters.AddWithValue("$identificationCode", (object?)line.IdentificationCode ?? DBNull.Value);
        insertLine.Parameters.AddWithValue("$productName", line.ProductName);
        insertLine.Parameters.AddWithValue("$presentationName", line.PresentationName);
        insertLine.Parameters.AddWithValue("$quantity", line.Quantity.ToString(System.Globalization.CultureInfo.InvariantCulture));
        insertLine.Parameters.AddWithValue("$unitPrice", line.UnitPrice.ToString(System.Globalization.CultureInfo.InvariantCulture));
        insertLine.Parameters.AddWithValue("$lineTotal", line.LineTotal.ToString(System.Globalization.CultureInfo.InvariantCulture));
        insertLine.ExecuteNonQuery();
    }

    public IReadOnlyList<SaleLine> ListSaleLines(Guid saleId)
    {
        using var command = _connection.CreateCommand();
        command.CommandText = """
            SELECT sale_id, line_number, presentation_id, identification_code, product_name,
                   presentation_name, quantity, unit_price, line_total
            FROM sale_lines
            WHERE sale_id = $saleId
            ORDER BY line_number;
            """;
        command.Parameters.AddWithValue("$saleId", saleId.ToString());

        var results = new List<SaleLine>();
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            results.Add(new SaleLine(
                SaleId: Guid.Parse(reader.GetString(0)),
                LineNumber: reader.GetInt32(1),
                PresentationId: Guid.Parse(reader.GetString(2)),
                IdentificationCode: reader.IsDBNull(3) ? null : reader.GetString(3),
                ProductName: reader.GetString(4),
                PresentationName: reader.GetString(5),
                Quantity: decimal.Parse(reader.GetString(6), System.Globalization.CultureInfo.InvariantCulture),
                UnitPrice: decimal.Parse(reader.GetString(7), System.Globalization.CultureInfo.InvariantCulture),
                LineTotal: decimal.Parse(reader.GetString(8), System.Globalization.CultureInfo.InvariantCulture)));
        }
        return results;
    }

    /// <summary>
    /// Task 7.2: the query <see cref="Commerce.Pos.Windows.LocalEffectivePriceSource"/>
    /// runs over `price_replica` (which carries only the CURRENT price, never
    /// history — see the table's own doc comment). A row that is not yet
    /// effective on <paramref name="effectiveOn"/> (a future-dated publish
    /// already replicated) or that does not exist returns `null`, matching
    /// <see cref="Commerce.Application.Pricing.IEffectivePriceSource"/>'s
    /// contract — never a zero fallback.
    /// </summary>
    public decimal? GetEffectivePrice(Guid presentationId, DateOnly effectiveOn)
    {
        using var command = _connection.CreateCommand();
        command.CommandText = """
            SELECT unit_price, effective_from FROM price_replica WHERE presentation_id = $presentationId;
            """;
        command.Parameters.AddWithValue("$presentationId", presentationId.ToString());

        using var reader = command.ExecuteReader();
        if (!reader.Read())
        {
            return null;
        }

        var unitPrice = decimal.Parse(reader.GetString(0), System.Globalization.CultureInfo.InvariantCulture);
        var effectiveFrom = DateOnly.Parse(reader.GetString(1));
        return effectiveFrom <= effectiveOn ? unitPrice : null;
    }

    /// <summary>
    /// Task 7.4/7.6: presentation + price lookup by scanned code, org-scoped
    /// per the replica's own partial unique index. `null` means the code is
    /// unresolved locally (design.md "Unknown code / no price at the POS").
    /// </summary>
    public CatalogPriceReplicaItem? FindByIdentificationCode(Guid organizationId, string identificationCode)
    {
        using var command = _connection.CreateCommand();
        command.CommandText = """
            SELECT c.presentation_id, c.organization_id, c.product_id, c.product_name, c.presentation_name,
                   c.identification_code, c.quantity_behavior, c.unit_id, p.unit_price, p.effective_from, c.updated_at_utc
            FROM catalog_replica c
            LEFT JOIN price_replica p ON p.presentation_id = c.presentation_id
            WHERE c.organization_id = $organizationId AND c.identification_code = $code;
            """;
        command.Parameters.AddWithValue("$organizationId", organizationId.ToString());
        command.Parameters.AddWithValue("$code", identificationCode);

        using var reader = command.ExecuteReader();
        return reader.Read() ? ReadCatalogPriceReplicaItem(reader) : null;
    }

    /// <summary>
    /// Phase F (commerce-sync-ownership design.md "Outbox generalization"):
    /// acknowledges whichever table holds the operation — <c>sync_outbox</c>
    /// first (every new write, including "sale", lands there), falling back
    /// to the legacy <c>outbox</c> for rows written before this change. Still
    /// idempotent: acknowledging an already-acknowledged (or unknown-but-
    /// previously-seen) operation must not fail the retry.
    /// </summary>
    public bool Acknowledge(Guid operationId)
    {
        lock (_writeGate)
        {
            using (var command = _connection.CreateCommand())
            {
                command.CommandText = """
                    UPDATE sync_outbox SET status = 'Acknowledged', acknowledged_at_utc = $now
                    WHERE operation_id = $operationId;
                    """;
                command.Parameters.AddWithValue("$operationId", operationId.ToString());
                command.Parameters.AddWithValue("$now", DateTimeOffset.UtcNow.ToString("O"));
                if (command.ExecuteNonQuery() > 0)
                {
                    return true;
                }
            }

            using (var command = _connection.CreateCommand())
            {
                command.CommandText = """
                    UPDATE outbox SET status = 'Acknowledged', acknowledged_at_utc = $now
                    WHERE operation_id = $operationId;
                    """;
                command.Parameters.AddWithValue("$operationId", operationId.ToString());
                command.Parameters.AddWithValue("$now", DateTimeOffset.UtcNow.ToString("O"));
                if (command.ExecuteNonQuery() > 0)
                {
                    return true;
                }
            }

            // Idempotent: acknowledging an already-acknowledged (or unknown-
            // but-previously-seen) operation must not fail the retry.
            return RowExists(operationId);
        }
    }

    /// <summary>
    /// Phase F (commerce-sync-ownership design.md "Outbox generalization"):
    /// the UNION of <c>sync_outbox</c> (every new write) and any still-
    /// <c>Pending</c> LEGACY <c>outbox</c> row, with the legacy row's payload
    /// reconstituted AT READ TIME into a real <see cref="SalePayloadV1"/> from
    /// its <c>sale_id</c>/<c>total_amount</c> + <c>sale_effects.sale_kind</c>
    /// join — no migration statement, no rewrite of the legacy row.
    /// </summary>
    public IReadOnlyList<SyncEnvelope> GetPendingOutbox(Guid branchId)
    {
        var results = new List<SyncEnvelope>();

        using (var command = _connection.CreateCommand())
        {
            command.CommandText = """
                SELECT operation_id, branch_id, organization_id, aggregate_id, aggregate_version,
                       actor_id, correlation_id, occurred_at_utc, payload_kind, payload
                FROM sync_outbox
                WHERE branch_id = $branchId AND status = 'Pending';
                """;
            command.Parameters.AddWithValue("$branchId", branchId.ToString());

            using var reader = command.ExecuteReader();
            while (reader.Read())
            {
                results.Add(ReadEnvelope(reader));
            }
        }

        using (var command = _connection.CreateCommand())
        {
            command.CommandText = """
                SELECT o.operation_id, o.branch_id, o.organization_id, o.aggregate_id, o.aggregate_version,
                       o.actor_id, o.correlation_id, o.occurred_at_utc, o.sale_id, o.total_amount,
                       e.sale_kind, e.occurred_at_utc
                FROM outbox o
                LEFT JOIN sale_effects e ON e.sale_id = o.sale_id
                WHERE o.branch_id = $branchId AND o.status = 'Pending';
                """;
            command.Parameters.AddWithValue("$branchId", branchId.ToString());

            using var reader = command.ExecuteReader();
            while (reader.Read())
            {
                results.Add(ReadLegacyOutboxEnvelope(reader));
            }
        }

        return results;
    }

    /// <summary>
    /// Phase F (commerce-sync-ownership design.md "Materialization contract"):
    /// dedup check -> resolve handler -> <c>Apply</c> -> insert the `inbox`
    /// row -> ONE commit. Dedup and materialization therefore succeed or fail
    /// together by construction (Requirement: Inbound Materialization
    /// Contract). An unknown `payload_kind` rolls back the WHOLE transaction
    /// and returns <see cref="InboundApplyOutcome.UnknownKind"/> — no `inbox`
    /// row is written, so the sender retries after the receiver upgrades.
    /// </summary>
    public InboundApplyResult ApplyInbound(SyncEnvelope envelope) => ApplyInboundCore(envelope, commit: true);

    /// <summary>
    /// Task 3.4/3.5: mirrors <see cref="SimulateInterruptedCommit"/> — proves
    /// atomicity when a handler throws (or, here, when the caller simply
    /// never commits): disposing an uncommitted transaction rolls back BOTH
    /// the handler's writes and the `inbox` insert together.
    /// </summary>
    public void SimulateInterruptedInboundApply(SyncEnvelope envelope) => ApplyInboundCore(envelope, commit: false);

    private InboundApplyResult ApplyInboundCore(SyncEnvelope envelope, bool commit)
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

            if (!InboundEffectRegistry.Default.TryGetValue(envelope.PayloadKind, out var handler))
            {
                // Unknown kind: roll back the whole transaction — disposing
                // without a commit is the rollback — no `inbox` row is ever
                // written for a kind this receiver cannot materialize.
                return new InboundApplyResult(InboundApplyOutcome.UnknownKind, envelope.OperationId);
            }

            handler.Apply(envelope, transaction);

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

            if (!commit)
            {
                // Deliberately abandoned: no Commit(). Disposing an
                // uncommitted SqliteTransaction rolls it back, proving
                // atomicity across handler writes + the inbox insert.
                return new InboundApplyResult(InboundApplyOutcome.Applied, envelope.OperationId);
            }

            transaction.Commit();
            return new InboundApplyResult(InboundApplyOutcome.Applied, envelope.OperationId);
        }
    }

    /// <summary>
    /// Spans both <c>sync_outbox</c> and the legacy <c>outbox</c> (Phase F
    /// "Outbox generalization"), so freshness/pending-count visibility never
    /// regresses while the legacy table still holds pre-migration rows.
    /// </summary>
    public SyncStatusSnapshot GetStatus(Guid branchId, bool isOffline)
    {
        using var pendingCommand = _connection.CreateCommand();
        pendingCommand.CommandText = """
            SELECT
                (SELECT COUNT(1) FROM sync_outbox WHERE branch_id = $branchId AND status = 'Pending') +
                (SELECT COUNT(1) FROM outbox WHERE branch_id = $branchId AND status = 'Pending');
            """;
        pendingCommand.Parameters.AddWithValue("$branchId", branchId.ToString());
        var pendingCount = (int)(long)pendingCommand.ExecuteScalar()!;

        using var lastAckCommand = _connection.CreateCommand();
        lastAckCommand.CommandText = """
            SELECT MAX(acknowledged_at_utc) FROM (
                SELECT acknowledged_at_utc FROM sync_outbox WHERE branch_id = $branchId AND status = 'Acknowledged'
                UNION ALL
                SELECT acknowledged_at_utc FROM outbox WHERE branch_id = $branchId AND status = 'Acknowledged'
            );
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

    // --- Cloud->local catalog+price replica (commerce-pricing-engine Unit 6) ---
    // ONE channel, reusing the EXISTING sync_cursors table with a second
    // channel constant (design.md "BranchNode replication: one channel, not
    // two") — no second cursor table.

    public const string CatalogPricesChannel = "catalog-prices";

    /// <summary>
    /// The real pull entry point, byte-for-byte mirroring
    /// <see cref="ApplyCustomerSync"/>: upserts, removed-id deletions, and the
    /// cursor advance happen in ONE transaction, so a failure never leaves the
    /// replica ahead of the cursor or vice versa.
    /// </summary>
    public void ApplyCatalogPriceSync(
        IReadOnlyList<CatalogPriceReplicaItem> items, IReadOnlyList<Guid> removedPresentationIds, DateTimeOffset serverTimeUtc)
    {
        lock (_writeGate)
        {
            using var transaction = _connection.BeginTransaction();
            foreach (var item in items)
            {
                UpsertCatalogRow(item, transaction);
                UpsertOrRemovePriceRow(item, transaction);
            }
            foreach (var presentationId in removedPresentationIds)
            {
                DeleteCatalogRow(presentationId, transaction);
                DeletePriceRow(presentationId, transaction);
            }
            UpsertCursor(CatalogPricesChannel, serverTimeUtc, transaction);
            transaction.Commit();
        }
    }

    /// <summary>
    /// Test-only atomicity proof, mirroring
    /// <see cref="SimulateInterruptedCustomerSync"/>: performs the same writes
    /// as <see cref="ApplyCatalogPriceSync"/> but never commits. Disposing an
    /// uncommitted <see cref="SqliteTransaction"/> rolls it back, proving that
    /// an interrupted pull leaves both the replica and the cursor
    /// byte-identical across a restart.
    /// </summary>
    public void SimulateInterruptedCatalogPriceSync(
        IReadOnlyList<CatalogPriceReplicaItem> items, IReadOnlyList<Guid> removedPresentationIds, DateTimeOffset serverTimeUtc)
    {
        lock (_writeGate)
        {
            using var transaction = _connection.BeginTransaction();
            foreach (var item in items)
            {
                UpsertCatalogRow(item, transaction);
                UpsertOrRemovePriceRow(item, transaction);
            }
            foreach (var presentationId in removedPresentationIds)
            {
                DeleteCatalogRow(presentationId, transaction);
                DeletePriceRow(presentationId, transaction);
            }
            UpsertCursor(CatalogPricesChannel, serverTimeUtc, transaction);
            // Deliberately abandoned: no Commit().
        }
    }

    public IReadOnlyList<CatalogPriceReplicaItem> ListCatalogPriceReplica()
    {
        using var command = _connection.CreateCommand();
        command.CommandText = """
            SELECT c.presentation_id, c.organization_id, c.product_id, c.product_name, c.presentation_name,
                   c.identification_code, c.quantity_behavior, c.unit_id, p.unit_price, p.effective_from, c.updated_at_utc
            FROM catalog_replica c
            LEFT JOIN price_replica p ON p.presentation_id = c.presentation_id;
            """;

        var results = new List<CatalogPriceReplicaItem>();
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            results.Add(ReadCatalogPriceReplicaItem(reader));
        }
        return results;
    }

    public DateTimeOffset? GetCatalogPricesCursor()
    {
        using var command = _connection.CreateCommand();
        command.CommandText = "SELECT last_synced_utc FROM sync_cursors WHERE channel = $channel;";
        command.Parameters.AddWithValue("$channel", CatalogPricesChannel);

        var raw = command.ExecuteScalar();
        return raw is null or DBNull ? null : DateTimeOffset.Parse((string)raw);
    }

    private void UpsertCatalogRow(CatalogPriceReplicaItem item, SqliteTransaction transaction)
    {
        using var command = _connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            INSERT INTO catalog_replica
                (presentation_id, organization_id, product_id, product_name, presentation_name,
                 identification_code, quantity_behavior, unit_id, updated_at_utc)
            VALUES ($presentationId, $organizationId, $productId, $productName, $presentationName,
                    $identificationCode, $quantityBehavior, $unitId, $updatedAt)
            ON CONFLICT(presentation_id) DO UPDATE SET
                organization_id     = excluded.organization_id,
                product_id          = excluded.product_id,
                product_name        = excluded.product_name,
                presentation_name   = excluded.presentation_name,
                identification_code = excluded.identification_code,
                quantity_behavior   = excluded.quantity_behavior,
                unit_id             = excluded.unit_id,
                updated_at_utc      = excluded.updated_at_utc;
            """;
        command.Parameters.AddWithValue("$presentationId", item.PresentationId.ToString());
        command.Parameters.AddWithValue("$organizationId", item.OrganizationId.ToString());
        command.Parameters.AddWithValue("$productId", item.ProductId.ToString());
        command.Parameters.AddWithValue("$productName", item.ProductName);
        command.Parameters.AddWithValue("$presentationName", item.PresentationName);
        command.Parameters.AddWithValue("$identificationCode", (object?)item.IdentificationCode ?? DBNull.Value);
        command.Parameters.AddWithValue("$quantityBehavior", item.QuantityBehavior);
        command.Parameters.AddWithValue("$unitId", item.UnitId.ToString());
        command.Parameters.AddWithValue("$updatedAt", item.UpdatedAtUtc.ToString("O"));
        command.ExecuteNonQuery();
    }

    /// <summary>
    /// A presentation with no effective price (<see cref="CatalogPriceReplicaItem.UnitPrice"/>
    /// null) never gets a zero substituted (design.md "no zero fallback") — its
    /// `price_replica` row is removed instead of upserted with a fabricated value.
    /// </summary>
    private void UpsertOrRemovePriceRow(CatalogPriceReplicaItem item, SqliteTransaction transaction)
    {
        if (item.UnitPrice is null || item.EffectiveFrom is null)
        {
            DeletePriceRow(item.PresentationId, transaction);
            return;
        }

        using var command = _connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            INSERT INTO price_replica (presentation_id, organization_id, unit_price, effective_from, updated_at_utc)
            VALUES ($presentationId, $organizationId, $unitPrice, $effectiveFrom, $updatedAt)
            ON CONFLICT(presentation_id) DO UPDATE SET
                organization_id = excluded.organization_id,
                unit_price      = excluded.unit_price,
                effective_from  = excluded.effective_from,
                updated_at_utc  = excluded.updated_at_utc;
            """;
        command.Parameters.AddWithValue("$presentationId", item.PresentationId.ToString());
        command.Parameters.AddWithValue("$organizationId", item.OrganizationId.ToString());
        command.Parameters.AddWithValue("$unitPrice", item.UnitPrice.Value.ToString(System.Globalization.CultureInfo.InvariantCulture));
        command.Parameters.AddWithValue("$effectiveFrom", item.EffectiveFrom.Value.ToString("O"));
        command.Parameters.AddWithValue("$updatedAt", item.UpdatedAtUtc.ToString("O"));
        command.ExecuteNonQuery();
    }

    private void DeleteCatalogRow(Guid presentationId, SqliteTransaction transaction)
    {
        using var command = _connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "DELETE FROM catalog_replica WHERE presentation_id = $presentationId;";
        command.Parameters.AddWithValue("$presentationId", presentationId.ToString());
        command.ExecuteNonQuery();
    }

    private void DeletePriceRow(Guid presentationId, SqliteTransaction transaction)
    {
        using var command = _connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "DELETE FROM price_replica WHERE presentation_id = $presentationId;";
        command.Parameters.AddWithValue("$presentationId", presentationId.ToString());
        command.ExecuteNonQuery();
    }

    private static CatalogPriceReplicaItem ReadCatalogPriceReplicaItem(SqliteDataReader reader) => new(
        PresentationId: Guid.Parse(reader.GetString(0)),
        OrganizationId: Guid.Parse(reader.GetString(1)),
        ProductId: Guid.Parse(reader.GetString(2)),
        ProductName: reader.GetString(3),
        PresentationName: reader.GetString(4),
        IdentificationCode: reader.IsDBNull(5) ? null : reader.GetString(5),
        QuantityBehavior: reader.GetString(6),
        UnitId: Guid.Parse(reader.GetString(7)),
        UnitPrice: reader.IsDBNull(8) ? null : decimal.Parse(reader.GetString(8), System.Globalization.CultureInfo.InvariantCulture),
        EffectiveFrom: reader.IsDBNull(9) ? null : DateOnly.Parse(reader.GetString(9)),
        UpdatedAtUtc: DateTimeOffset.Parse(reader.GetString(10)));

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

    /// <summary>
    /// Dedup check against `sync_outbox` — every new sale write lands there
    /// (Phase F "Outbox generalization": "Every new write goes to
    /// sync_outbox, including 'sale'"). A replayed <c>OperationId</c> returns
    /// the already-committed <see cref="SaleEffect"/> reconstructed from
    /// `sale_effects` (still written unchanged), never a second effect.
    /// </summary>
    private SaleEffect? ReadExistingSyncOutboxSale(Guid operationId, Guid branchId, SqliteTransaction transaction)
    {
        using var exists = _connection.CreateCommand();
        exists.Transaction = transaction;
        exists.CommandText = "SELECT COUNT(1) FROM sync_outbox WHERE operation_id = $operationId AND payload_kind = 'sale';";
        exists.Parameters.AddWithValue("$operationId", operationId.ToString());
        if ((long)exists.ExecuteScalar()! == 0)
        {
            return null;
        }

        using var command = _connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT aggregate_id FROM sync_outbox WHERE operation_id = $operationId;
            """;
        command.Parameters.AddWithValue("$operationId", operationId.ToString());
        var saleId = Guid.Parse((string)command.ExecuteScalar()!);

        using var saleCommand = _connection.CreateCommand();
        saleCommand.Transaction = transaction;
        saleCommand.CommandText = """
            SELECT total_amount, occurred_at_utc, sale_kind FROM sale_effects WHERE sale_id = $saleId;
            """;
        saleCommand.Parameters.AddWithValue("$saleId", saleId.ToString());
        using var reader = saleCommand.ExecuteReader();
        if (!reader.Read())
        {
            return null;
        }

        return new SaleEffect(
            saleId,
            branchId,
            decimal.Parse(reader.GetString(0), System.Globalization.CultureInfo.InvariantCulture),
            DateTimeOffset.Parse(reader.GetString(1)),
            reader.GetString(2));
    }

    /// <summary>
    /// Task 2.2 (GREEN): the generic, payload-only writer — no
    /// <see cref="SaleEffect"/> parameter, no sale-specific column. Used both
    /// by every sale commit path (which builds its own <see cref="SyncEnvelope"/>
    /// carrying a real <c>SalePayloadV1</c>) and by <see cref="EnqueueOutbox"/>
    /// for any other payload kind.
    /// </summary>
    private void InsertSyncOutboxRow(SyncEnvelope envelope, SqliteTransaction transaction)
    {
        using var insert = _connection.CreateCommand();
        insert.Transaction = transaction;
        insert.CommandText = """
            INSERT INTO sync_outbox (
                operation_id, branch_id, organization_id, aggregate_id, aggregate_version,
                actor_id, correlation_id, occurred_at_utc, payload_kind, payload,
                status, acknowledged_at_utc, attempt_count, last_attempt_at_utc, last_error)
            VALUES (
                $operationId, $branchId, $organizationId, $aggregateId, $aggregateVersion,
                $actorId, $correlationId, $occurredAt, $payloadKind, $payload,
                'Pending', NULL, 0, NULL, NULL);
            """;
        insert.Parameters.AddWithValue("$operationId", envelope.OperationId.ToString());
        insert.Parameters.AddWithValue("$branchId", envelope.BranchId.ToString());
        insert.Parameters.AddWithValue("$organizationId", envelope.OrganizationId.ToString());
        insert.Parameters.AddWithValue("$aggregateId", envelope.AggregateId.ToString());
        insert.Parameters.AddWithValue("$aggregateVersion", envelope.AggregateVersion);
        insert.Parameters.AddWithValue("$actorId", envelope.ActorId.ToString());
        insert.Parameters.AddWithValue("$correlationId", envelope.CorrelationId.ToString());
        insert.Parameters.AddWithValue("$occurredAt", envelope.OccurredAtUtc.ToString("O"));
        insert.Parameters.AddWithValue("$payloadKind", envelope.PayloadKind);
        insert.Parameters.AddWithValue("$payload", envelope.Payload);
        insert.ExecuteNonQuery();
    }

    /// <summary>
    /// Task 2.1/2.2 (GREEN): the public payload-only enqueue entry point —
    /// accepts no <see cref="SaleEffect"/>, writes no sale-specific column
    /// (Requirement: Generic Outbox Payload Contract, scenario "Enqueue a
    /// non-sale payload kind").
    /// </summary>
    public void EnqueueOutbox(SyncEnvelope envelope)
    {
        lock (_writeGate)
        {
            using var transaction = _connection.BeginTransaction();
            InsertSyncOutboxRow(envelope, transaction);
            transaction.Commit();
        }
    }

    /// <summary>
    /// Task 2.8 (GREEN): durable failure recording, replacing the in-memory
    /// failure list (design.md "Retry: where and how"). Only `sync_outbox`
    /// carries the attempt columns — a legacy `outbox` row (untouched DDL)
    /// has nowhere to persist an attempt, so this is a no-op for a still-
    /// undrained legacy row; the row simply stays `Pending` and is retried
    /// on the next sweep either way. The row is NEVER moved to a terminal
    /// state (design.md: no dead letter).
    /// </summary>
    public void RecordAttemptFailure(Guid operationId, string error)
    {
        lock (_writeGate)
        {
            using var command = _connection.CreateCommand();
            command.CommandText = """
                UPDATE sync_outbox
                SET attempt_count = attempt_count + 1, last_attempt_at_utc = $now, last_error = $error
                WHERE operation_id = $operationId;
                """;
            command.Parameters.AddWithValue("$operationId", operationId.ToString());
            command.Parameters.AddWithValue("$now", DateTimeOffset.UtcNow.ToString("O"));
            command.Parameters.AddWithValue("$error", error);
            command.ExecuteNonQuery();
        }
    }

    private bool RowExists(Guid operationId)
    {
        using (var command = _connection.CreateCommand())
        {
            command.CommandText = "SELECT COUNT(1) FROM sync_outbox WHERE operation_id = $operationId;";
            command.Parameters.AddWithValue("$operationId", operationId.ToString());
            if ((long)command.ExecuteScalar()! > 0)
            {
                return true;
            }
        }

        using (var command = _connection.CreateCommand())
        {
            command.CommandText = "SELECT COUNT(1) FROM outbox WHERE operation_id = $operationId;";
            command.Parameters.AddWithValue("$operationId", operationId.ToString());
            return (long)command.ExecuteScalar()! > 0;
        }
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

    /// <summary>
    /// Task 2.5/2.6 (GREEN): reconstitutes a LEGACY `outbox` row's payload
    /// into a real <see cref="SalePayloadV1"/> AT READ TIME — the legacy row
    /// itself is never rewritten. Reader column order matches the
    /// <c>GetPendingOutbox</c> legacy SELECT above.
    /// </summary>
    private SyncEnvelope ReadLegacyOutboxEnvelope(SqliteDataReader reader)
    {
        var saleId = Guid.Parse(reader.GetString(8));
        var totalAmount = decimal.Parse(reader.GetString(9), System.Globalization.CultureInfo.InvariantCulture);
        var saleKind = reader.IsDBNull(10) ? "Manual" : reader.GetString(10);
        var occurredAt = reader.IsDBNull(11) ? DateTimeOffset.Parse(reader.GetString(7)) : DateTimeOffset.Parse(reader.GetString(11));
        var lines = ListSaleLines(saleId);

        var payload = new SalePayloadV1(saleId, totalAmount, saleKind, occurredAt, lines);
        var payloadJson = SyncPayloadCodec.Serialize(payload);

        return new SyncEnvelope(
            OperationId: Guid.Parse(reader.GetString(0)),
            ContractVersion: 1,
            BranchId: Guid.Parse(reader.GetString(1)),
            OrganizationId: Guid.Parse(reader.GetString(2)),
            AggregateId: Guid.Parse(reader.GetString(3)),
            AggregateVersion: reader.GetInt64(4),
            ActorId: Guid.Parse(reader.GetString(5)),
            CorrelationId: Guid.Parse(reader.GetString(6)),
            OccurredAtUtc: DateTimeOffset.Parse(reader.GetString(7)),
            PayloadKind: "sale",
            Payload: payloadJson);
    }

    // --- Payment parallel path (Unit 5; commerce-payments design.md "Data
    // Flow" — POS cash payment at a branch, ADR-002: never blocks) ---------

    /// <summary>
    /// ONE SQLite transaction: `payment_effects` + `payment_outbox`, exactly
    /// mirroring <see cref="CommitSaleAtomicallyCore"/>'s atomicity shape. No
    /// cloud call, no gateway call on this path — returns immediately
    /// (ADR-002: offline branch sales/payments never block).
    /// </summary>
    public void CommitPaymentAtomically(SyncEnvelope envelope, PaymentEffect effect)
    {
        lock (_writeGate)
        {
            using var transaction = _connection.BeginTransaction();

            InsertPaymentEffectRow(effect, transaction);
            InsertPaymentOutboxRow(envelope, transaction);

            transaction.Commit();
        }
    }

    /// <summary>
    /// Test-only atomicity proof, mirroring <see cref="SimulateInterruptedCommit"/>:
    /// performs the same writes as <see cref="CommitPaymentAtomically"/> but
    /// never commits. Disposing an uncommitted <see cref="SqliteTransaction"/>
    /// rolls it back, proving that an interrupted payment commit leaves
    /// BOTH tables byte-identical across a restart.
    /// </summary>
    public void SimulateInterruptedPaymentCommit(SyncEnvelope envelope, PaymentEffect effect)
    {
        lock (_writeGate)
        {
            using var transaction = _connection.BeginTransaction();

            InsertPaymentEffectRow(effect, transaction);
            InsertPaymentOutboxRow(envelope, transaction);

            // Deliberately abandoned: no Commit().
        }
    }

    public IReadOnlyList<PaymentOutboxRow> GetPendingPaymentOutbox(Guid branchId)
    {
        using var command = _connection.CreateCommand();
        command.CommandText = """
            SELECT operation_id, branch_id, organization_id, aggregate_id, aggregate_version,
                   actor_id, correlation_id, occurred_at_utc, payload_kind, payload, status
            FROM payment_outbox
            WHERE branch_id = $branchId AND status = 'Pending';
            """;
        command.Parameters.AddWithValue("$branchId", branchId.ToString());

        var results = new List<PaymentOutboxRow>();
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            results.Add(ReadPaymentOutboxRow(reader));
        }
        return results;
    }

    /// <summary>
    /// Idempotent, mirroring <see cref="Acknowledge"/>: acknowledging an
    /// already-acknowledged (or unknown-but-previously-seen) operation must
    /// not fail the retry.
    /// </summary>
    public bool AcknowledgePayment(Guid operationId)
    {
        lock (_writeGate)
        {
            using var command = _connection.CreateCommand();
            command.CommandText = """
                UPDATE payment_outbox SET status = 'Acknowledged', acknowledged_at_utc = $now
                WHERE operation_id = $operationId;
                """;
            command.Parameters.AddWithValue("$operationId", operationId.ToString());
            command.Parameters.AddWithValue("$now", DateTimeOffset.UtcNow.ToString("O"));
            var affected = command.ExecuteNonQuery();

            if (affected > 0)
            {
                return true;
            }

            using var exists = _connection.CreateCommand();
            exists.CommandText = "SELECT COUNT(1) FROM payment_outbox WHERE operation_id = $operationId;";
            exists.Parameters.AddWithValue("$operationId", operationId.ToString());
            return (long)exists.ExecuteScalar()! > 0;
        }
    }

    private void InsertPaymentEffectRow(PaymentEffect effect, SqliteTransaction transaction)
    {
        using var insert = _connection.CreateCommand();
        insert.Transaction = transaction;
        insert.CommandText = """
            INSERT INTO payment_effects
                (entry_id, branch_id, subject_kind, subject_id, method, amount, entry_kind, reverses_entry_id, occurred_at_utc)
            VALUES ($entryId, $branchId, $subjectKind, $subjectId, $method, $amount, $entryKind, $reversesEntryId, $occurredAt);
            """;
        insert.Parameters.AddWithValue("$entryId", effect.EntryId.ToString());
        insert.Parameters.AddWithValue("$branchId", effect.BranchId.ToString());
        insert.Parameters.AddWithValue("$subjectKind", effect.SubjectKind);
        insert.Parameters.AddWithValue("$subjectId", effect.SubjectId.ToString());
        insert.Parameters.AddWithValue("$method", effect.Method);
        insert.Parameters.AddWithValue("$amount", effect.Amount.ToString(System.Globalization.CultureInfo.InvariantCulture));
        insert.Parameters.AddWithValue("$entryKind", effect.EntryKind);
        insert.Parameters.AddWithValue("$reversesEntryId", (object?)effect.ReversesEntryId?.ToString() ?? DBNull.Value);
        insert.Parameters.AddWithValue("$occurredAt", effect.OccurredAtUtc.ToString("O"));
        insert.ExecuteNonQuery();
    }

    private void InsertPaymentOutboxRow(SyncEnvelope envelope, SqliteTransaction transaction)
    {
        using var insert = _connection.CreateCommand();
        insert.Transaction = transaction;
        insert.CommandText = """
            INSERT INTO payment_outbox (
                operation_id, branch_id, organization_id, aggregate_id, aggregate_version,
                actor_id, correlation_id, occurred_at_utc, payload_kind, payload, status, acknowledged_at_utc)
            VALUES (
                $operationId, $branchId, $organizationId, $aggregateId, $aggregateVersion,
                $actorId, $correlationId, $occurredAt, $payloadKind, $payload, 'Pending', NULL);
            """;
        insert.Parameters.AddWithValue("$operationId", envelope.OperationId.ToString());
        insert.Parameters.AddWithValue("$branchId", envelope.BranchId.ToString());
        insert.Parameters.AddWithValue("$organizationId", envelope.OrganizationId.ToString());
        insert.Parameters.AddWithValue("$aggregateId", envelope.AggregateId.ToString());
        insert.Parameters.AddWithValue("$aggregateVersion", envelope.AggregateVersion);
        insert.Parameters.AddWithValue("$actorId", envelope.ActorId.ToString());
        insert.Parameters.AddWithValue("$correlationId", envelope.CorrelationId.ToString());
        insert.Parameters.AddWithValue("$occurredAt", envelope.OccurredAtUtc.ToString("O"));
        insert.Parameters.AddWithValue("$payloadKind", envelope.PayloadKind);
        insert.Parameters.AddWithValue("$payload", envelope.Payload);
        insert.ExecuteNonQuery();
    }

    private static PaymentOutboxRow ReadPaymentOutboxRow(SqliteDataReader reader) => new(
        OperationId: Guid.Parse(reader.GetString(0)),
        BranchId: Guid.Parse(reader.GetString(1)),
        OrganizationId: Guid.Parse(reader.GetString(2)),
        AggregateId: Guid.Parse(reader.GetString(3)),
        AggregateVersion: reader.GetInt64(4),
        ActorId: Guid.Parse(reader.GetString(5)),
        CorrelationId: Guid.Parse(reader.GetString(6)),
        OccurredAtUtc: DateTimeOffset.Parse(reader.GetString(7)),
        PayloadKind: reader.GetString(8),
        Payload: reader.GetString(9),
        Status: reader.GetString(10));

    public void Dispose()
    {
        _connection.Dispose();
    }
}
