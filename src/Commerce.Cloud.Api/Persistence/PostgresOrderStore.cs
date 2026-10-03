using System.Globalization;
using Commerce.BranchNode;
using Commerce.Cloud.Api.Ordering;
using Commerce.Cloud.Api.Tenancy;
using Commerce.Domain.Catalog;
using Commerce.Domain.Ordering;
using Commerce.Domain.Tenancy;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;

namespace Commerce.Cloud.Api.Persistence;

/// <summary>
/// Npgsql-backed <see cref="IOrderStore"/> (persist-web-orders): web orders survive an API restart and
/// carry their human number <c>P{branch}-W-{sequence}</c>. Follows the other stores: an injected
/// <see cref="NpgsqlDataSource"/>, one transaction per call, tenant scope applied as the first statement.
///
/// Submission, in one transaction: read the order (idempotency is per organization) -> resolve the
/// branch code (a missing branch is a typed denial) -> take the per-branch advisory lock (seed 3) ->
/// re-read (a concurrent submit of the same id finished first) -> spend the guest verification ->
/// <c>MAX(sequence)+1</c> -> insert order and lines -> COMMIT. Any exception rolls the whole transaction
/// back: no order, no consumed verification and no advanced counter. Delivery to the destination branch
/// happens only AFTER that commit (a rollback must never leave the branch holding a phantom order); the
/// order is stored pending/offline first and its delivery state is persisted by a follow-up statement.
/// A delivery or follow-up failure leaves the stored order honestly pending: the order is never lost
/// and <see cref="RetryDeliveryAsync"/> repeats the attempt idempotently.
/// The lock is transaction-scoped (pgbouncer-safe); orders are never deleted, so a committed number is
/// never reissued, and <c>UNIQUE (organization_id, destination_branch_id, sequence)</c> is the backstop.
/// </summary>
public sealed class PostgresOrderStore : IOrderStore
{
    /// <summary>Advisory-lock seed of the per-branch order counter (1 = per-branch registers, 2 = per-installation, see 0022).</summary>
    private const int OrderCounterLockSeed = 3;

    private const string OrderColumns =
        """
        order_id, origin, customer_id, guest_document_id, guest_channel, guest_contact_address,
        guest_display_name, guest_delivery_notes, destination_branch_id, status, pending_reason,
        branch_code, sequence, submitted_at_utc
        """;

    private readonly NpgsqlDataSource _dataSource;
    private readonly Func<DateTimeOffset> _clock;
    private readonly ILogger<PostgresOrderStore> _logger;
    private readonly Action? _afterCommit;

    /// <param name="afterCommit">Test seam, invoked right after the order's transaction committed and before
    /// delivery starts: the well-defined point at which a client may disconnect.</param>
    public PostgresOrderStore(
        NpgsqlDataSource dataSource, Func<DateTimeOffset>? clock = null, ILogger<PostgresOrderStore>? logger = null,
        Action? afterCommit = null)
    {
        _afterCommit = afterCommit;
        _dataSource = dataSource;
        _clock = clock ?? (() => DateTimeOffset.UtcNow);
        _logger = logger ?? NullLogger<PostgresOrderStore>.Instance;
    }

    public async Task<OrderSubmissionOutcome> SubmitAsync(
        CloudTenantScope scope,
        Guid orderId,
        OrderOrigin origin,
        Guid? customerId,
        GuestContact? guestContact,
        Guid destinationBranchId,
        Guid actorId,
        IReadOnlyList<OrderLineSnapshot> lines,
        Guid correlationId,
        BranchSyncStore? destination,
        bool hasAvailableStock,
        GuestVerificationConsumption? verification,
        CancellationToken ct)
    {
        // The same order id racing through two different branch locks loses the primary-key race in
        // one of them; that transaction rolls back and the second pass finds the winner.
        for (var attempt = 0; ; attempt++)
        {
            var outcome = await TrySubmitAsync(
                scope, orderId, origin, customerId, guestContact, destinationBranchId, actorId, lines, correlationId,
                destination, hasAvailableStock, verification, ct);
            if (outcome is not null) return outcome;
            if (attempt >= 2) throw new InvalidOperationException($"Order {orderId} could not be stored after repeated key conflicts.");
        }
    }

