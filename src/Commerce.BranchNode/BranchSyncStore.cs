using Commerce.Domain.Payments;
using Commerce.Domain.Sales;
using Commerce.Domain.Sync;
using Commerce.Domain.Sync.Payloads;
using Commerce.Domain.Tenancy;
using Microsoft.Data.Sqlite;

namespace Commerce.BranchNode;

/// <summary>
/// <see cref="Refusal"/> is set when the commit was refused before anything was
/// written (pos-cash-session: no open cash session); <see cref="WasNewlyCommitted"/>
/// is then false and <see cref="Effect"/> is the effect that was attempted.
/// </summary>
public sealed record BranchOutboxCommitResult(
    bool WasNewlyCommitted, SaleEffect Effect, Commerce.Domain.CashSessions.SaleCommitRefusal? Refusal = null);

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
/// `PaymentTerms` never leave the server; `DiscountPercentage` arrives with the
/// `price-lists` snapshot (<see cref="CustomerDiscountReplica"/>).
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
    DateTimeOffset UpdatedAtUtc,
    Guid? CategoryId = null,
    string? CategoryName = null,
    string? CategoryIconKey = null);

/// <summary>
/// One organization category as known to this terminal (catalog-categories):
/// derived from the replicated catalog rows, so it only lists categories that
/// have at least one product locally.
/// </summary>
public sealed record CatalogCategory(Guid Id, string Name, string IconKey);

/// <summary>
/// A capped local catalog search result. <see cref="Truncated"/> is true when
/// more rows matched than <see cref="Items"/> holds.
/// </summary>
public sealed record CatalogSearchResult(IReadOnlyList<CatalogPriceReplicaItem> Items, bool Truncated);

/// <summary>
/// SQLite-owned branch state (ADR-002: only the branch node opens the file).
/// Serialized writer, WAL, `synchronous=FULL` per design.md. Sale effect and
/// outbox row are committed atomically, or neither is committed.
/// </summary>
public sealed partial class BranchSyncStore : IDisposable
{
    private readonly SqliteConnection _connection;
    private readonly object _writeGate = new();

    public BranchSyncStore(string connectionString)
    {
        _connection = new SqliteConnection(connectionString);
        _connection.Open();

        // Case/accent-folding helper for the local catalog search (SQLite's own
        // LIKE/lower() only fold ASCII).
        _connection.CreateFunction("fold_text", (string? value) => FoldText(value), isDeterministic: true);

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
                sale_kind TEXT NOT NULL DEFAULT 'Manual',
                customer_id TEXT NULL
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
                updated_at_utc TEXT NOT NULL,
                category_id TEXT NULL,
                category_name TEXT NULL,
                category_icon_key TEXT NULL
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
        EnsureSaleCustomerColumnExists();
        EnsureSaleNumberStorageExists();
        EnsureCatalogCategoryColumnsExist();
        EnsureDiscountStorageExists();
        EnsureStockReplicaExists();
        EnsurePriceListsReplicaExists();
        EnsureTenderStorageExists();
        EnsureCashSessionStorageExists();
        EnsureOrganizationSettingsReplicaExists();
        EnsureSaleHistoryStorageExists();
        EnsureCustomerPaymentStorageExists();
        EnsureCashMovementStorageExists();
        EnsureCategoryStorageExists();
    }

    /// <summary>
    /// A `branch.db` created before categories existed has a `catalog_replica`
    /// without the category columns. Adds each nullable column only when
    /// missing (SQLite has no `ADD COLUMN IF NOT EXISTS`), so reopening is
    /// idempotent; existing rows read as uncategorized until the next sync
    /// re-sends them with their category.
    /// </summary>
    private void EnsureCatalogCategoryColumnsExist()
    {
        var existing = new HashSet<string>(StringComparer.Ordinal);
        using (var check = _connection.CreateCommand())
        {
            check.CommandText = "PRAGMA table_info(catalog_replica);";
            using var reader = check.ExecuteReader();
            while (reader.Read())
            {
                existing.Add(reader.GetString(1));
            }
        }

        foreach (var column in new[] { "category_id", "category_name", "category_icon_key" })
        {
            if (existing.Contains(column))
            {
                continue;
            }

            using var alter = _connection.CreateCommand();
            alter.CommandText = $"ALTER TABLE catalog_replica ADD COLUMN {column} TEXT NULL;";
            alter.ExecuteNonQuery();
        }
    }

