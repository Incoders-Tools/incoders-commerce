using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Commerce.Cloud.Api.Auditing;
using Commerce.Cloud.Api.Tenancy;
using Commerce.Domain.CurrentAccounts;
using Commerce.Domain.Ordering;
using Commerce.Domain.Stock;
using Commerce.Domain.Tenancy;
using Npgsql;

namespace Commerce.Cloud.Api.Persistence;

/// <summary>
/// Order fulfillment of the SELECTED BRANCH (0045): the tracking list and status changes of its orders, its delivery
/// runs (repartos), the settlement of a run when the truck returns, and the remitos (delivery notes) of its orders.
/// <para>
/// Every operation runs in one transaction under the caller's tenant scope (organization AND branch: the stock ledger is
/// branch-scoped) and locks the rows it changes (<c>FOR UPDATE</c>), so two operators cannot move the same order or run
/// at once. Every change writes an audit row in the same transaction.
/// </para>
/// <para>
/// SETTLEMENT (<see cref="SettleRunAsync"/>), per order of the run, as one unit: what was really delivered per line is
/// recorded (<c>order_line_deliveries</c>), the stock goes down by it (one <c>Sale</c> movement per delivered line, source
/// <c>OrderDelivery</c>, keyed by order and line so a retry writes nothing twice), and the delivered value (net prices x
/// delivered quantities) is posted as a Debit on the customer's current account unless it was paid on delivery (always
/// for a guest, who has no account; posting is keyed by the order, so it happens once). An order with nothing delivered
/// goes back to ReadyToDispatch, out of the run, for another one. Orders never move stock before this: not when taken,
/// not when dispatched.
/// </para>
/// </summary>
public sealed class PostgresFulfillmentStore
{
    public const string OrderDeliverySource = "OrderDelivery";
    public const string OrderDeliveryPaymentSource = "OrderDeliveryPayment";
    private const int ListLimit = 500;

    private readonly NpgsqlDataSource _dataSource;

    public PostgresFulfillmentStore(NpgsqlDataSource dataSource) => _dataSource = dataSource;

    /// <summary>Stable id of a delivered order line: the first 16 bytes of SHA-256("order-delivery-line:{orderId}:{lineNo}").</summary>
    public static Guid LineKey(Guid orderId, int lineNo) =>
        new(SHA256.HashData(Encoding.UTF8.GetBytes($"order-delivery-line:{orderId:D}:{lineNo}"))[..16]);

    // ---------------------------------------------------------------- orders

    private const string SummarySelect = """
        SELECT o.order_id, o.branch_code, o.sequence, o.submitted_at_utc, o.origin, o.customer_id,
               COALESCE(c.display_name, o.guest_display_name, ''),
               o.fulfillment_status,
               COALESCE((SELECT SUM(l.line_total) FROM order_lines l
                         WHERE l.organization_id = o.organization_id AND l.order_id = o.order_id), 0),
               (SELECT COUNT(*) FROM order_lines l WHERE l.organization_id = o.organization_id AND l.order_id = o.order_id),
               r.id, r.run_number, r.run_date,
               o.remito_sequence, o.delivered_total, o.settlement, o.delivered_at, o.note, o.cancel_reason
        FROM orders o
        LEFT JOIN customers c ON c.organization_id = o.organization_id AND c.id = o.customer_id
        LEFT JOIN delivery_run_orders ro ON ro.organization_id = o.organization_id AND ro.order_id = o.order_id
        LEFT JOIN delivery_runs r ON r.organization_id = ro.organization_id AND r.id = ro.run_id
        """;

    private const string OrderNumberSql = "('P' || lpad(o.branch_code::text, 2, '0') || '-W-' || o.sequence::text)";

    /// <summary>
    /// The branch's orders, newest first: by status (<c>null</c> = all, <c>"Active"</c> = not delivered nor cancelled),
    /// submitted in [<paramref name="from"/>, <paramref name="to"/>] (business dates, inclusive), and matching
    /// <paramref name="search"/> in the order number or the customer name.
    /// </summary>
    public async Task<IReadOnlyList<OrderTrackingSummary>> ListOrdersAsync(
        CloudTenantScope scope, string? status, DateTimeOffset? fromUtc, DateTimeOffset? toUtc, string? search, CancellationToken ct)
    {
        await using var connection = await _dataSource.OpenConnectionAsync(ct);
        await using var tx = await connection.BeginTransactionAsync(ct);
        await TenantScopeSql.ApplyAsync(connection, tx, scope, ct);

        var statusFilter = status switch
        {
            null or "" => "TRUE",
            "Active" => "o.fulfillment_status NOT IN ('Delivered', 'PartiallyDelivered', 'Cancelled')",
            _ => "o.fulfillment_status = $5",
        };
        await using var cmd = new NpgsqlCommand(
            $"""
            {SummarySelect}
            WHERE o.destination_branch_id = $1
              AND ($2::timestamptz IS NULL OR o.submitted_at_utc >= $2)
              AND ($3::timestamptz IS NULL OR o.submitted_at_utc < $3)
              AND ($4::text IS NULL OR c.display_name ILIKE $4 OR c.legal_name ILIKE $4 OR o.guest_display_name ILIKE $4
                   OR {OrderNumberSql} ILIKE $4)
              AND {statusFilter}
            ORDER BY o.submitted_at_utc DESC
            LIMIT {ListLimit}
            """, connection, tx);
        cmd.Parameters.AddWithValue(scope.BranchId!.Value);
        cmd.Parameters.AddWithValue(NpgsqlTypes.NpgsqlDbType.TimestampTz, (object?)fromUtc ?? DBNull.Value);
        cmd.Parameters.AddWithValue(NpgsqlTypes.NpgsqlDbType.TimestampTz, (object?)toUtc ?? DBNull.Value);
        cmd.Parameters.AddWithValue(NpgsqlTypes.NpgsqlDbType.Text,
            string.IsNullOrWhiteSpace(search) ? DBNull.Value : $"%{EscapeLike(search.Trim())}%");
        cmd.Parameters.AddWithValue(NpgsqlTypes.NpgsqlDbType.Text, (object?)status ?? DBNull.Value);

        var results = new List<OrderTrackingSummary>();
        await using (var reader = await cmd.ExecuteReaderAsync(ct))
        {
            while (await reader.ReadAsync(ct))
            {
                results.Add(ReadSummary(reader));
            }
        }

        await tx.CommitAsync(ct);
        return results;
    }