    private async Task<OrderSubmissionOutcome?> TrySubmitAsync(
        CloudTenantScope scope, Guid orderId, OrderOrigin origin, Guid? customerId, GuestContact? guestContact,
        Guid destinationBranchId, Guid actorId, IReadOnlyList<OrderLineSnapshot> lines, Guid correlationId,
        BranchSyncStore? destination, bool hasAvailableStock, GuestVerificationConsumption? verification, CancellationToken ct)
    {
        await using var connection = await _dataSource.OpenConnectionAsync(ct);
        await using var tx = await connection.BeginTransactionAsync(ct);
        await TenantScopeSql.ApplyAsync(connection, tx, scope, ct);

        var existing = await ReadOrderAsync(connection, tx, scope.OrganizationId, orderId, ct);
        if (existing is not null)
        {
            return await ExistingOutcomeAsync(connection, tx, scope, existing, verification, ct);
        }

        var branchCode = await FindBranchCodeAsync(connection, tx, scope.OrganizationId, destinationBranchId, ct);
        if (branchCode is null)
        {
            return Denied(OrderSubmissionReasons.DestinationBranchNotFound);
        }

        await using (var lockCmd = new NpgsqlCommand("SELECT pg_advisory_xact_lock(hashtextextended($1, $2))", connection, tx))
        {
            lockCmd.Parameters.AddWithValue(destinationBranchId.ToString());
            lockCmd.Parameters.AddWithValue((long)OrderCounterLockSeed);
            await lockCmd.ExecuteNonQueryAsync(ct);
        }

        existing = await ReadOrderAsync(connection, tx, scope.OrganizationId, orderId, ct);
        if (existing is not null)
        {
            return await ExistingOutcomeAsync(connection, tx, scope, existing, verification, ct);
        }

        if (verification is not null && !await ConsumeVerificationAsync(connection, tx, scope.OrganizationId, orderId, verification, ct))
        {
            return Denied(OrderSubmissionReasons.VerificationInvalid);
        }

        var sequence = await NextSequenceAsync(connection, tx, scope.OrganizationId, destinationBranchId, ct);
        var submittedAt = Truncate(_clock());
        var order = new Order(
            orderId, scope.OrganizationId, origin, customerId, guestContact, destinationBranchId, lines, submittedAt,
            new OrderNumber(branchCode.Value, sequence));
        // Stored pending/offline until a delivery is actually attempted after the commit.
        order.MarkPending(OrderPendingReason.DestinationOffline);

        if (!await InsertOrderAsync(connection, tx, order, ct))
        {
            return null; // lost the primary-key race: roll back and read the winner
        }

        await InsertLinesAsync(connection, tx, order, ct);

        await tx.CommitAsync(ct);

        _afterCommit?.Invoke();
        await DeliverAfterCommitAsync(connection, order, actorId, correlationId, destination, hasAvailableStock);
        return new OrderSubmissionOutcome(
            OrderSubmissionOutcomeStatus.Accepted, OrderSubmissionReasons.Accepted, order, WasNewlyAccepted: true);
    }