    /// <summary>
    /// Human sale numbers (`V01-C2-125`): the per-(branch, register) counter table and the
    /// three nullable `sale_effects` columns that carry a sale's number. A `branch.db` from
    /// before numbering gains them empty (old sales stay unnumbered, never renumbered);
    /// reopening is idempotent.
    /// </summary>
    private void EnsureSaleNumberStorageExists()
    {
        using (var create = _connection.CreateCommand())
        {
            create.CommandText = """
                CREATE TABLE IF NOT EXISTS terminal_counters (
                    branch_id TEXT NOT NULL,
                    register_number INTEGER NOT NULL,
                    last_sequence INTEGER NOT NULL,
                    PRIMARY KEY (branch_id, register_number)
                );
                """;
            create.ExecuteNonQuery();
        }

        var existing = new HashSet<string>(StringComparer.Ordinal);
        using (var check = _connection.CreateCommand())
        {
            check.CommandText = "PRAGMA table_info(sale_effects);";
            using var reader = check.ExecuteReader();
            while (reader.Read())
            {
                existing.Add(reader.GetString(1));
            }
        }

        foreach (var column in new[] { "branch_code", "register_number", "sale_sequence" }.Where(c => !existing.Contains(c)))
        {
            using var alter = _connection.CreateCommand();
            alter.CommandText = $"ALTER TABLE sale_effects ADD COLUMN {column} INTEGER NULL;";
            alter.ExecuteNonQuery();
        }

        // Covers the one-off MAX(sale_sequence) seed of NextSaleSequence.
        using var index = _connection.CreateCommand();
        index.CommandText = "CREATE INDEX IF NOT EXISTS ix_sale_effects_number ON sale_effects (branch_id, register_number, sale_sequence);";
        index.ExecuteNonQuery();
    }

