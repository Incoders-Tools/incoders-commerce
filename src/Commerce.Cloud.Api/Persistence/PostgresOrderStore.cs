using System.Globalization;
using Commerce.BranchNode;
using Commerce.Cloud.Api.Ordering;
using Commerce.Cloud.Api.Tenancy;
using Commerce.Domain.Catalog;
using Commerce.Domain.Ordering;
using Commerce.Domain.Tenancy;
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
/// <c>MAX(sequence)+1</c> -> insert order and lines -> attempt delivery and persist its state. Any
/// exception rolls the whole transaction back: no order, no consumed verification and no advanced counter.
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

    public PostgresOrderStore(NpgsqlDataSource dataSource, Func<DateTimeOffset>? clock = null)
    {
        _dataSource = dataSource;
        _clock = clock ?? (() => DateTimeOffset.UtcNow);
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

        if (!await InsertOrderAsync(connection, tx, order, ct))
        {
            return null; // lost the primary-key race: roll back and read the winner
        }

        await InsertLinesAsync(connection, tx, order, ct);

        OrderDelivery.Attempt(order, actorId, correlationId, destination, hasAvailableStock, _clock());
        await UpdateDeliveryStateAsync(connection, tx, order, ct);

        await tx.CommitAsync(ct);
        return new OrderSubmissionOutcome(
            OrderSubmissionOutcomeStatus.Accepted, OrderSubmissionReasons.Accepted, order, WasNewlyAccepted: true);
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

    public async Task<IReadOnlyList<Order>> ListPendingAsync(CloudTenantScope scope, CancellationToken ct)
    {
        await using var connection = await _dataSource.OpenConnectionAsync(ct);
        await using var tx = await connection.BeginTransactionAsync(ct);
        await TenantScopeSql.ApplyAsync(connection, tx, scope, ct);

        var linesByOrder = new Dictionary<Guid, List<OrderLineSnapshot>>();
        await using (var linesCmd = new NpgsqlCommand(LinesSelect + " WHERE organization_id = $1 ORDER BY order_id, line_no", connection, tx))
        {
            linesCmd.Parameters.AddWithValue(scope.OrganizationId);
            await using var reader = await linesCmd.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct))
            {
                var id = reader.GetGuid(0);
                if (!linesByOrder.TryGetValue(id, out var list)) linesByOrder[id] = list = [];
                list.Add(ReadLine(reader, offset: 1));
            }
        }

        var orders = new List<Order>();
        await using (var ordersCmd = new NpgsqlCommand(
            $"""
            SELECT {OrderColumns} FROM orders WHERE organization_id = $1
            ORDER BY CASE origin WHEN 'RegisteredCustomer' THEN 0 ELSE 1 END, submitted_at_utc, sequence
            """, connection, tx))
        {
            ordersCmd.Parameters.AddWithValue(scope.OrganizationId);
            await using var reader = await ordersCmd.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct))
            {
                var orderId = reader.GetGuid(0);
                orders.Add(Rehydrate(reader, scope.OrganizationId, linesByOrder.GetValueOrDefault(orderId) ?? []));
            }
        }

        await tx.CommitAsync(ct);
        return orders;
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
                    applied_discount_percentage, unit_net_price, line_total)
                VALUES ($1, $2, $3, $4, $5, $6, $7, $8, $9, $10, $11, $12, $13, $14)
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
               quantity, unit_list_price, applied_discount_percentage, unit_net_price, line_total
        FROM order_lines
        """;

    private static async Task<Order?> ReadOrderAsync(
        NpgsqlConnection connection, NpgsqlTransaction tx, Guid organizationId, Guid orderId, CancellationToken ct,
        bool forUpdate = false)
    {
        var lines = new List<OrderLineSnapshot>();
        await using (var linesCmd = new NpgsqlCommand(
            LinesSelect + " WHERE organization_id = $1 AND order_id = $2 ORDER BY line_no", connection, tx))
        {
            linesCmd.Parameters.AddWithValue(organizationId);
            linesCmd.Parameters.AddWithValue(orderId);
            await using var reader = await linesCmd.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct))
            {
                lines.Add(ReadLine(reader, offset: 1));
            }
        }

        await using var cmd = new NpgsqlCommand(
            $"SELECT {OrderColumns} FROM orders WHERE organization_id = $1 AND order_id = $2" + (forUpdate ? " FOR UPDATE" : ""),
            connection, tx);
        cmd.Parameters.AddWithValue(organizationId);
        cmd.Parameters.AddWithValue(orderId);
        await using var orderReader = await cmd.ExecuteReaderAsync(ct);
        return await orderReader.ReadAsync(ct) ? Rehydrate(orderReader, organizationId, lines) : null;
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
        LineTotal: reader.GetDecimal(offset + 10));

    /// <summary>Column order of <see cref="OrderColumns"/>. The constructor re-checks the origin invariant on the way back in.</summary>
    private static Order Rehydrate(NpgsqlDataReader reader, Guid organizationId, IReadOnlyList<OrderLineSnapshot> lines)
    {
        var origin = Enum.Parse<OrderOrigin>(reader.GetString(1));
        GuestContact? guest = origin == OrderOrigin.Guest
            ? new GuestContact(
                reader.GetString(3), Enum.Parse<GuestContactChannel>(reader.GetString(4)), reader.GetString(5),
                reader.GetString(6), reader.IsDBNull(7) ? null : reader.GetString(7))
            : null;

        var order = new Order(
            reader.GetGuid(0), organizationId, origin, reader.IsDBNull(2) ? null : reader.GetGuid(2), guest,
            reader.GetGuid(8), lines, reader.GetFieldValue<DateTimeOffset>(13),
            new OrderNumber(new BranchCode(reader.GetInt16(11)), reader.GetInt32(12)));

        if (Enum.Parse<OrderDeliveryStatus>(reader.GetString(9)) == OrderDeliveryStatus.DestinationConfirmed)
        {
            order.MarkDestinationConfirmed();
        }
        else
        {
            order.MarkPending(Enum.Parse<OrderPendingReason>(reader.GetString(10)));
        }

        return order;
    }
}