    /// <summary>
    /// Delivery of a committed order and persistence of its new state. Failures are contained on purpose:
    /// the order is already durable, so it stays pending/offline and the caller still gets the accepted
    /// outcome; a retry repeats delivery idempotently (the order id is the envelope operation id). A failure
    /// is logged at Error level (order id and number, branch, exception) so an order stuck pending is
    /// visible. Request cancellation is deliberately NOT honoured here: the order is committed, so a
    /// disconnecting client must not turn an accepted order into a reported failure or skip persisting
    /// the delivery state (the follow-up write is short and bounded by the connection timeouts).
    /// </summary>
    private async Task DeliverAfterCommitAsync(
        NpgsqlConnection connection, Order order, Guid actorId, Guid correlationId, BranchSyncStore? destination,
        bool hasAvailableStock)
    {
        var ct = CancellationToken.None;
        var storedStatus = order.Status;
        var storedReason = order.PendingReason;
        try
        {
            OrderDelivery.Attempt(order, actorId, correlationId, destination, hasAvailableStock, _clock());
            if (order.Status == storedStatus && order.PendingReason == storedReason) return;

            await using var tx = await connection.BeginTransactionAsync(ct);
            await TenantScopeSql.ApplyAsync(connection, tx, new CloudTenantScope(order.OrganizationId), ct);
            await UpdateDeliveryStateAsync(connection, tx, order, ct);
            await tx.CommitAsync(ct);
        }
        catch (Exception ex)
        {
            _logger.LogError(
                ex,
                "Delivery of committed order {OrderId} ({OrderNumber}) to branch {BranchId} failed; the order stays pending and can be retried",
                order.OrderId, order.OrderNumber, order.DestinationBranchId);

            // Report what is durable, not what the failed attempt wished for.
            if (storedStatus == OrderDeliveryStatus.DestinationConfirmed) order.MarkDestinationConfirmed();
            else order.MarkPending(storedReason);
        }
    }

    public async Task<OrderSubmissionOutcome> RetryDeliveryAsync(
        CloudTenantScope scope, Guid orderId, Guid actorId, Guid correlationId,
        BranchSyncStore destination, bool hasAvailableStock, CancellationToken ct)
    {
        await using var connection = await _dataSource.OpenConnectionAsync(ct);
        await using var tx = await connection.BeginTransactionAsync(ct);
        await TenantScopeSql.ApplyAsync(connection, tx, scope, ct);

        var order = await ReadOrderAsync(connection, tx, scope.OrganizationId, orderId, ct, forUpdate: true);
        if (order is null)
        {
            return Denied(OrderSubmissionReasons.NotFound);
        }

        OrderDelivery.Attempt(order, actorId, correlationId, destination, hasAvailableStock, _clock());
        await UpdateDeliveryStateAsync(connection, tx, order, ct);

        await tx.CommitAsync(ct);
        return new OrderSubmissionOutcome(
            OrderSubmissionOutcomeStatus.Accepted, OrderSubmissionReasons.Retried, order, WasNewlyAccepted: false);
    }

    public async Task<Order?> FindAsync(CloudTenantScope scope, Guid orderId, CancellationToken ct)
    {
        await using var connection = await _dataSource.OpenConnectionAsync(ct);
        await using var tx = await connection.BeginTransactionAsync(ct);
        await TenantScopeSql.ApplyAsync(connection, tx, scope, ct);

        var order = await ReadOrderAsync(connection, tx, scope.OrganizationId, orderId, ct);
        await tx.CommitAsync(ct);
        return order;
    }