    /// <summary>
    /// A `branch.db` created before the customer was recorded on sales has no
    /// `sale_effects.customer_id`. Adds it as a nullable column (existing rows
    /// stay walk-in/unknown) only when missing, so reopening is idempotent.
    /// </summary>
    private void EnsureSaleCustomerColumnExists()
    {
        using var check = _connection.CreateCommand();
        check.CommandText = "PRAGMA table_info(sale_effects);";
        using var reader = check.ExecuteReader();
        while (reader.Read())
        {
            if (string.Equals(reader.GetString(1), "customer_id", StringComparison.Ordinal))
            {
                return;
            }
        }
        reader.Close();

        using var alter = _connection.CreateCommand();
        alter.CommandText = "ALTER TABLE sale_effects ADD COLUMN customer_id TEXT NULL;";
        alter.ExecuteNonQuery();
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

    public BranchOutboxCommitResult CommitSaleAtomically(
        SyncEnvelope envelope, SaleEffect effect, bool requireOpenCashSession = false, SaleNumbering? numbering = null) =>
        CommitSaleAtomicallyCore(envelope, effect with { SaleKind = "Manual" }, lines: [], requireOpenCashSession, numbering);

    /// <summary>
    /// Task 7.3 (GREEN): the scan-composed sale counterpart to
    /// <see cref="CommitSaleAtomically"/> — same atomic commit path,
    /// `SaleKind = "Scanned"`, and its lines persisted to `sale_lines` in the
    /// SAME transaction as the sale effect and outbox row (design.md "POS
    /// scan-to-sell": `sale_effects(sale_kind='Scanned') + sale_lines [1 tx]`).
    /// </summary>
    public BranchOutboxCommitResult CommitScannedSaleAtomically(
        SyncEnvelope envelope, SaleEffect effect, IReadOnlyList<SaleLine> lines, bool requireOpenCashSession = false, SaleNumbering? numbering = null) =>
        CommitSaleAtomicallyCore(envelope, effect with { SaleKind = "Scanned" }, lines, requireOpenCashSession, numbering);

    /// <param name="numbering">The terminal's branch code and register number, or null while it does not know them
    /// (the sale then commits WITHOUT a number). When given, the next sequence of that (branch, register) is taken
    /// INSIDE this transaction, after the idempotency check and the cash-session check, so a replay or a refused
    /// sale never burns a number and the number commits or rolls back with the sale.</param>
    private BranchOutboxCommitResult CommitSaleAtomicallyCore(
        SyncEnvelope envelope, SaleEffect effect, IReadOnlyList<SaleLine> lines, bool requireOpenCashSession, SaleNumbering? numbering)
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

            // pos-cash-session: the sale must belong to a session that is STILL open
            // inside this same transaction, so a close can never interleave.
            if (requireOpenCashSession && !IsCashSessionOpen(effect.CashSessionId, transaction))
            {
                transaction.Rollback();
                return new BranchOutboxCommitResult(
                    WasNewlyCommitted: false, effect, Commerce.Domain.CashSessions.SaleCommitRefusal.NoOpenCashSession);
            }

            if (numbering is { } terminal)
            {
                var sequence = NextSaleSequence(effect.BranchId, terminal.Register, transaction);
                effect = effect with
                {
                    BranchCode = terminal.Branch.Value, RegisterNumber = terminal.Register.Value, SaleSequence = sequence,
                };
                envelope = envelope with { Payload = StampSaleNumber(envelope.Payload, effect) };
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

    /// <summary>
    /// Takes the next sequence of the (branch, register) counter, never below the highest sequence
    /// already stored for that pair: a lost row is seeded from it and a counter that fell behind
    /// is lifted to it, so the terminal can never repeat a number. Must run inside the sale transaction.
    /// </summary>
    private int NextSaleSequence(Guid branchId, RegisterNumber register, SqliteTransaction transaction)
    {
        // Hot path: the counter row exists, one UPDATE. The counter is reconciled with the highest
        // stored sequence (a restored or merged database can hold a higher one); that lookup is a
        // single seek on ix_sale_effects_number, not a scan.
        using (var bump = _connection.CreateCommand())
        {
            bump.Transaction = transaction;
            bump.CommandText = """
                UPDATE terminal_counters SET last_sequence = MAX(
                        last_sequence,
                        COALESCE((SELECT MAX(sale_sequence) FROM sale_effects
                                   WHERE branch_id = $branchId AND register_number = $register), 0)) + 1
                 WHERE branch_id = $branchId AND register_number = $register
                RETURNING last_sequence;
                """;
            bump.Parameters.AddWithValue("$branchId", branchId.ToString());
            bump.Parameters.AddWithValue("$register", register.Value);
            if (bump.ExecuteScalar() is { } bumped)
            {
                return Convert.ToInt32(bumped, System.Globalization.CultureInfo.InvariantCulture);
            }
        }

        // First sale of the pair (or a lost row): seed once from the highest stored sequence,
        // served by ix_sale_effects_number.
        using var seed = _connection.CreateCommand();
        seed.Transaction = transaction;
        seed.CommandText = """
            INSERT INTO terminal_counters (branch_id, register_number, last_sequence)
            VALUES ($branchId, $register,
                    COALESCE((SELECT MAX(sale_sequence) FROM sale_effects WHERE branch_id = $branchId AND register_number = $register), 0) + 1)
            RETURNING last_sequence;
            """;
        seed.Parameters.AddWithValue("$branchId", branchId.ToString());
        seed.Parameters.AddWithValue("$register", register.Value);
        return Convert.ToInt32(seed.ExecuteScalar(), System.Globalization.CultureInfo.InvariantCulture);
    }

    /// <summary>The queued payload carries the number too (additive optional fields of SalePayloadV1).</summary>
    private static string StampSaleNumber(string payloadJson, SaleEffect effect) =>
        SyncPayloadCodec.Serialize(SyncPayloadCodec.Deserialize<SalePayloadV1>(payloadJson) with
        {
            BranchCode = effect.BranchCode, RegisterNumber = effect.RegisterNumber, SaleSequence = effect.SaleSequence,
        });

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
            INSERT INTO sale_effects
                (sale_id, branch_id, total_amount, occurred_at_utc, sale_kind, customer_id,
                 sale_discount_percent, sale_discount_amount, discount_auth_method, discount_operator_id, discount_pin_version,
                 tender_method, tender_amount_received, tender_change, cash_session_id,
                 branch_code, register_number, sale_sequence)
            VALUES ($saleId, $branchId, $totalAmount, $occurredAt, $saleKind, $customerId,
                    $saleDiscountPercent, $saleDiscountAmount, $authMethod, $authOperatorId, $authPinVersion,
                    $tenderMethod, $tenderReceived, $tenderChange, $cashSessionId,
                    $branchCode, $registerNumber, $saleSequence);
            """;
        insertSale.Parameters.AddWithValue("$saleId", effect.SaleId.ToString());
        insertSale.Parameters.AddWithValue("$branchId", effect.BranchId.ToString());
        insertSale.Parameters.AddWithValue("$totalAmount", effect.TotalAmount.ToString());
        insertSale.Parameters.AddWithValue("$occurredAt", effect.OccurredAtUtc.ToString("O"));
        insertSale.Parameters.AddWithValue("$saleKind", effect.SaleKind);
        insertSale.Parameters.AddWithValue("$customerId", effect.CustomerId is { } customerId ? customerId.ToString() : DBNull.Value);
        insertSale.Parameters.AddWithValue("$saleDiscountPercent", DecimalOrNull(effect.SaleDiscountPercent));
        insertSale.Parameters.AddWithValue("$saleDiscountAmount", DecimalOrNull(effect.SaleDiscountAmount));
        insertSale.Parameters.AddWithValue("$authMethod", (object?)effect.DiscountAuthorization?.Method ?? DBNull.Value);
        insertSale.Parameters.AddWithValue("$authOperatorId", effect.DiscountAuthorization is { } auth ? auth.OperatorId.ToString() : DBNull.Value);
        insertSale.Parameters.AddWithValue("$authPinVersion", effect.DiscountAuthorization is { } pinAuth ? pinAuth.PinVersion : DBNull.Value);
        insertSale.Parameters.AddWithValue("$tenderMethod", (object?)effect.Tender?.Method ?? DBNull.Value);
        insertSale.Parameters.AddWithValue("$tenderReceived", DecimalOrNull(effect.Tender?.AmountReceived));
        insertSale.Parameters.AddWithValue("$tenderChange", DecimalOrNull(effect.Tender?.ChangeGiven));
        insertSale.Parameters.AddWithValue("$cashSessionId", effect.CashSessionId is { } sessionId ? sessionId.ToString() : DBNull.Value);
        insertSale.Parameters.AddWithValue("$branchCode", effect.BranchCode is { } branchCode ? branchCode : DBNull.Value);
        insertSale.Parameters.AddWithValue("$registerNumber", effect.RegisterNumber is { } registerNumber ? registerNumber : DBNull.Value);
        insertSale.Parameters.AddWithValue("$saleSequence", effect.SaleSequence is { } saleSequence ? saleSequence : DBNull.Value);
        insertSale.ExecuteNonQuery();
    }

    private void InsertSaleLineRow(SaleLine line, SqliteTransaction transaction)
    {
        using var insertLine = _connection.CreateCommand();
        insertLine.Transaction = transaction;
        insertLine.CommandText = """
            INSERT INTO sale_lines
                (sale_id, line_number, presentation_id, identification_code, product_name,
                 presentation_name, quantity, unit_price, line_total, line_discount_percent, line_discount_amount)
            VALUES ($saleId, $lineNumber, $presentationId, $identificationCode, $productName,
                    $presentationName, $quantity, $unitPrice, $lineTotal, $lineDiscountPercent, $lineDiscountAmount);
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
        insertLine.Parameters.AddWithValue("$lineDiscountPercent", DecimalOrNull(line.LineDiscountPercent));
        insertLine.Parameters.AddWithValue("$lineDiscountAmount", DecimalOrNull(line.LineDiscountAmount));
        insertLine.ExecuteNonQuery();
    }

    public IReadOnlyList<SaleLine> ListSaleLines(Guid saleId)
    {
        using var command = _connection.CreateCommand();
        command.CommandText = """
            SELECT sale_id, line_number, presentation_id, identification_code, product_name,
                   presentation_name, quantity, unit_price, line_total, line_discount_percent, line_discount_amount
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
                LineTotal: decimal.Parse(reader.GetString(8), System.Globalization.CultureInfo.InvariantCulture),
                LineDiscountPercent: ReadDecimalOrNull(reader, 9),
                LineDiscountAmount: ReadDecimalOrNull(reader, 10)));
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
                   c.identification_code, c.quantity_behavior, c.unit_id, p.unit_price, p.effective_from, c.updated_at_utc,
                   c.category_id, c.category_name, c.category_icon_key
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
                WHERE branch_id = $branchId AND status = 'Pending'
                ORDER BY occurred_at_utc, rowid;
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

        // The last time the cloud snapshot was applied locally (the pull side of a sync), which a sync with nothing to
        // send still moves.
        using var downloadCommand = _connection.CreateCommand();
        downloadCommand.CommandText = "SELECT last_synced_utc FROM sync_cursors WHERE channel = $channel;";
        downloadCommand.Parameters.AddWithValue("$channel", "price-lists-applied-local");
        DateTimeOffset? lastDownloadedUtc = downloadCommand.ExecuteScalar() is string downloaded
            && DateTimeOffset.TryParse(downloaded, System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.RoundtripKind, out var parsed)
                ? parsed
                : null;

        return new SyncStatusSnapshot(branchId, lastAcknowledgedUtc, pendingCount, isOffline, lastDownloadedUtc);
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

    public const int DefaultCatalogSearchLimit = 120;

    /// <summary>
    /// Local name search over the replica (desktop POS redesign T3). Every
    /// whitespace-separated token of <paramref name="query"/> must match
    /// (accent- and case-insensitively) inside the product + presentation name,
    /// or be a prefix of the identification code. An empty query lists
    /// everything. Organization scoped, ordered by name and capped at
    /// <paramref name="limit"/>; <see cref="CatalogSearchResult.Truncated"/>
    /// reports that more rows matched.
    /// </summary>
    public CatalogSearchResult SearchCatalog(
        Guid organizationId, string? query, int limit = DefaultCatalogSearchLimit, Guid? categoryId = null)
    {
        var tokens = (query ?? string.Empty)
            .Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries)
            .Select(FoldText)
            .ToList();

        var where = new System.Text.StringBuilder("c.organization_id = $organizationId");
        if (categoryId is not null)
        {
            where.Append(" AND c.category_id = $categoryId");
        }
        for (var i = 0; i < tokens.Count; i++)
        {
            where.Append($"""
                 AND (instr(fold_text(c.product_name || ' ' || c.presentation_name), $t{i}) > 0
                      OR instr(fold_text(coalesce(c.identification_code, '')), $t{i}) = 1)
                """);
        }

        using var command = _connection.CreateCommand();
        command.CommandText = $"""
            SELECT c.presentation_id, c.organization_id, c.product_id, c.product_name, c.presentation_name,
                   c.identification_code, c.quantity_behavior, c.unit_id, p.unit_price, p.effective_from, c.updated_at_utc,
                   c.category_id, c.category_name, c.category_icon_key
            FROM catalog_replica c
            LEFT JOIN price_replica p ON p.presentation_id = c.presentation_id
            WHERE {where}
            ORDER BY c.product_name COLLATE NOCASE, c.presentation_name COLLATE NOCASE
            LIMIT $limit;
            """;
        command.Parameters.AddWithValue("$organizationId", organizationId.ToString());
        if (categoryId is not null)
        {
            command.Parameters.AddWithValue("$categoryId", categoryId.Value.ToString());
        }
        for (var i = 0; i < tokens.Count; i++)
        {
            command.Parameters.AddWithValue($"$t{i}", tokens[i]);
        }
        command.Parameters.AddWithValue("$limit", limit + 1);

        var items = new List<CatalogPriceReplicaItem>();
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            items.Add(ReadCatalogPriceReplicaItem(reader));
        }

        return items.Count > limit
            ? new CatalogSearchResult(items.Take(limit).ToList(), Truncated: true)
            : new CatalogSearchResult(items, Truncated: false);
    }

    /// <summary>
    /// The distinct categories present in the local catalog for the
    /// organization, ordered by name — what the POS category rail lists after
    /// "Todos". Products without a category (an older row not re-synced yet)
    /// contribute nothing here and remain reachable under "Todos".
    /// </summary>
    public IReadOnlyList<CatalogCategory> ListCatalogCategories(Guid organizationId)
    {
        using var command = _connection.CreateCommand();
        command.CommandText = """
            SELECT category_id, MAX(category_name), MAX(coalesce(category_icon_key, 'generic'))
            FROM catalog_replica
            WHERE organization_id = $organizationId AND category_id IS NOT NULL AND category_name IS NOT NULL
            GROUP BY category_id
            ORDER BY MAX(category_name) COLLATE NOCASE, category_id;
            """;
        command.Parameters.AddWithValue("$organizationId", organizationId.ToString());

        var categories = new List<CatalogCategory>();
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            categories.Add(new CatalogCategory(Guid.Parse(reader.GetString(0)), reader.GetString(1), reader.GetString(2)));
        }
        return categories;
    }

    /// <summary>Lower-cases and strips diacritics so "Café" and "CAFE" compare equal.</summary>
    public static string FoldText(string? value)
    {
        if (string.IsNullOrEmpty(value))
        {
            return string.Empty;
        }

        var decomposed = value.Normalize(System.Text.NormalizationForm.FormD);
        var builder = new System.Text.StringBuilder(decomposed.Length);
        foreach (var ch in decomposed)
        {
            if (System.Globalization.CharUnicodeInfo.GetUnicodeCategory(ch) != System.Globalization.UnicodeCategory.NonSpacingMark)
            {
                builder.Append(char.ToLowerInvariant(ch));
            }
        }
        return builder.ToString();
    }

    public IReadOnlyList<CatalogPriceReplicaItem> ListCatalogPriceReplica()
    {
        using var command = _connection.CreateCommand();
        command.CommandText = """
            SELECT c.presentation_id, c.organization_id, c.product_id, c.product_name, c.presentation_name,
                   c.identification_code, c.quantity_behavior, c.unit_id, p.unit_price, p.effective_from, c.updated_at_utc,
                   c.category_id, c.category_name, c.category_icon_key
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
                 identification_code, quantity_behavior, unit_id, updated_at_utc,
                 category_id, category_name, category_icon_key)
            VALUES ($presentationId, $organizationId, $productId, $productName, $presentationName,
                    $identificationCode, $quantityBehavior, $unitId, $updatedAt,
                    $categoryId, $categoryName, $categoryIconKey)
            ON CONFLICT(presentation_id) DO UPDATE SET
                organization_id     = excluded.organization_id,
                product_id          = excluded.product_id,
                product_name        = excluded.product_name,
                presentation_name   = excluded.presentation_name,
                identification_code = excluded.identification_code,
                quantity_behavior   = excluded.quantity_behavior,
                unit_id             = excluded.unit_id,
                updated_at_utc      = excluded.updated_at_utc,
                category_id         = excluded.category_id,
                category_name       = excluded.category_name,
                category_icon_key   = excluded.category_icon_key;
            """;
        command.Parameters.AddWithValue("$categoryId", (object?)item.CategoryId?.ToString() ?? DBNull.Value);
        command.Parameters.AddWithValue("$categoryName", (object?)item.CategoryName ?? DBNull.Value);
        command.Parameters.AddWithValue("$categoryIconKey", (object?)item.CategoryIconKey ?? DBNull.Value);
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
        UpdatedAtUtc: DateTimeOffset.Parse(reader.GetString(10)),
        CategoryId: reader.IsDBNull(11) ? null : Guid.Parse(reader.GetString(11)),
        CategoryName: reader.IsDBNull(12) ? null : reader.GetString(12),
        CategoryIconKey: reader.IsDBNull(13) ? null : reader.GetString(13));

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

        return ReadSaleEffect(saleId, branchId, transaction);
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