    public async Task<OrderTrackingDetail?> GetOrderAsync(CloudTenantScope scope, Guid orderId, CancellationToken ct)
    {
        await using var connection = await _dataSource.OpenConnectionAsync(ct);
        await using var tx = await connection.BeginTransactionAsync(ct);
        await TenantScopeSql.ApplyAsync(connection, tx, scope, ct);

        var summary = await ReadSummaryAsync(connection, tx, scope.BranchId!.Value, orderId, ct);
        if (summary is null)
        {
            await tx.CommitAsync(ct);
            return null;
        }

        var lines = await ReadLinesAsync(connection, tx, orderId, ct);
        var party = await ReadPartyAsync(connection, tx, orderId, ct);
        await tx.CommitAsync(ct);

        var status = Enum.Parse<OrderFulfillmentStatus>(summary.Status);
        return new OrderTrackingDetail(
            summary, lines, party,
            [.. OrderFulfillmentRules.ManualTargets(status).Select(target => target.ToString())]);
    }

    /// <summary>
    /// One manual step of an order (<see cref="OrderFulfillmentRules.CanMoveManually"/>). Cancelling needs a reason and
    /// takes the order out of a planned run.
    /// </summary>
    public async Task<FulfillmentResult> ChangeStatusAsync(
        CloudTenantScope scope, Guid orderId, OrderFulfillmentStatus target, string? reason, Guid actorId, CancellationToken ct)
    {
        var trimmedReason = string.IsNullOrWhiteSpace(reason) ? null : reason.Trim();
        if (target == OrderFulfillmentStatus.Cancelled
            && (trimmedReason is null || trimmedReason.Length > OrderFulfillmentRules.MaxCancelReasonLength))
        {
            return FulfillmentResult.Invalid("cancel-reason-required");
        }

        await using var connection = await _dataSource.OpenConnectionAsync(ct);
        await using var tx = await connection.BeginTransactionAsync(ct);
        await TenantScopeSql.ApplyAsync(connection, tx, scope, ct);

        var current = await LockOrderStatusAsync(connection, tx, scope.BranchId!.Value, orderId, ct);
        if (current is null)
        {
            await tx.RollbackAsync(ct);
            return FulfillmentResult.NotFound();
        }

        if (!OrderFulfillmentRules.CanMoveManually(current.Value, target))
        {
            await tx.RollbackAsync(ct);
            return new FulfillmentResult(FulfillmentOutcome.InvalidTransition, $"{current.Value}->{target}");
        }

        if (target == OrderFulfillmentStatus.Cancelled)
        {
            await ExecAsync(connection, tx,
                "DELETE FROM delivery_run_orders WHERE order_id = $1 AND run_id IN (SELECT id FROM delivery_runs WHERE status = 'Planned')",
                ct, orderId);
        }

        await ExecAsync(connection, tx,
            """
            UPDATE orders SET fulfillment_status = $2, fulfillment_updated_at = now(),
                              cancel_reason = CASE WHEN $2 = 'Cancelled' THEN $3 ELSE cancel_reason END
            WHERE order_id = $1
            """, ct, orderId, target.ToString(), (object?)trimmedReason ?? DBNull.Value);
        await AuditAsync(connection, tx, scope, actorId, "order", orderId, "order.fulfillment_changed",
            new { from = current.Value.ToString(), to = target.ToString(), reason = trimmedReason }, ct);

        await tx.CommitAsync(ct);
        return FulfillmentResult.Ok(orderId);
    }

    // ---------------------------------------------------------------- remitos

    /// <summary>
    /// The remitos of the given orders of the branch, in the order asked, numbering the ones that have no number yet
    /// (a cancelled order gets none and is left out). Dated on its run's date, else <paramref name="today"/>.
    /// </summary>
    public async Task<IReadOnlyList<RemitoDocument>> GetRemitosAsync(
        CloudTenantScope scope, IReadOnlyList<Guid> orderIds, DateOnly today, CancellationToken ct)
    {
        await using var connection = await _dataSource.OpenConnectionAsync(ct);
        await using var tx = await connection.BeginTransactionAsync(ct);
        await TenantScopeSql.ApplyAsync(connection, tx, scope, ct);
        var branchId = scope.BranchId!.Value;

        var printable = new List<Guid>();
        foreach (var orderId in orderIds.Distinct())
        {
            if (await LockOrderStatusAsync(connection, tx, branchId, orderId, ct) is { } status && status != OrderFulfillmentStatus.Cancelled)
            {
                printable.Add(orderId);
            }
        }

        await AssignRemitoNumbersAsync(connection, tx, scope.OrganizationId, branchId, printable, ct);

        var organization = await ReadOrganizationProfileAsync(connection, tx, scope.OrganizationId, ct);
        var branch = await ReadBranchProfileAsync(connection, tx, branchId, ct);
        var documents = new List<RemitoDocument>();
        foreach (var orderId in printable)
        {
            var summary = (await ReadSummaryAsync(connection, tx, branchId, orderId, ct))!;
            var lines = await ReadLinesAsync(connection, tx, orderId, ct);
            var party = await ReadPartyAsync(connection, tx, orderId, ct);
            var run = summary.RunId is { } runId ? await ReadRunAsync(connection, tx, runId, ct) : null;
            var total = lines.Sum(line => line.DeliveredQuantity is { } delivered
                ? OrderFulfillmentRules.DeliveredAmount(line.UnitNetPrice, delivered)
                : line.LineTotal);
            documents.Add(new RemitoDocument(
                orderId, summary.RemitoNumber!, summary.OrderNumber, summary.RunDate ?? today, organization!, branch!, party, lines,
                summary.DeliveredTotal ?? total, run?.RunNumber, run?.DriverName, run?.Vehicle, summary.Note));
        }

        await tx.CommitAsync(ct);
        return documents;
    }

    private static async Task AssignRemitoNumbersAsync(
        NpgsqlConnection connection, NpgsqlTransaction tx, Guid organizationId, Guid branchId, IReadOnlyList<Guid> orderIds, CancellationToken ct)
    {
        var unnumbered = new List<Guid>();
        foreach (var orderId in orderIds)
        {
            await using var check = new NpgsqlCommand("SELECT remito_sequence FROM orders WHERE order_id = $1", connection, tx);
            check.Parameters.AddWithValue(orderId);
            if (await check.ExecuteScalarAsync(ct) is DBNull or null)
            {
                unnumbered.Add(orderId);
            }
        }

        if (unnumbered.Count == 0)
        {
            return;
        }

        await ExecAsync(connection, tx,
            "INSERT INTO remito_counters (organization_id, branch_id, last_sequence) VALUES ($1, $2, 0) ON CONFLICT DO NOTHING",
            ct, organizationId, branchId);
        int last;
        await using (var counter = new NpgsqlCommand(
            "SELECT last_sequence FROM remito_counters WHERE branch_id = $1 FOR UPDATE", connection, tx))
        {
            counter.Parameters.AddWithValue(branchId);
            last = (int)(await counter.ExecuteScalarAsync(ct))!;
        }

        foreach (var orderId in unnumbered)
        {
            last++;
            await ExecAsync(connection, tx, "UPDATE orders SET remito_sequence = $2 WHERE order_id = $1", ct, orderId, last);
        }

        await ExecAsync(connection, tx, "UPDATE remito_counters SET last_sequence = $2 WHERE branch_id = $1", ct, branchId, last);
    }