    public async Task<IReadOnlyList<Order>> ListPendingAsync(
        CloudTenantScope scope, int limit = IOrderStore.DefaultPendingLimit, CancellationToken ct = default)
    {
        await using var connection = await _dataSource.OpenConnectionAsync(ct);
        await using var tx = await connection.BeginTransactionAsync(ct);
        await TenantScopeSql.ApplyAsync(connection, tx, scope, ct);

        // Orders first, then ONLY their lines: an order is committed together with its lines, so any
        // order this statement saw has its lines visible to the next one (the other way round could
        // read lines before an order committed, then find that order without them).
        var headers = new List<OrderRow>();
        await using (var ordersCmd = new NpgsqlCommand(
            $"""
            SELECT {OrderColumns} FROM orders
            WHERE organization_id = $1 AND status = 'PendingDestination'
            ORDER BY CASE origin WHEN 'RegisteredCustomer' THEN 0 ELSE 1 END, submitted_at_utc, sequence, order_id
            LIMIT $2
            """, connection, tx))
        {
            ordersCmd.Parameters.AddWithValue(scope.OrganizationId);
            ordersCmd.Parameters.AddWithValue(Math.Clamp(limit, 1, IOrderStore.MaxPendingLimit));
            await using var reader = await ordersCmd.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct))
            {
                headers.Add(ReadRow(reader));
            }
        }

        var linesByOrder = new Dictionary<Guid, List<OrderLineSnapshot>>();
        if (headers.Count > 0)
        {
            await using var linesCmd = new NpgsqlCommand(
                LinesSelect + " WHERE organization_id = $1 AND order_id = ANY($2) ORDER BY order_id, line_no", connection, tx);
            linesCmd.Parameters.AddWithValue(scope.OrganizationId);
            linesCmd.Parameters.AddWithValue(headers.Select(h => h.OrderId).ToArray());
            await using var reader = await linesCmd.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct))
            {
                var id = reader.GetGuid(0);
                if (!linesByOrder.TryGetValue(id, out var list)) linesByOrder[id] = list = [];
                list.Add(ReadLine(reader, offset: 1));
            }
        }

        await tx.CommitAsync(ct);
        return headers.Select(h => Rehydrate(h, scope.OrganizationId, linesByOrder.GetValueOrDefault(h.OrderId) ?? [])).ToList();
    }

    // ---------------------------------------------------------------------------------------------

    private static async Task<OrderSubmissionOutcome> ExistingOutcomeAsync(
        NpgsqlConnection connection, NpgsqlTransaction tx, CloudTenantScope scope, Order existing,
        GuestVerificationConsumption? verification, CancellationToken ct)
    {
        // A guest retry (lost response) gets the same order without a fresh verification, but only
        // when it presents the very ticket that was spent on this order.
        if (verification is not null && !await VerificationBelongsToOrderAsync(connection, tx, scope.OrganizationId, existing.OrderId, verification, ct))
        {
            return Denied(OrderSubmissionReasons.VerificationInvalid);
        }

        await tx.CommitAsync(ct);
        return new OrderSubmissionOutcome(
            OrderSubmissionOutcomeStatus.Accepted, OrderSubmissionReasons.ExistingOrder, existing, WasNewlyAccepted: false);
    }

    private static OrderSubmissionOutcome Denied(string reason) =>
        new(OrderSubmissionOutcomeStatus.Denied, reason, Order: null, WasNewlyAccepted: false);

    /// <summary>Postgres keeps microseconds; keep the in-memory value identical to what is stored.</summary>
    private static DateTimeOffset Truncate(DateTimeOffset value) =>
        new(value.UtcTicks - value.UtcTicks % 10, TimeSpan.Zero);

    private static async Task<BranchCode?> FindBranchCodeAsync(
        NpgsqlConnection connection, NpgsqlTransaction tx, Guid organizationId, Guid branchId, CancellationToken ct)
    {
        await using var cmd = new NpgsqlCommand("SELECT code FROM branches WHERE organization_id = $1 AND id = $2", connection, tx);
        cmd.Parameters.AddWithValue(organizationId);
        cmd.Parameters.AddWithValue(branchId);
        var code = await cmd.ExecuteScalarAsync(ct);
        return code is null or DBNull ? null : new BranchCode(Convert.ToInt32(code, CultureInfo.InvariantCulture));
    }

    private static async Task<int> NextSequenceAsync(
        NpgsqlConnection connection, NpgsqlTransaction tx, Guid organizationId, Guid branchId, CancellationToken ct)
    {
        await using var cmd = new NpgsqlCommand(
            "SELECT COALESCE(MAX(sequence), 0) + 1 FROM orders WHERE organization_id = $1 AND destination_branch_id = $2",
            connection, tx);
        cmd.Parameters.AddWithValue(organizationId);
        cmd.Parameters.AddWithValue(branchId);
        return Convert.ToInt32(await cmd.ExecuteScalarAsync(ct), CultureInfo.InvariantCulture);
    }

    /// <summary>
    /// Spends the ticket in the order transaction: ONE conditional UPDATE (unconsumed, confirmed inside the
    /// confirm-to-submit window, same document id and contact address), so two submissions can never both
    /// win and a rollback gives the ticket back.
    /// </summary>
    private static async Task<bool> ConsumeVerificationAsync(
        NpgsqlConnection connection, NpgsqlTransaction tx, Guid organizationId, Guid orderId,
        GuestVerificationConsumption verification, CancellationToken ct)
    {
        await using var cmd = new NpgsqlCommand(
            """
            UPDATE guest_order_verifications SET consumed_at = now(), consumed_order_id = $1
            WHERE id = $2 AND organization_id = $3 AND consumed_at IS NULL
              AND confirmed_at IS NOT NULL AND confirmed_at > $4
              AND document_id = $5 AND lower(contact_address) = lower($6)
            """, connection, tx);
        cmd.Parameters.AddWithValue(orderId);
        cmd.Parameters.AddWithValue(verification.VerificationId);
        cmd.Parameters.AddWithValue(organizationId);
        cmd.Parameters.AddWithValue(verification.ConfirmedAfter);
        cmd.Parameters.AddWithValue(verification.DocumentId);
        cmd.Parameters.AddWithValue(verification.ContactAddress);
        return await cmd.ExecuteNonQueryAsync(ct) == 1;
    }

    private static async Task<bool> VerificationBelongsToOrderAsync(
        NpgsqlConnection connection, NpgsqlTransaction tx, Guid organizationId, Guid orderId,
        GuestVerificationConsumption verification, CancellationToken ct)
    {
        await using var cmd = new NpgsqlCommand(
            """
            SELECT 1 FROM guest_order_verifications
            WHERE id = $1 AND organization_id = $2 AND consumed_order_id = $3
              AND document_id = $4 AND lower(contact_address) = lower($5)
            """, connection, tx);
        cmd.Parameters.AddWithValue(verification.VerificationId);
        cmd.Parameters.AddWithValue(organizationId);
        cmd.Parameters.AddWithValue(orderId);
        cmd.Parameters.AddWithValue(verification.DocumentId);
        cmd.Parameters.AddWithValue(verification.ContactAddress);
        return await cmd.ExecuteScalarAsync(ct) is not null;
    }

    private static async Task<bool> InsertOrderAsync(NpgsqlConnection connection, NpgsqlTransaction tx, Order order, CancellationToken ct)
    {
        var guest = order.GuestContact;
        var number = order.OrderNumber!.Value;
        await using var cmd = new NpgsqlCommand(
            """
            INSERT INTO orders (organization_id, order_id, destination_branch_id, origin, customer_id,
                guest_document_id, guest_channel, guest_contact_address, guest_display_name, guest_delivery_notes,
                status, pending_reason, branch_code, sequence, submitted_at_utc)
            VALUES ($1, $2, $3, $4, $5, $6, $7, $8, $9, $10, $11, $12, $13, $14, $15)
            ON CONFLICT (organization_id, order_id) DO NOTHING
            """, connection, tx);
        cmd.Parameters.AddWithValue(order.OrganizationId);
        cmd.Parameters.AddWithValue(order.OrderId);
        cmd.Parameters.AddWithValue(order.DestinationBranchId);
        cmd.Parameters.AddWithValue(order.Origin.ToString());
        cmd.Parameters.Add(new NpgsqlParameter { Value = (object?)order.CustomerId ?? DBNull.Value, NpgsqlDbType = NpgsqlTypes.NpgsqlDbType.Uuid });
        cmd.Parameters.Add(Text(guest?.DocumentId));
        cmd.Parameters.Add(Text(guest?.Channel.ToString()));
        cmd.Parameters.Add(Text(guest?.ContactAddress));
        cmd.Parameters.Add(Text(guest?.DisplayName));
        cmd.Parameters.Add(Text(guest?.DeliveryNotes));
        cmd.Parameters.AddWithValue(order.Status.ToString());
        cmd.Parameters.AddWithValue(order.PendingReason.ToString());
        cmd.Parameters.AddWithValue((short)number.Branch.Value);
        cmd.Parameters.AddWithValue(number.Sequence);
        cmd.Parameters.AddWithValue(order.SubmittedAtUtc);
        return await cmd.ExecuteNonQueryAsync(ct) == 1;
    }

    private static NpgsqlParameter Text(string? value) =>
        new() { Value = (object?)value ?? DBNull.Value, NpgsqlDbType = NpgsqlTypes.NpgsqlDbType.Text };

    private static async Task InsertLinesAsync(NpgsqlConnection connection, NpgsqlTransaction tx, Order order, CancellationToken ct)
    {
        var lineNo = 0;
        foreach (var line in order.Lines)
        {
            await using var cmd = new NpgsqlCommand(
                """
                INSERT INTO order_lines (organization_id, order_id, line_no, product_id, product_name, presentation_id,
                    presentation_name, quantity_behavior, unit_id, quantity, unit_list_price,
                    applied_discount_percentage, unit_net_price, line_total, priced_from_price_list_id, price_fell_back)
                VALUES ($1, $2, $3, $4, $5, $6, $7, $8, $9, $10, $11, $12, $13, $14, $15, $16)
                """, connection, tx);
            cmd.Parameters.AddWithValue(order.OrganizationId);
            cmd.Parameters.AddWithValue(order.OrderId);
            cmd.Parameters.AddWithValue(++lineNo);
            cmd.Parameters.AddWithValue(line.ProductId);
            cmd.Parameters.AddWithValue(line.ProductName);
            cmd.Parameters.AddWithValue(line.PresentationId);
            cmd.Parameters.AddWithValue(line.PresentationName);
            cmd.Parameters.AddWithValue(line.QuantityBehavior.ToString());
            cmd.Parameters.AddWithValue(line.UnitId);
            cmd.Parameters.AddWithValue(line.Quantity);
            cmd.Parameters.AddWithValue(line.UnitListPrice);
            cmd.Parameters.AddWithValue(line.AppliedDiscountPercentage);
            cmd.Parameters.AddWithValue(line.UnitNetPrice);
            cmd.Parameters.AddWithValue(line.LineTotal);
            cmd.Parameters.Add(new NpgsqlParameter { Value = (object?)line.PricedFromListId ?? DBNull.Value, NpgsqlDbType = NpgsqlTypes.NpgsqlDbType.Uuid });
            cmd.Parameters.AddWithValue(line.FellBack);
            await cmd.ExecuteNonQueryAsync(ct);
        }
    }

    private static async Task UpdateDeliveryStateAsync(NpgsqlConnection connection, NpgsqlTransaction tx, Order order, CancellationToken ct)
    {
        await using var cmd = new NpgsqlCommand(
            "UPDATE orders SET status = $1, pending_reason = $2 WHERE organization_id = $3 AND order_id = $4", connection, tx);
        cmd.Parameters.AddWithValue(order.Status.ToString());
        cmd.Parameters.AddWithValue(order.PendingReason.ToString());
        cmd.Parameters.AddWithValue(order.OrganizationId);
        cmd.Parameters.AddWithValue(order.OrderId);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    private const string LinesSelect =
        """
        SELECT order_id, product_id, product_name, presentation_id, presentation_name, quantity_behavior, unit_id,
               quantity, unit_list_price, applied_discount_percentage, unit_net_price, line_total,
               priced_from_price_list_id, price_fell_back
        FROM order_lines
        """;

    private static async Task<Order?> ReadOrderAsync(
        NpgsqlConnection connection, NpgsqlTransaction tx, Guid organizationId, Guid orderId, CancellationToken ct,
        bool forUpdate = false)
    {
        // The order first, then its lines (see ListPendingAsync: never read lines before the order).
        OrderRow? row;
        await using (var cmd = new NpgsqlCommand(
            $"SELECT {OrderColumns} FROM orders WHERE organization_id = $1 AND order_id = $2" + (forUpdate ? " FOR UPDATE" : ""),
            connection, tx))
        {
            cmd.Parameters.AddWithValue(organizationId);
            cmd.Parameters.AddWithValue(orderId);
            await using var orderReader = await cmd.ExecuteReaderAsync(ct);
            row = await orderReader.ReadAsync(ct) ? ReadRow(orderReader) : null;
        }
        if (row is null) return null;

        var lines = new List<OrderLineSnapshot>();
        await using var linesCmd = new NpgsqlCommand(
            LinesSelect + " WHERE organization_id = $1 AND order_id = $2 ORDER BY line_no", connection, tx);
        linesCmd.Parameters.AddWithValue(organizationId);
        linesCmd.Parameters.AddWithValue(orderId);
        await using var reader = await linesCmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            lines.Add(ReadLine(reader, offset: 1));
        }
        return Rehydrate(row, organizationId, lines);
    }

    private static OrderLineSnapshot ReadLine(NpgsqlDataReader reader, int offset) => new(
        ProductId: reader.GetGuid(offset),
        ProductName: reader.GetString(offset + 1),
        PresentationId: reader.GetGuid(offset + 2),
        PresentationName: reader.GetString(offset + 3),
        QuantityBehavior: Enum.Parse<QuantityBehavior>(reader.GetString(offset + 4)),
        UnitId: reader.GetGuid(offset + 5),
        Quantity: reader.GetDecimal(offset + 6),
        UnitListPrice: reader.GetDecimal(offset + 7),
        AppliedDiscountPercentage: reader.GetDecimal(offset + 8),
        UnitNetPrice: reader.GetDecimal(offset + 9),
        LineTotal: reader.GetDecimal(offset + 10),
        PricedFromListId: reader.IsDBNull(offset + 11) ? null : reader.GetGuid(offset + 11),
        FellBack: reader.GetBoolean(offset + 12));

    /// <summary>One <c>orders</c> row, read completely so the reader can be closed before the lines are queried.</summary>
    private sealed record OrderRow(
        Guid OrderId, string Origin, Guid? CustomerId, string? GuestDocumentId, string? GuestChannel,
        string? GuestContactAddress, string? GuestDisplayName, string? GuestDeliveryNotes, Guid DestinationBranchId,
        string Status, string PendingReason, short BranchCode, int Sequence, DateTimeOffset SubmittedAtUtc);

    /// <summary>Column order of <see cref="OrderColumns"/>.</summary>
    private static OrderRow ReadRow(NpgsqlDataReader reader)
    {
        static string? Text(NpgsqlDataReader r, int i) => r.IsDBNull(i) ? null : r.GetString(i);
        return new OrderRow(
            reader.GetGuid(0), reader.GetString(1), reader.IsDBNull(2) ? null : reader.GetGuid(2),
            Text(reader, 3), Text(reader, 4), Text(reader, 5), Text(reader, 6), Text(reader, 7),
            reader.GetGuid(8), reader.GetString(9), reader.GetString(10), reader.GetInt16(11), reader.GetInt32(12),
            reader.GetFieldValue<DateTimeOffset>(13));
    }

    /// <summary>The constructor re-checks the origin invariant on the way back in.</summary>
    private static Order Rehydrate(OrderRow row, Guid organizationId, IReadOnlyList<OrderLineSnapshot> lines)
    {
        var origin = Enum.Parse<OrderOrigin>(row.Origin);
        GuestContact? guest = origin == OrderOrigin.Guest
            ? new GuestContact(
                row.GuestDocumentId!, Enum.Parse<GuestContactChannel>(row.GuestChannel!), row.GuestContactAddress!,
                row.GuestDisplayName!, row.GuestDeliveryNotes)
            : null;

        var order = new Order(
            row.OrderId, organizationId, origin, row.CustomerId, guest, row.DestinationBranchId, lines, row.SubmittedAtUtc,
            new OrderNumber(new BranchCode(row.BranchCode), row.Sequence));

        if (Enum.Parse<OrderDeliveryStatus>(row.Status) == OrderDeliveryStatus.DestinationConfirmed)
        {
            order.MarkDestinationConfirmed();
        }
        else
        {
            order.MarkPending(Enum.Parse<OrderPendingReason>(row.PendingReason));
        }

        return order;
    }
}