    // ---------------------------------------------------------------- delivery runs

    private const string RunSelect = """
        SELECT r.id, r.run_number, r.run_date, r.driver_name, r.vehicle, r.notes, r.status,
               (SELECT COUNT(*) FROM delivery_run_orders ro WHERE ro.organization_id = r.organization_id AND ro.run_id = r.id),
               COALESCE((SELECT SUM(COALESCE(o.delivered_total, (SELECT SUM(l.line_total) FROM order_lines l
                                                                 WHERE l.organization_id = o.organization_id AND l.order_id = o.order_id)))
                         FROM delivery_run_orders ro
                         JOIN orders o ON o.organization_id = ro.organization_id AND o.order_id = ro.order_id
                         WHERE ro.organization_id = r.organization_id AND ro.run_id = r.id), 0),
               r.created_at, r.dispatched_at, r.completed_at
        FROM delivery_runs r
        """;

    public async Task<IReadOnlyList<DeliveryRunSummary>> ListRunsAsync(
        CloudTenantScope scope, DateOnly? from, DateOnly? to, string? status, CancellationToken ct)
    {
        await using var connection = await _dataSource.OpenConnectionAsync(ct);
        await using var tx = await connection.BeginTransactionAsync(ct);
        await TenantScopeSql.ApplyAsync(connection, tx, scope, ct);

        await using var cmd = new NpgsqlCommand(
            $"""
            {RunSelect}
            WHERE r.branch_id = $1 AND ($2::date IS NULL OR r.run_date >= $2) AND ($3::date IS NULL OR r.run_date <= $3)
              AND ($4::text IS NULL OR r.status = $4)
            ORDER BY r.run_date DESC, r.run_number DESC
            LIMIT {ListLimit}
            """, connection, tx);
        cmd.Parameters.AddWithValue(scope.BranchId!.Value);
        cmd.Parameters.AddWithValue(NpgsqlTypes.NpgsqlDbType.Date, (object?)from ?? DBNull.Value);
        cmd.Parameters.AddWithValue(NpgsqlTypes.NpgsqlDbType.Date, (object?)to ?? DBNull.Value);
        cmd.Parameters.AddWithValue(NpgsqlTypes.NpgsqlDbType.Text, string.IsNullOrWhiteSpace(status) ? DBNull.Value : status);

        var runs = new List<DeliveryRunSummary>();
        await using (var reader = await cmd.ExecuteReaderAsync(ct))
        {
            while (await reader.ReadAsync(ct))
            {
                runs.Add(ReadRun(reader));
            }
        }

        await tx.CommitAsync(ct);
        return runs;
    }

    public async Task<DeliveryRunDetail?> GetRunAsync(CloudTenantScope scope, Guid runId, CancellationToken ct)
    {
        await using var connection = await _dataSource.OpenConnectionAsync(ct);
        await using var tx = await connection.BeginTransactionAsync(ct);
        await TenantScopeSql.ApplyAsync(connection, tx, scope, ct);

        var run = await ReadRunAsync(connection, tx, runId, ct);
        if (run is null || !await RunBelongsToBranchAsync(connection, tx, runId, scope.BranchId!.Value, ct))
        {
            await tx.CommitAsync(ct);
            return null;
        }

        var stops = new List<(int StopNo, Guid OrderId)>();
        await using (var cmd = new NpgsqlCommand(
            "SELECT stop_no, order_id FROM delivery_run_orders WHERE run_id = $1 ORDER BY stop_no, order_id", connection, tx))
        {
            cmd.Parameters.AddWithValue(runId);
            await using var reader = await cmd.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct))
            {
                stops.Add((reader.GetInt32(0), reader.GetGuid(1)));
            }
        }

        var detail = new List<DeliveryRunStop>();
        foreach (var (stopNo, orderId) in stops)
        {
            detail.Add(new DeliveryRunStop(stopNo, (await ReadSummaryAsync(connection, tx, scope.BranchId!.Value, orderId, ct))!));
        }

        await tx.CommitAsync(ct);
        return new DeliveryRunDetail(run, detail);
    }

    /// <summary>Creates a planned run with the given orders as its stops, in that order. Every order must be able to join a run and be in no other.</summary>
    public async Task<FulfillmentResult> CreateRunAsync(
        CloudTenantScope scope, DateOnly runDate, string? driverName, string? vehicle, string? notes,
        IReadOnlyList<Guid> orderIds, Guid actorId, CancellationToken ct)
    {
        await using var connection = await _dataSource.OpenConnectionAsync(ct);
        await using var tx = await connection.BeginTransactionAsync(ct);
        await TenantScopeSql.ApplyAsync(connection, tx, scope, ct);
        var branchId = scope.BranchId!.Value;

        // One run number at a time per branch.
        await ExecAsync(connection, tx, "SELECT pg_advisory_xact_lock(hashtext('delivery-run:' || $1::text))", ct, branchId);
        int runNumber;
        await using (var next = new NpgsqlCommand(
            "SELECT COALESCE(MAX(run_number), 0) + 1 FROM delivery_runs WHERE branch_id = $1", connection, tx))
        {
            next.Parameters.AddWithValue(branchId);
            runNumber = (int)(await next.ExecuteScalarAsync(ct))!;
        }

        var runId = Guid.NewGuid();
        await ExecAsync(connection, tx,
            """
            INSERT INTO delivery_runs (organization_id, id, branch_id, run_number, run_date, driver_name, vehicle, notes, created_by_user_id)
            VALUES ($1, $2, $3, $4, $5, $6, $7, $8, $9)
            """, ct, scope.OrganizationId, runId, branchId, runNumber, runDate,
            Nullable(driverName), Nullable(vehicle), Nullable(notes), actorId);

        var placed = await PlaceOrdersAsync(connection, tx, scope, runId, orderIds, ct);
        if (placed is not null)
        {
            await tx.RollbackAsync(ct);
            return placed;
        }

        await AuditAsync(connection, tx, scope, actorId, "delivery-run", runId, "delivery-run.created",
            new { runNumber, runDate, driverName, vehicle, orders = orderIds }, ct);
        await tx.CommitAsync(ct);
        return FulfillmentResult.Ok(runId);
    }

    /// <summary>Changes a PLANNED run: its header and its stops (orders taken out are free again; new ones must be free).</summary>
    public async Task<FulfillmentResult> UpdateRunAsync(
        CloudTenantScope scope, Guid runId, DateOnly runDate, string? driverName, string? vehicle, string? notes,
        IReadOnlyList<Guid> orderIds, Guid actorId, CancellationToken ct)
    {
        await using var connection = await _dataSource.OpenConnectionAsync(ct);
        await using var tx = await connection.BeginTransactionAsync(ct);
        await TenantScopeSql.ApplyAsync(connection, tx, scope, ct);

        var status = await LockRunStatusAsync(connection, tx, runId, scope.BranchId!.Value, ct);
        if (status is null)
        {
            await tx.RollbackAsync(ct);
            return FulfillmentResult.NotFound();
        }

        if (status != "Planned")
        {
            await tx.RollbackAsync(ct);
            return FulfillmentResult.Conflict("run-not-planned");
        }

        await ExecAsync(connection, tx,
            "UPDATE delivery_runs SET run_date = $2, driver_name = $3, vehicle = $4, notes = $5 WHERE id = $1",
            ct, runId, runDate, Nullable(driverName), Nullable(vehicle), Nullable(notes));
        await ExecAsync(connection, tx, "DELETE FROM delivery_run_orders WHERE run_id = $1", ct, runId);

        var placed = await PlaceOrdersAsync(connection, tx, scope, runId, orderIds, ct);
        if (placed is not null)
        {
            await tx.RollbackAsync(ct);
            return placed;
        }

        await AuditAsync(connection, tx, scope, actorId, "delivery-run", runId, "delivery-run.updated",
            new { runDate, driverName, vehicle, orders = orderIds }, ct);
        await tx.CommitAsync(ct);
        return FulfillmentResult.Ok(runId);
    }

    /// <summary>
    /// Discards a run that was planned wrong: only while Planned (nothing dispatched, no remito numbered, nothing moved),
    /// its stops go with it and its orders are free again for another run. Audited with what the run held.
    /// </summary>
    public async Task<FulfillmentResult> DeleteRunAsync(CloudTenantScope scope, Guid runId, Guid actorId, CancellationToken ct)
    {
        await using var connection = await _dataSource.OpenConnectionAsync(ct);
        await using var tx = await connection.BeginTransactionAsync(ct);
        await TenantScopeSql.ApplyAsync(connection, tx, scope, ct);

        var status = await LockRunStatusAsync(connection, tx, runId, scope.BranchId!.Value, ct);
        if (status is null)
        {
            await tx.RollbackAsync(ct);
            return FulfillmentResult.NotFound();
        }

        if (status != "Planned")
        {
            await tx.RollbackAsync(ct);
            return FulfillmentResult.Conflict("run-not-planned");
        }

        var orders = new List<Guid>();
        int runNumber;
        DateOnly runDate;
        await using (var read = new NpgsqlCommand("SELECT run_number, run_date FROM delivery_runs WHERE id = $1", connection, tx))
        {
            read.Parameters.AddWithValue(runId);
            await using var reader = await read.ExecuteReaderAsync(ct);
            await reader.ReadAsync(ct);
            (runNumber, runDate) = (reader.GetInt32(0), reader.GetFieldValue<DateOnly>(1));
        }

        await using (var stops = new NpgsqlCommand("SELECT order_id FROM delivery_run_orders WHERE run_id = $1 ORDER BY stop_no", connection, tx))
        {
            stops.Parameters.AddWithValue(runId);
            await using var reader = await stops.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct))
            {
                orders.Add(reader.GetGuid(0));
            }
        }

        await ExecAsync(connection, tx, "DELETE FROM delivery_runs WHERE id = $1", ct, runId);
        await AuditAsync(connection, tx, scope, actorId, "delivery-run", runId, "delivery-run.deleted", new { runNumber, runDate, orders }, ct);
        await tx.CommitAsync(ct);
        return FulfillmentResult.Ok(runId);
    }

    /// <summary>
    /// Dispatches a planned run with at least one order: every order gets its remito number and goes OutForDelivery, and
    /// the run too. An order cancelled meanwhile blocks the dispatch (take it out first).
    /// </summary>
    public async Task<FulfillmentResult> DispatchRunAsync(CloudTenantScope scope, Guid runId, Guid actorId, CancellationToken ct)
    {
        await using var connection = await _dataSource.OpenConnectionAsync(ct);
        await using var tx = await connection.BeginTransactionAsync(ct);
        await TenantScopeSql.ApplyAsync(connection, tx, scope, ct);
        var branchId = scope.BranchId!.Value;

        var status = await LockRunStatusAsync(connection, tx, runId, branchId, ct);
        if (status is null)
        {
            await tx.RollbackAsync(ct);
            return FulfillmentResult.NotFound();
        }

        if (status != "Planned")
        {
            await tx.RollbackAsync(ct);
            return FulfillmentResult.Conflict("run-not-planned");
        }

        var orderIds = await RunOrderIdsAsync(connection, tx, runId, ct);
        if (orderIds.Count == 0)
        {
            await tx.RollbackAsync(ct);
            return FulfillmentResult.Conflict("run-empty");
        }

        foreach (var orderId in orderIds)
        {
            var orderStatus = await LockOrderStatusAsync(connection, tx, branchId, orderId, ct);
            if (orderStatus is not { } current || !OrderFulfillmentRules.CanJoinRun(current))
            {
                await tx.RollbackAsync(ct);
                return FulfillmentResult.Conflict("order-not-dispatchable");
            }
        }

        await AssignRemitoNumbersAsync(connection, tx, scope.OrganizationId, branchId, orderIds, ct);
        foreach (var orderId in orderIds)
        {
            await ExecAsync(connection, tx,
                "UPDATE orders SET fulfillment_status = 'OutForDelivery', fulfillment_updated_at = now() WHERE order_id = $1", ct, orderId);
        }

        await ExecAsync(connection, tx,
            "UPDATE delivery_runs SET status = 'OutForDelivery', dispatched_at = now() WHERE id = $1", ct, runId);
        await AuditAsync(connection, tx, scope, actorId, "delivery-run", runId, "delivery-run.dispatched", new { orders = orderIds }, ct);
        await tx.CommitAsync(ct);
        return FulfillmentResult.Ok(runId);
    }

    /// <summary>
    /// Settles a run out for delivery when the truck returns (see the class remarks). <paramref name="returns"/> must
    /// name every order of the run exactly once. <paramref name="today"/> dates the current account postings.
    /// </summary>
    public async Task<FulfillmentResult> SettleRunAsync(
        CloudTenantScope scope, Guid runId, IReadOnlyList<OrderReturnInput> returns, Guid actorId, DateOnly today, CancellationToken ct)
    {
        await using var connection = await _dataSource.OpenConnectionAsync(ct);
        await using var tx = await connection.BeginTransactionAsync(ct);
        await TenantScopeSql.ApplyAsync(connection, tx, scope, ct);
        var branchId = scope.BranchId!.Value;

        var status = await LockRunStatusAsync(connection, tx, runId, branchId, ct);
        if (status is null)
        {
            await tx.RollbackAsync(ct);
            return FulfillmentResult.NotFound();
        }

        if (status != "OutForDelivery")
        {
            await tx.RollbackAsync(ct);
            return FulfillmentResult.Conflict("run-not-out-for-delivery");
        }

        var orderIds = await RunOrderIdsAsync(connection, tx, runId, ct);
        if (returns.Count != orderIds.Count || returns.Select(r => r.OrderId).Distinct().Count() != returns.Count
            || !returns.All(r => orderIds.Contains(r.OrderId)))
        {
            await tx.RollbackAsync(ct);
            return FulfillmentResult.Invalid("returns-must-cover-every-order");
        }

        var settled = new List<object>();
        foreach (var input in returns)
        {
            var result = await SettleOrderAsync(connection, tx, scope, runId, input, actorId, today, ct);
            if (result.Outcome != FulfillmentOutcome.Done)
            {
                await tx.RollbackAsync(ct);
                return result;
            }

            settled.Add(new { orderId = input.OrderId, delivered = input.Delivered, settlement = input.Settlement });
        }

        await ExecAsync(connection, tx, "UPDATE delivery_runs SET status = 'Completed', completed_at = now() WHERE id = $1", ct, runId);
        await AuditAsync(connection, tx, scope, actorId, "delivery-run", runId, "delivery-run.settled", new { orders = settled }, ct);
        await tx.CommitAsync(ct);
        return FulfillmentResult.Ok(runId);
    }

    private async Task<FulfillmentResult> SettleOrderAsync(
        NpgsqlConnection connection, NpgsqlTransaction tx, CloudTenantScope scope, Guid runId, OrderReturnInput input,
        Guid actorId, DateOnly today, CancellationToken ct)
    {
        var branchId = scope.BranchId!.Value;
        var current = await LockOrderStatusAsync(connection, tx, branchId, input.OrderId, ct);
        if (current != OrderFulfillmentStatus.OutForDelivery)
        {
            return FulfillmentResult.Conflict("order-not-out-for-delivery");
        }

        var lines = await ReadLinesAsync(connection, tx, input.OrderId, ct);
        var given = (input.Lines ?? []).ToDictionary(line => line.LineNo, line => line.DeliveredQuantity);
        if (given.Keys.Any(lineNo => lines.All(line => line.LineNo != lineNo))
            || given.Values.Any(quantity => !OrderFulfillmentRules.IsValidDeliveredQuantity(quantity)))
        {
            return FulfillmentResult.Invalid("invalid-delivered-quantity");
        }

        var delivered = lines
            .Select(line => (Line: line, Quantity: !input.Delivered ? 0m : given.TryGetValue(line.LineNo, out var q) ? q : line.Quantity))
            .ToList();
        var outcome = OrderFulfillmentRules.AfterReturn([.. delivered.Select(d => (d.Line.Quantity, d.Quantity))]);

        if (outcome == OrderFulfillmentStatus.ReadyToDispatch)
        {
            // Nothing delivered: back to the shelf, out of this run, ready for another one.
            await ExecAsync(connection, tx, "DELETE FROM delivery_run_orders WHERE run_id = $1 AND order_id = $2", ct, runId, input.OrderId);
            await ExecAsync(connection, tx,
                "UPDATE orders SET fulfillment_status = 'ReadyToDispatch', fulfillment_updated_at = now() WHERE order_id = $1",
                ct, input.OrderId);
            return FulfillmentResult.Ok(input.OrderId);
        }

        var party = await ReadOrderPartyIdsAsync(connection, tx, input.OrderId, ct);
        var settlement = party.CustomerId is null
            ? OrderSettlement.PaidOnDelivery
            : Enum.TryParse<OrderSettlement>(input.Settlement, out var chosen) ? chosen : OrderSettlement.CurrentAccount;

        decimal total = 0m;
        foreach (var (line, quantity) in delivered)
        {
            await ExecAsync(connection, tx,
                """
                INSERT INTO order_line_deliveries (organization_id, order_id, line_no, delivered_quantity)
                VALUES ($1, $2, $3, $4) ON CONFLICT DO NOTHING
                """, ct, scope.OrganizationId, input.OrderId, line.LineNo, quantity);
            total += OrderFulfillmentRules.DeliveredAmount(line.UnitNetPrice, quantity);

            if (quantity > 0m && await PresentationInBranchAsync(connection, tx, branchId, line.PresentationId, ct))
            {
                await StockMovementWriter.InsertAsync(
                    connection, tx,
                    new NewStockMovement(
                        Guid.NewGuid(), scope.OrganizationId, branchId, line.PresentationId, -quantity, StockMovementKind.Sale,
                        OrderDeliverySource, input.OrderId, LineKey(input.OrderId, line.LineNo), CreatedByUserId: actorId),
                    ct, ignoreDuplicateSourceLine: true);
            }
        }

        var reference = party.RemitoNumber ?? party.OrderNumber;
        var concept = $"Remito {party.RemitoNumber ?? "-"} · Pedido {party.OrderNumber}";
        if (total > 0m && settlement == OrderSettlement.PaidOnDelivery)
        {
            // Collected by the driver: the money goes In the branch cash (treasury).
            await PostgresTreasuryStore.RecordInAsync(connection, tx, new NewTreasuryMovement(
                scope.OrganizationId, branchId, Commerce.Domain.Sales.SaleTender.Cash, "DeliveryPayment", total, DateTimeOffset.UtcNow, today,
                $"Cobrado en la entrega · {concept}", reference, party.CustomerId, OrderDeliverySource, input.OrderId, actorId), ct);
        }

        if (total > 0m && party.CustomerId is { } customerId)
        {
            // The customer's account: the delivery is charged; on account it is due after the payment terms, paid on
            // delivery it is credited right away (the history shows it, the balance does not change).
            var account = AccountParty.Customer(customerId);
            var terms = settlement == OrderSettlement.CurrentAccount
                ? await PostgresTreasuryStore.CustomerTermsAsync(connection, tx, customerId, ct)
                : null;
            var charge = await PostgresCurrentAccountStore.InsertAsync(
                connection, tx, scope.OrganizationId, account,
                new NewAccountMovement(
                    Guid.NewGuid(), AccountMovementKind.Invoice, PartyAccountRules.DebtDirection(AccountPartyKind.Customer), total, today,
                    terms?.DueOn(today) ?? today, reference,
                    terms is null ? concept : $"{concept} · vence a {terms.Days} días ({PosSaleAccountProjection.TermsSourceText(terms)})", actorId),
                null, ct, OrderDeliverySource, input.OrderId);
            if (charge is not null)
            {
                await PostgresCurrentAccountStore.AuditAsync(
                    connection, tx, scope.OrganizationId, "org-user", actorId, account, "customer.movement_registered", charge, ct);
            }

            if (settlement == OrderSettlement.PaidOnDelivery)
            {
                var paid = await PostgresCurrentAccountStore.InsertAsync(
                    connection, tx, scope.OrganizationId, account,
                    new NewAccountMovement(
                        Guid.NewGuid(), AccountMovementKind.Payment, CurrentAccountRules.Opposite(PartyAccountRules.DebtDirection(AccountPartyKind.Customer)),
                        total, today, null, reference, $"Cobrado en la entrega · {concept}", actorId),
                    null, ct, OrderDeliveryPaymentSource, input.OrderId);
                if (paid is not null)
                {
                    await PostgresCurrentAccountStore.AuditAsync(
                        connection, tx, scope.OrganizationId, "org-user", actorId, account, "customer.movement_registered", paid, ct);
                }
            }
        }

        await ExecAsync(connection, tx,
            """
            UPDATE orders SET fulfillment_status = $2, fulfillment_updated_at = now(), delivered_at = now(),
                              delivered_total = $3, settlement = $4
            WHERE order_id = $1
            """, ct, input.OrderId, outcome.ToString(), total, settlement.ToString());
        return FulfillmentResult.Ok(input.OrderId);
    }

    /// <summary>Puts <paramref name="orderIds"/> in the run as stops 1..n; null when every one could join, else why not.</summary>
    private static async Task<FulfillmentResult?> PlaceOrdersAsync(
        NpgsqlConnection connection, NpgsqlTransaction tx, CloudTenantScope scope, Guid runId, IReadOnlyList<Guid> orderIds, CancellationToken ct)
    {
        if (orderIds.Distinct().Count() != orderIds.Count)
        {
            return FulfillmentResult.Invalid("duplicate-order");
        }

        var stop = 0;
        foreach (var orderId in orderIds)
        {
            var status = await LockOrderStatusAsync(connection, tx, scope.BranchId!.Value, orderId, ct);
            if (status is null)
            {
                return FulfillmentResult.Invalid("order-not-found");
            }

            if (!OrderFulfillmentRules.CanJoinRun(status.Value))
            {
                return FulfillmentResult.Conflict("order-not-dispatchable");
            }

            await using var inOther = new NpgsqlCommand("SELECT 1 FROM delivery_run_orders WHERE order_id = $1", connection, tx);
            inOther.Parameters.AddWithValue(orderId);
            if (await inOther.ExecuteScalarAsync(ct) is not null)
            {
                return FulfillmentResult.Conflict("order-in-another-run");
            }

            await ExecAsync(connection, tx,
                "INSERT INTO delivery_run_orders (organization_id, run_id, order_id, stop_no) VALUES ($1, $2, $3, $4)",
                ct, scope.OrganizationId, runId, orderId, ++stop);
        }

        return null;
    }

    // ---------------------------------------------------------------- document profiles

    public async Task<OrganizationDocumentProfile?> GetOrganizationProfileAsync(CloudTenantScope scope, CancellationToken ct)
    {
        await using var connection = await _dataSource.OpenConnectionAsync(ct);
        await using var tx = await connection.BeginTransactionAsync(ct);
        await TenantScopeSql.ApplyAsync(connection, tx, scope, ct);
        var profile = await ReadOrganizationProfileAsync(connection, tx, scope.OrganizationId, ct);
        await tx.CommitAsync(ct);
        return profile;
    }

    /// <summary>Replaces the organization's document data (the name is not part of it) and audits the change.</summary>
    public async Task<bool> UpdateOrganizationProfileAsync(
        CloudTenantScope scope, OrganizationDocumentProfile profile, Guid actorId, CancellationToken ct)
    {
        await using var connection = await _dataSource.OpenConnectionAsync(ct);
        await using var tx = await connection.BeginTransactionAsync(ct);
        await TenantScopeSql.ApplyAsync(connection, tx, scope, ct);

        var rows = await ExecAsync(connection, tx,
            """
            UPDATE organizations SET legal_name = $2, tax_id = $3, tax_condition = $4, gross_income_number = $5,
                   activity_start_date = $6, fiscal_address = $7, document_footer = $8, logo_url = $9, primary_color = $10
            WHERE id = $1
            """, ct, scope.OrganizationId, Nullable(profile.LegalName), Nullable(profile.TaxId), Nullable(profile.TaxCondition),
            Nullable(profile.GrossIncomeNumber), (object?)profile.ActivityStartDate ?? DBNull.Value, Nullable(profile.FiscalAddress),
            Nullable(profile.DocumentFooter), Nullable(profile.LogoUrl), Nullable(profile.PrimaryColor));
        if (rows == 0)
        {
            await tx.RollbackAsync(ct);
            return false;
        }

        await AuditAsync(connection, tx, scope, actorId, "organization", scope.OrganizationId, "organization.document_profile_updated", profile, ct);
        await tx.CommitAsync(ct);
        return true;
    }

    public async Task<IReadOnlyList<BranchDocumentProfile>> ListBranchProfilesAsync(CloudTenantScope scope, CancellationToken ct)
    {
        await using var connection = await _dataSource.OpenConnectionAsync(ct);
        await using var tx = await connection.BeginTransactionAsync(ct);
        await TenantScopeSql.ApplyAsync(connection, tx, new CloudTenantScope(scope.OrganizationId), ct);

        var profiles = new List<BranchDocumentProfile>();
        await using (var cmd = new NpgsqlCommand(
            "SELECT id, name, code, address, locality, phone, email, warehouse_address FROM branches ORDER BY code, name", connection, tx))
        await using (var reader = await cmd.ExecuteReaderAsync(ct))
        {
            while (await reader.ReadAsync(ct))
            {
                profiles.Add(ReadBranchProfile(reader));
            }
        }

        await tx.CommitAsync(ct);
        return profiles;
    }

    /// <summary>Replaces a branch's document data (not its name or code) and audits the change; false when the branch is not the caller's organization's.</summary>
    public async Task<bool> UpdateBranchProfileAsync(
        CloudTenantScope scope, BranchDocumentProfile profile, Guid actorId, CancellationToken ct)
    {
        await using var connection = await _dataSource.OpenConnectionAsync(ct);
        await using var tx = await connection.BeginTransactionAsync(ct);
        await TenantScopeSql.ApplyAsync(connection, tx, new CloudTenantScope(scope.OrganizationId), ct);

        var rows = await ExecAsync(connection, tx,
            "UPDATE branches SET address = $2, locality = $3, phone = $4, email = $5, warehouse_address = $6 WHERE id = $1",
            ct, profile.BranchId, Nullable(profile.Address), Nullable(profile.Locality), Nullable(profile.Phone),
            Nullable(profile.Email), Nullable(profile.WarehouseAddress));
        if (rows == 0)
        {
            await tx.RollbackAsync(ct);
            return false;
        }

        await AuditAsync(connection, tx, scope, actorId, "branch", profile.BranchId, "branch.document_profile_updated", profile, ct);
        await tx.CommitAsync(ct);
        return true;
    }

    // ---------------------------------------------------------------- readers

    private static async Task<OrderTrackingSummary?> ReadSummaryAsync(
        NpgsqlConnection connection, NpgsqlTransaction tx, Guid branchId, Guid orderId, CancellationToken ct)
    {
        await using var cmd = new NpgsqlCommand(
            $"{SummarySelect} WHERE o.destination_branch_id = $1 AND o.order_id = $2", connection, tx);
        cmd.Parameters.AddWithValue(branchId);
        cmd.Parameters.AddWithValue(orderId);
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        return await reader.ReadAsync(ct) ? ReadSummary(reader) : null;
    }

    private static OrderTrackingSummary ReadSummary(NpgsqlDataReader reader)
    {
        var branchCode = new BranchCode(reader.GetInt16(1));
        return new OrderTrackingSummary(
            reader.GetGuid(0),
            new OrderNumber(branchCode, reader.GetInt32(2)).Format(),
            reader.GetFieldValue<DateTimeOffset>(3),
            reader.GetString(4),
            reader.IsDBNull(5) ? null : reader.GetGuid(5),
            reader.GetString(6),
            reader.GetString(7),
            reader.GetDecimal(8),
            (int)reader.GetInt64(9),
            reader.IsDBNull(10) ? null : reader.GetGuid(10),
            reader.IsDBNull(11) ? null : reader.GetInt32(11),
            reader.IsDBNull(12) ? null : DateOnly.FromDateTime(reader.GetDateTime(12)),
            reader.IsDBNull(13) ? null : new RemitoNumber(branchCode, reader.GetInt32(13)).Format(),
            reader.IsDBNull(14) ? null : reader.GetDecimal(14),
            reader.IsDBNull(15) ? null : reader.GetString(15),
            reader.IsDBNull(16) ? null : reader.GetFieldValue<DateTimeOffset>(16),
            reader.IsDBNull(17) ? null : reader.GetString(17),
            reader.IsDBNull(18) ? null : reader.GetString(18));
    }

    private static async Task<IReadOnlyList<OrderTrackingLine>> ReadLinesAsync(
        NpgsqlConnection connection, NpgsqlTransaction tx, Guid orderId, CancellationToken ct)
    {
        await using var cmd = new NpgsqlCommand(
            """
            SELECT l.line_no, l.presentation_id, l.product_name, l.presentation_name, l.quantity_behavior, l.quantity,
                   l.unit_net_price, l.line_total, d.delivered_quantity
            FROM order_lines l
            LEFT JOIN order_line_deliveries d
                   ON d.organization_id = l.organization_id AND d.order_id = l.order_id AND d.line_no = l.line_no
            WHERE l.order_id = $1
            ORDER BY l.line_no
            """, connection, tx);
        cmd.Parameters.AddWithValue(orderId);
        var lines = new List<OrderTrackingLine>();
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            lines.Add(new OrderTrackingLine(
                reader.GetInt32(0), reader.GetGuid(1), reader.GetString(2), reader.GetString(3), reader.GetString(4),
                reader.GetDecimal(5), reader.GetDecimal(6), reader.GetDecimal(7), reader.IsDBNull(8) ? null : reader.GetDecimal(8)));
        }

        return lines;
    }

    private static async Task<OrderPartyData> ReadPartyAsync(NpgsqlConnection connection, NpgsqlTransaction tx, Guid orderId, CancellationToken ct)
    {
        await using var cmd = new NpgsqlCommand(
            """
            SELECT COALESCE(c.display_name, o.guest_display_name, ''), c.legal_name, c.tax_id_type,
                   COALESCE(c.tax_id, o.guest_document_id), c.tax_condition, c.phone,
                   NULLIF(btrim(concat_ws(' ', c.address_street, c.address_number)), ''),
                   COALESCE(ci.name, c.locality), COALESCE(pr.name, c.province), COALESCE(c.postal_code, ci.postal_code),
                   COALESCE(c.delivery_notes, o.guest_delivery_notes), o.guest_contact_address, o.origin
            FROM orders o
            LEFT JOIN customers c ON c.organization_id = o.organization_id AND c.id = o.customer_id
            LEFT JOIN cities ci ON ci.id = c.city_id
            LEFT JOIN provinces pr ON pr.id = ci.province_id
            WHERE o.order_id = $1
            """, connection, tx);
        cmd.Parameters.AddWithValue(orderId);
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        await reader.ReadAsync(ct);

        string? Text(int ordinal) => reader.IsDBNull(ordinal) ? null : reader.GetString(ordinal);
        var isGuest = reader.GetString(12) == "Guest";
        return new OrderPartyData(
            reader.GetString(0),
            Text(1),
            isGuest ? null : Text(2),
            Text(3),
            Text(4),
            Text(5),
            isGuest ? null : Text(6),
            Text(7),
            Text(8),
            Text(9),
            Text(10));
    }

    private static async Task<(Guid? CustomerId, string OrderNumber, string? RemitoNumber)> ReadOrderPartyIdsAsync(
        NpgsqlConnection connection, NpgsqlTransaction tx, Guid orderId, CancellationToken ct)
    {
        await using var cmd = new NpgsqlCommand(
            "SELECT customer_id, branch_code, sequence, remito_sequence FROM orders WHERE order_id = $1", connection, tx);
        cmd.Parameters.AddWithValue(orderId);
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        await reader.ReadAsync(ct);
        var branch = new BranchCode(reader.GetInt16(1));
        return (
            reader.IsDBNull(0) ? null : reader.GetGuid(0),
            new OrderNumber(branch, reader.GetInt32(2)).Format(),
            reader.IsDBNull(3) ? null : new RemitoNumber(branch, reader.GetInt32(3)).Format());
    }

    private static async Task<DeliveryRunSummary?> ReadRunAsync(NpgsqlConnection connection, NpgsqlTransaction tx, Guid runId, CancellationToken ct)
    {
        await using var cmd = new NpgsqlCommand($"{RunSelect} WHERE r.id = $1", connection, tx);
        cmd.Parameters.AddWithValue(runId);
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        return await reader.ReadAsync(ct) ? ReadRun(reader) : null;
    }

    private static DeliveryRunSummary ReadRun(NpgsqlDataReader reader) => new(
        reader.GetGuid(0),
        reader.GetInt32(1),
        DateOnly.FromDateTime(reader.GetDateTime(2)),
        reader.IsDBNull(3) ? null : reader.GetString(3),
        reader.IsDBNull(4) ? null : reader.GetString(4),
        reader.IsDBNull(5) ? null : reader.GetString(5),
        reader.GetString(6),
        (int)reader.GetInt64(7),
        reader.GetDecimal(8),
        reader.GetFieldValue<DateTimeOffset>(9),
        reader.IsDBNull(10) ? null : reader.GetFieldValue<DateTimeOffset>(10),
        reader.IsDBNull(11) ? null : reader.GetFieldValue<DateTimeOffset>(11));

    private static async Task<OrganizationDocumentProfile?> ReadOrganizationProfileAsync(
        NpgsqlConnection connection, NpgsqlTransaction tx, Guid organizationId, CancellationToken ct)
    {
        await using var cmd = new NpgsqlCommand(
            """
            SELECT name, legal_name, tax_id, tax_condition, gross_income_number, activity_start_date, fiscal_address,
                   document_footer, logo_url, primary_color
            FROM organizations WHERE id = $1
            """, connection, tx);
        cmd.Parameters.AddWithValue(organizationId);
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        if (!await reader.ReadAsync(ct))
        {
            return null;
        }

        string? Text(int ordinal) => reader.IsDBNull(ordinal) ? null : reader.GetString(ordinal);
        return new OrganizationDocumentProfile(
            reader.GetString(0), Text(1), Text(2), Text(3), Text(4),
            reader.IsDBNull(5) ? null : DateOnly.FromDateTime(reader.GetDateTime(5)), Text(6), Text(7), Text(8), Text(9));
    }

    private static async Task<BranchDocumentProfile?> ReadBranchProfileAsync(
        NpgsqlConnection connection, NpgsqlTransaction tx, Guid branchId, CancellationToken ct)
    {
        await using var cmd = new NpgsqlCommand(
            "SELECT id, name, code, address, locality, phone, email, warehouse_address FROM branches WHERE id = $1", connection, tx);
        cmd.Parameters.AddWithValue(branchId);
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        return await reader.ReadAsync(ct) ? ReadBranchProfile(reader) : null;
    }

    private static BranchDocumentProfile ReadBranchProfile(NpgsqlDataReader reader)
    {
        string? Text(int ordinal) => reader.IsDBNull(ordinal) ? null : reader.GetString(ordinal);
        return new BranchDocumentProfile(
            reader.GetGuid(0), reader.GetString(1), reader.GetInt16(2), Text(3), Text(4), Text(5), Text(6), Text(7));
    }

    // ---------------------------------------------------------------- helpers

    private static async Task<OrderFulfillmentStatus?> LockOrderStatusAsync(
        NpgsqlConnection connection, NpgsqlTransaction tx, Guid branchId, Guid orderId, CancellationToken ct)
    {
        await using var cmd = new NpgsqlCommand(
            "SELECT fulfillment_status FROM orders WHERE order_id = $1 AND destination_branch_id = $2 FOR UPDATE", connection, tx);
        cmd.Parameters.AddWithValue(orderId);
        cmd.Parameters.AddWithValue(branchId);
        return await cmd.ExecuteScalarAsync(ct) is string status ? Enum.Parse<OrderFulfillmentStatus>(status) : null;
    }

    private static async Task<string?> LockRunStatusAsync(
        NpgsqlConnection connection, NpgsqlTransaction tx, Guid runId, Guid branchId, CancellationToken ct)
    {
        await using var cmd = new NpgsqlCommand(
            "SELECT status FROM delivery_runs WHERE id = $1 AND branch_id = $2 FOR UPDATE", connection, tx);
        cmd.Parameters.AddWithValue(runId);
        cmd.Parameters.AddWithValue(branchId);
        return await cmd.ExecuteScalarAsync(ct) as string;
    }

    private static async Task<bool> RunBelongsToBranchAsync(
        NpgsqlConnection connection, NpgsqlTransaction tx, Guid runId, Guid branchId, CancellationToken ct)
    {
        await using var cmd = new NpgsqlCommand("SELECT 1 FROM delivery_runs WHERE id = $1 AND branch_id = $2", connection, tx);
        cmd.Parameters.AddWithValue(runId);
        cmd.Parameters.AddWithValue(branchId);
        return await cmd.ExecuteScalarAsync(ct) is not null;
    }

    private static async Task<List<Guid>> RunOrderIdsAsync(NpgsqlConnection connection, NpgsqlTransaction tx, Guid runId, CancellationToken ct)
    {
        var ids = new List<Guid>();
        await using var cmd = new NpgsqlCommand(
            "SELECT order_id FROM delivery_run_orders WHERE run_id = $1 ORDER BY stop_no, order_id", connection, tx);
        cmd.Parameters.AddWithValue(runId);
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            ids.Add(reader.GetGuid(0));
        }

        return ids;
    }

    private static async Task<bool> PresentationInBranchAsync(
        NpgsqlConnection connection, NpgsqlTransaction tx, Guid branchId, Guid presentationId, CancellationToken ct)
    {
        await using var cmd = new NpgsqlCommand("SELECT 1 FROM presentations WHERE branch_id = $1 AND id = $2", connection, tx);
        cmd.Parameters.AddWithValue(branchId);
        cmd.Parameters.AddWithValue(presentationId);
        return await cmd.ExecuteScalarAsync(ct) is not null;
    }

    private static async Task<int> ExecAsync(
        NpgsqlConnection connection, NpgsqlTransaction tx, string sql, CancellationToken ct, params object[] parameters)
    {
        await using var cmd = new NpgsqlCommand(sql, connection, tx);
        foreach (var parameter in parameters)
        {
            cmd.Parameters.Add(parameter is DBNull ? new NpgsqlParameter { Value = DBNull.Value } : new NpgsqlParameter { Value = parameter });
        }

        return await cmd.ExecuteNonQueryAsync(ct);
    }

    private static Task AuditAsync(
        NpgsqlConnection connection, NpgsqlTransaction tx, CloudTenantScope scope, Guid actorId, string entityType, Guid entityId,
        string action, object detail, CancellationToken ct) =>
        AuditLogWriter.InsertAsync(connection, tx, new UserManagementAuditEntry(
            "org-user", actorId, scope.OrganizationId, entityType, entityId, action, null, JsonSerializer.Serialize(detail)), ct);

    private static object Nullable(string? value) => string.IsNullOrWhiteSpace(value) ? DBNull.Value : value.Trim();

    private static string EscapeLike(string value) =>
        value.Replace("\\", "\\\\", StringComparison.Ordinal).Replace("%", "\\%", StringComparison.Ordinal).Replace("_", "\\_", StringComparison.Ordinal);
}
