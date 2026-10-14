using System.Text.Json;
using Commerce.Application.Pricing;
using Commerce.Cloud.Api.Auditing;
using Commerce.Cloud.Api.Pricing;
using Commerce.Cloud.Api.Tenancy;
using Commerce.Domain.CurrentAccounts;
using Commerce.Domain.Payroll;
using Commerce.Domain.Stock;
using Commerce.Domain.Sync.Payloads;
using Microsoft.Extensions.Logging;
using Npgsql;

namespace Commerce.Cloud.Api.Persistence;

/// <summary>One line of goods to register: what and how much (the price comes from the default list).</summary>
public sealed record GoodsLineInput(Guid PresentationId, decimal Quantity);

public sealed record GoodsQuoteLine(
    Guid PresentationId, string ProductName, string PresentationName, decimal Quantity, decimal UnitPrice, decimal LineTotal);

public sealed record GoodsQuote(IReadOnlyList<GoodsQuoteLine> Lines, decimal Total);

public sealed record GoodsRegistration(Guid GoodsId, decimal Total, IReadOnlyList<GoodsQuoteLine> Lines);

/// <summary>One item an employee took; <see cref="Status"/> as overtime's (<see cref="OvertimeStatus"/>).</summary>
public sealed record GoodsItemRecord(
    Guid Id,
    string SourceType,
    Guid SourceId,
    int LineNo,
    DateOnly OccurredOn,
    Guid? PresentationId,
    string ProductName,
    string PresentationName,
    decimal Quantity,
    decimal UnitPrice,
    decimal LineTotal,
    decimal DiscountPercent,
    string Status,
    int? RunNumber,
    // owner feedback round 2, R2: its product and how it is sold, to fill the form again ("Corregir"); null when the
    // presentation is not the selected branch's (the catalog is branch-owned).
    Guid? ProductId = null,
    string? QuantityBehavior = null);

public enum EmployeeGoodsOutcome
{
    Done,
    NotFound,
    /// <summary>The employee has no linked customer ("lleva mercadería" off): there is no account to charge.</summary>
    NoGoodsAccount,
    /// <summary>A presentation has no effective price on the default list (or no default list exists).</summary>
    NoPrice,
    /// <summary>The idempotency key already names another employee's registration.</summary>
    KeyInUse,
    /// <summary>owner feedback round 2, R1: a paid payroll already settled the goods; they can no longer be voided.</summary>
    Settled,
}

/// <summary>
/// employee-lifecycle E4: the goods an employee takes (`employee_goods_items`, 0056). The web registers them like a sale
/// (<see cref="RegisterAsync"/>): priced on the organization's DEFAULT list (Mostrador), never the linked customer's own
/// list; stock out of the selected branch; a debt (Invoice) on the employee's linked customer account, which the payroll
/// deducts; and its items, pending for the payroll's per-item discount. A POS sale on current account to that customer
/// copies its lines here (<see cref="ProjectPosSaleAsync"/>; the sale projection already moved its stock and debt).
/// </summary>
public sealed class PostgresEmployeeGoodsStore
{
    public const string WebSource = "Web";
    public const string PosSaleSource = "PosSale";

    /// <summary>The source of the stock movements and the account charge of goods registered in the web.</summary>
    public const string StockSourceType = "EmployeeGoods";

    private readonly NpgsqlDataSource _dataSource;
    private readonly PostgresPriceListStore _priceLists;
    private readonly PostgresRateComponentStore _rateComponents;

    public PostgresEmployeeGoodsStore(NpgsqlDataSource dataSource, PostgresPriceListStore priceLists, PostgresRateComponentStore rateComponents)
    {
        _dataSource = dataSource;
        _priceLists = priceLists;
        _rateComponents = rateComponents;
    }

    /// <summary>
    /// Prices the lines on the default list for <paramref name="effectiveOn"/>; nothing is written. The catalog and the
    /// prices are the selected branch's (<paramref name="scope"/> carries it).
    /// </summary>
    public async Task<(EmployeeGoodsOutcome Outcome, GoodsQuote? Quote)> QuoteAsync(
        CloudTenantScope scope, Guid employeeId, IReadOnlyList<GoodsLineInput> lines, CancellationToken ct, DateOnly? effectiveOn = null)
    {
        if (await EmployeeCustomerAsync(scope, employeeId, ct) is not { } found)
        {
            return (EmployeeGoodsOutcome.NotFound, null);
        }

        if (found.CustomerId is null)
        {
            return (EmployeeGoodsOutcome.NoGoodsAccount, null);
        }

        var quote = await PriceAsync(scope, lines, effectiveOn ?? DateOnly.FromDateTime(DateTime.UtcNow), ct);
        return quote is null ? (EmployeeGoodsOutcome.NoPrice, null) : (EmployeeGoodsOutcome.Done, quote);
    }

    /// <summary>
    /// The goods taken on <paramref name="occurredOn"/>, like a sale in the selected branch: priced on the default list,
    /// stock out, the debt on the employee's linked customer account, its items pending. All or nothing.
    /// <paramref name="goodsId"/> is the caller's idempotency key: a retry with the same key (an answer lost on the way
    /// back) gets the first registration back and moves nothing again.
    /// </summary>
    public async Task<(EmployeeGoodsOutcome Outcome, GoodsRegistration? Registration)> RegisterAsync(
        CloudTenantScope scope, Guid actorId, Guid employeeId, DateOnly occurredOn, IReadOnlyList<GoodsLineInput> lines, CancellationToken ct,
        Guid? goodsId = null)
    {
        var id = goodsId ?? Guid.NewGuid();
        if (goodsId is not null)
        {
            switch (await RegisteredAsync(scope, employeeId, id, ct))
            {
                case (true, _):
                    return (EmployeeGoodsOutcome.KeyInUse, null);
                case (false, { } earlier):
                    return (EmployeeGoodsOutcome.Done, earlier);
            }
        }

        var (outcome, quote) = await QuoteAsync(scope, employeeId, lines, ct, occurredOn);
        if (outcome != EmployeeGoodsOutcome.Done)
        {
            return (outcome, null);
        }

        var employee = (await EmployeeCustomerAsync(scope, employeeId, ct))!.Value;
        var customerId = employee.CustomerId!.Value;
        var branchId = scope.BranchId!.Value;

        await using var connection = await _dataSource.OpenConnectionAsync(ct);
        await using var tx = await connection.BeginTransactionAsync(ct);
        await TenantScopeSql.ApplyAsync(connection, tx, scope, ct);

        // Two retries racing with the same key: one writes, the other finds it written.
        await using (var lockCmd = new NpgsqlCommand("SELECT pg_advisory_xact_lock(hashtext('employee-goods:' || $1::text))", connection, tx))
        {
            lockCmd.Parameters.AddWithValue(id);
            await lockCmd.ExecuteNonQueryAsync(ct);
        }

        if (await ReadRegistrationAsync(connection, tx, employeeId, id, ct) is var (otherEmployee, raced) && (otherEmployee || raced is not null))
        {
            await tx.RollbackAsync(ct);
            return otherEmployee ? (EmployeeGoodsOutcome.KeyInUse, null) : (EmployeeGoodsOutcome.Done, raced);
        }

        var goodsKey = id;

        var lineNo = 0;
        foreach (var line in quote!.Lines)
        {
            lineNo++;
            await InsertItemAsync(connection, tx, scope.OrganizationId, employeeId, customerId, branchId, WebSource, goodsKey, lineNo, occurredOn,
                line.PresentationId, line.ProductName, line.PresentationName, line.Quantity, line.UnitPrice, line.LineTotal, actorId, ct);
            if (await PresentationInBranchAsync(connection, tx, branchId, line.PresentationId, ct))
            {
                await StockMovementWriter.InsertAsync(connection, tx, new NewStockMovement(
                    Guid.NewGuid(), scope.OrganizationId, branchId, line.PresentationId, -line.Quantity, StockMovementKind.Sale,
                    StockSourceType, goodsKey, Guid.NewGuid(), CreatedByUserId: actorId), ct);
            }
        }

        var party = AccountParty.Customer(customerId);
        var concept = Concept(quote.Lines);
        var charge = await PostgresCurrentAccountStore.InsertAsync(connection, tx, scope.OrganizationId, party,
            new NewAccountMovement(Guid.NewGuid(), AccountMovementKind.Invoice, PartyAccountRules.DebtDirection(AccountPartyKind.Customer),
                quote.Total, occurredOn, occurredOn, null, concept, actorId),
            null, ct, StockSourceType, goodsKey);
        if (charge is not null)
        {
            await PostgresCurrentAccountStore.AuditAsync(connection, tx, scope.OrganizationId, "org-user", actorId, party, "customer.movement_registered", charge, ct);
        }

        await AuditLogWriter.InsertAsync(connection, tx, new UserManagementAuditEntry(
            "org-user", actorId, scope.OrganizationId, "employee", employeeId, "employee.goods_registered", null,
            JsonSerializer.Serialize(new { goodsId = goodsKey, occurredOn, branchId, total = quote.Total, lines = quote.Lines })), ct);
        await tx.CommitAsync(ct);
        return (EmployeeGoodsOutcome.Done, new GoodsRegistration(goodsKey, quote.Total, quote.Lines));
    }

    private async Task<(bool OtherEmployee, GoodsRegistration? Registration)> RegisteredAsync(
        CloudTenantScope scope, Guid employeeId, Guid goodsId, CancellationToken ct)
    {
        await using var connection = await _dataSource.OpenConnectionAsync(ct);
        await using var tx = await connection.BeginTransactionAsync(ct);
        await TenantScopeSql.ApplyAsync(connection, tx, scope.OrganizationId, null, ct);
        var registration = await ReadRegistrationAsync(connection, tx, employeeId, goodsId, ct);
        await tx.CommitAsync(ct);
        return registration;
    }

    /// <summary>
    /// A web registration already written under <paramref name="goodsId"/>, rebuilt from its items (null when none), or
    /// <c>OtherEmployee</c> when the key names another employee's registration.
    /// </summary>
    private static async Task<(bool OtherEmployee, GoodsRegistration? Registration)> ReadRegistrationAsync(
        NpgsqlConnection connection, NpgsqlTransaction tx, Guid employeeId, Guid goodsId, CancellationToken ct)
    {
        await using (var owner = new NpgsqlCommand(
            "SELECT employee_id FROM employee_goods_items WHERE source_type = 'Web' AND source_id = $1 LIMIT 1", connection, tx))
        {
            owner.Parameters.AddWithValue(goodsId);
            switch (await owner.ExecuteScalarAsync(ct))
            {
                case null:
                    return (false, null);
                case Guid other when other != employeeId:
                    return (true, null);
            }
        }

        var items = await ReadItemsAsync(connection, tx, "WHERE g.source_type = 'Web' AND g.source_id = $1 ORDER BY g.line_no", goodsId, ct);

        var lines = items.Select(item => new GoodsQuoteLine(
            item.PresentationId ?? Guid.Empty, item.ProductName, item.PresentationName, item.Quantity, item.UnitPrice, item.LineTotal)).ToList();
        return (false, new GoodsRegistration(goodsId, lines.Sum(line => line.LineTotal), lines));
    }

    /// <summary>
    /// owner feedback round 2, R1: voids a registration made in the web, whole and in one transaction: its charge on the
    /// employee's customer account is reversed, its stock comes back in the branch it left (whatever branch is selected
    /// now) and its items stop being something to deduct (<see cref="ReleaseAsync"/>). A retry finds it voided and moves
    /// nothing again. <see cref="EmployeeGoodsOutcome.Settled"/> (nothing written) once a paid payroll took it.
    /// </summary>
    public async Task<EmployeeGoodsOutcome> VoidAsync(
        CloudTenantScope scope, Guid actorId, Guid employeeId, Guid goodsId, DateOnly today, CancellationToken ct)
    {
        await using var connection = await _dataSource.OpenConnectionAsync(ct);
        await using var tx = await connection.BeginTransactionAsync(ct);
        await TenantScopeSql.ApplyAsync(connection, tx, scope.OrganizationId, null, ct);
        await using (var lockCmd = new NpgsqlCommand("SELECT pg_advisory_xact_lock(hashtext('employee-goods:' || $1::text))", connection, tx))
        {
            lockCmd.Parameters.AddWithValue(goodsId);
            await lockCmd.ExecuteNonQueryAsync(ct);
        }

        // The charge, on the customer account the employee has now (the one the goods were charged to).
        Guid? charge = null;
        var reversed = false;
        await using (var find = new NpgsqlCommand(
            """
            SELECT m.id, EXISTS (SELECT 1 FROM current_account_movements r WHERE r.reverses_movement_id = m.id)
            FROM current_account_movements m
            JOIN employees e ON e.organization_id = m.organization_id AND e.customer_id = m.customer_id
            WHERE e.id = $1 AND m.source_type = $2 AND m.source_id = $3
            """, connection, tx))
        {
            find.Parameters.AddWithValue(employeeId);
            find.Parameters.AddWithValue(StockSourceType);
            find.Parameters.AddWithValue(goodsId);
            await using var reader = await find.ExecuteReaderAsync(ct);
            if (await reader.ReadAsync(ct))
            {
                (charge, reversed) = (reader.GetGuid(0), reader.GetBoolean(1));
            }
        }

        if (charge is null)
        {
            await tx.RollbackAsync(ct);
            return EmployeeGoodsOutcome.NotFound;
        }

        // Already reversed: a retry, or a charge reversed before reversals released goods (its items still pending, its
        // stock out). Releasing is a no-op once done, so both end voided.
        if (reversed)
        {
            if (!await ReleaseAsync(connection, tx, scope.OrganizationId, StockSourceType, goodsId, actorId, ct))
            {
                await tx.RollbackAsync(ct);
                return EmployeeGoodsOutcome.Settled;
            }

            await tx.CommitAsync(ct);
            return EmployeeGoodsOutcome.Done;
        }

        var customerId = await LinkedCustomerAsync(connection, tx, employeeId, ct);
        var party = AccountParty.Customer(customerId);
        var original = (await PostgresCurrentAccountStore.FindBySourceAsync(connection, tx, party, StockSourceType, goodsId, ct))!;
        if (!await ReleaseAsync(connection, tx, scope.OrganizationId, StockSourceType, goodsId, actorId, ct))
        {
            await tx.RollbackAsync(ct);
            return EmployeeGoodsOutcome.Settled;
        }

        var result = await PostgresCurrentAccountStore.ReverseWithinAsync(
            connection, tx, scope.OrganizationId, party, original.Id, $"Anulación: {original.Concept}", null, today, actorId, ct);
        if (result.Outcome != ReverseMovementOutcome.Reversed)
        {
            await tx.RollbackAsync(ct);
            return result.Outcome == ReverseMovementOutcome.AlreadyReversed ? EmployeeGoodsOutcome.Done : EmployeeGoodsOutcome.NotFound;
        }

        await PostgresCurrentAccountStore.AuditAsync(connection, tx, scope.OrganizationId, "org-user", actorId, party, "customer.movement_reversed", result.Reversal!, ct);
        await tx.CommitAsync(ct);
        return EmployeeGoodsOutcome.Done;
    }

    /// <summary>
    /// What a reversed charge takes back from the goods, inside the caller's transaction: goods registered in the web
    /// (charge source <see cref="StockSourceType"/>) get their stock back in their own branch; a POS sale's
    /// (<see cref="PosSaleAccountProjection.SourceType"/>) keeps its stock, as any sale whose charge is reversed. Either way
    /// its items are deleted (a draft payslip's goods amount lowered by them) and the void is audited on the employee.
    /// False, with nothing written, when a paid payroll already took one of them; true when there is nothing to release.
    /// </summary>
    internal static async Task<bool> ReleaseAsync(
        NpgsqlConnection connection, NpgsqlTransaction tx, Guid organizationId, string chargeSourceType, Guid sourceId, Guid actorId,
        CancellationToken ct)
    {
        var itemSource = chargeSourceType switch
        {
            StockSourceType => WebSource,
            PosSaleAccountProjection.SourceType => PosSaleSource,
            _ => null,
        };
        if (itemSource is null)
        {
            return true;
        }

        Guid? employeeId = null;
        Guid? branchId = null;
        var total = 0m;
        var settled = false;
        await using (var read = new NpgsqlCommand(
            """
            SELECT g.employee_id, g.branch_id, g.line_total, COALESCE(run.status = 'Paid', false)
            FROM employee_goods_items g
            LEFT JOIN payslips p ON p.id = g.payslip_id
            LEFT JOIN payroll_runs run ON run.organization_id = p.organization_id AND run.id = p.run_id
            WHERE g.source_type = $1 AND g.source_id = $2
            FOR UPDATE OF g
            """, connection, tx))
        {
            read.Parameters.AddWithValue(itemSource);
            read.Parameters.AddWithValue(sourceId);
            await using var reader = await read.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct))
            {
                employeeId = reader.GetGuid(0);
                branchId ??= reader.IsDBNull(1) ? null : reader.GetGuid(1);
                total += reader.GetDecimal(2);
                settled |= reader.GetBoolean(3);
            }
        }

        if (settled)
        {
            return false;
        }

        if (employeeId is null)
        {
            return true;
        }

        // A draft payslip deducts them no more.
        await using (var lower = new NpgsqlCommand(
            """
            UPDATE payslips p SET purchases_amount = GREATEST(p.purchases_amount - s.total, 0)
            FROM (SELECT payslip_id, sum(line_total) AS total FROM employee_goods_items
                  WHERE source_type = $1 AND source_id = $2 AND payslip_id IS NOT NULL GROUP BY payslip_id) s
            WHERE p.id = s.payslip_id
            """, connection, tx))
        {
            lower.Parameters.AddWithValue(itemSource);
            lower.Parameters.AddWithValue(sourceId);
            await lower.ExecuteNonQueryAsync(ct);
        }

        await using (var delete = new NpgsqlCommand("DELETE FROM employee_goods_items WHERE source_type = $1 AND source_id = $2", connection, tx))
        {
            delete.Parameters.AddWithValue(itemSource);
            delete.Parameters.AddWithValue(sourceId);
            await delete.ExecuteNonQueryAsync(ct);
        }

        if (itemSource == WebSource && branchId is { } branch)
        {
            // The stock is branch-scoped: read and write it in the goods' own branch.
            await TenantScopeSql.ApplyAsync(connection, tx, organizationId, branch, ct);
            var originals = new List<(Guid Id, Guid PresentationId, decimal Quantity)>();
            await using (var find = new NpgsqlCommand(
                "SELECT id, presentation_id, quantity FROM stock_movements WHERE source_type = $1 AND source_id = $2 AND kind = 'Sale'", connection, tx))
            {
                find.Parameters.AddWithValue(StockSourceType);
                find.Parameters.AddWithValue(sourceId);
                await using var reader = await find.ExecuteReaderAsync(ct);
                while (await reader.ReadAsync(ct))
                {
                    originals.Add((reader.GetGuid(0), reader.GetGuid(1), reader.GetDecimal(2)));
                }
            }

            foreach (var original in originals)
            {
                await StockMovementWriter.InsertAsync(connection, tx, new NewStockMovement(
                    Guid.NewGuid(), organizationId, branch, original.PresentationId, -original.Quantity, StockMovementKind.Reversal,
                    StockSourceType, sourceId, ReversesMovementId: original.Id, Reason: "Anulación de mercadería del personal",
                    CreatedByUserId: actorId), ct);
            }
        }

        await AuditLogWriter.InsertAsync(connection, tx, new UserManagementAuditEntry(
            "org-user", actorId, organizationId, "employee", employeeId.Value, "employee.goods_voided", null,
            JsonSerializer.Serialize(new { source = itemSource, sourceId, total })), ct);
        return true;
    }

    private static async Task<Guid> LinkedCustomerAsync(NpgsqlConnection connection, NpgsqlTransaction tx, Guid employeeId, CancellationToken ct)
    {
        await using var cmd = new NpgsqlCommand("SELECT customer_id FROM employees WHERE id = $1", connection, tx);
        cmd.Parameters.AddWithValue(employeeId);
        return (Guid)(await cmd.ExecuteScalarAsync(ct))!;
    }

    /// <summary>The employee's goods items, newest day first, with where each stands.</summary>
    public async Task<IReadOnlyList<GoodsItemRecord>> ListAsync(CloudTenantScope scope, Guid employeeId, CancellationToken ct)
    {
        await using var connection = await _dataSource.OpenConnectionAsync(ct);
        await using var tx = await connection.BeginTransactionAsync(ct);
        await TenantScopeSql.ApplyAsync(connection, tx, scope, ct);
        var items = await ReadItemsAsync(connection, tx, "WHERE g.employee_id = $1 ORDER BY g.occurred_on DESC, g.created_at_utc DESC, g.line_no", employeeId, ct);
        await tx.CommitAsync(ct);
        return items;
    }

    internal static async Task<List<GoodsItemRecord>> ReadItemsAsync(
        NpgsqlConnection connection, NpgsqlTransaction tx, string where, Guid parameter, CancellationToken ct)
    {
        await using var cmd = new NpgsqlCommand(
            $"""
            SELECT g.id, g.source_type, g.source_id, g.line_no, g.occurred_on, g.presentation_id, g.product_name, g.presentation_name,
                   g.quantity, g.unit_price, g.line_total, g.discount_percent,
                   CASE WHEN g.payslip_id IS NULL THEN 'Pending' WHEN run.status = 'Paid' THEN 'Paid' ELSE 'InPayroll' END, run.run_number,
                   pres.product_id, pres.quantity_behavior
            FROM employee_goods_items g
            LEFT JOIN presentations pres ON pres.id = g.presentation_id
            LEFT JOIN payslips p ON p.id = g.payslip_id
            LEFT JOIN payroll_runs run ON run.organization_id = p.organization_id AND run.id = p.run_id
            {where}
            """, connection, tx);
        cmd.Parameters.AddWithValue(parameter);
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        var items = new List<GoodsItemRecord>();
        while (await reader.ReadAsync(ct))
        {
            items.Add(new GoodsItemRecord(
                reader.GetGuid(0), reader.GetString(1), reader.GetGuid(2), reader.GetInt32(3), reader.GetFieldValue<DateOnly>(4),
                reader.IsDBNull(5) ? null : reader.GetGuid(5), reader.GetString(6), reader.GetString(7), reader.GetDecimal(8),
                reader.GetDecimal(9), reader.GetDecimal(10), reader.GetDecimal(11), reader.GetString(12), reader.IsDBNull(13) ? null : reader.GetInt32(13),
                reader.IsDBNull(14) ? null : reader.GetGuid(14), reader.IsDBNull(15) ? null : reader.GetString(15)));
        }

        return items;
    }

    /// <summary>
    /// branch -> cloud: a POS sale on current account to an employee's linked customer leaves its items (the sale projection
    /// already wrote the stock and the debt): the lines taken (positive quantity; a returned line only lowers the charge),
    /// with what was charged spread over them so they add up to it. Keyed by the sale and the line: a redelivery adds
    /// nothing. Runs in the inbox transaction under its OWN savepoint: the goods never hold the sale's money back.
    /// </summary>
    internal static async Task ProjectPosSaleAsync(
        NpgsqlConnection connection, NpgsqlTransaction tx, Guid organizationId, Guid branchId, SalePayloadV1 payload, DateOnly occurredOn,
        Guid actorId, ILogger? logger, CancellationToken ct)
    {
        var taken = payload.Lines.Where(line => line.Quantity > 0m && line.LineTotal > 0m).ToList();
        if (payload.CustomerId is not { } customerId || taken.Count == 0 || payload.TotalAmount <= 0m)
        {
            return;
        }

        const string savepoint = "pos_sale_employee_goods";
        await tx.SaveAsync(savepoint, ct);
        try
        {
            await using var find = new NpgsqlCommand("SELECT id FROM employees WHERE customer_id = $1", connection, tx);
            find.Parameters.AddWithValue(customerId);
            if (await find.ExecuteScalarAsync(ct) is Guid employeeId)
            {
                var charged = EmployeeGoodsRules.Spread([.. taken.Select(line => line.LineTotal)], payload.TotalAmount);
                for (var i = 0; i < taken.Count; i++)
                {
                    var line = taken[i];
                    await InsertItemAsync(connection, tx, organizationId, employeeId, customerId, branchId, PosSaleSource, payload.SaleId,
                        line.LineNumber, occurredOn, line.PresentationId, line.ProductName, line.PresentationName, line.Quantity, line.UnitPrice,
                        charged[i], actorId, ct);
                }
            }

            await tx.ReleaseAsync(savepoint, ct);
        }
        catch (Exception ex) when (!ct.IsCancellationRequested && ex is not OutOfMemoryException)
        {
            await tx.RollbackAsync(savepoint, ct);
            logger?.LogWarning(ex, "Sale {SaleId} was charged but its employee goods items were not copied ({Message})", payload.SaleId, ex.Message);
        }
    }

    /// <summary>A voided POS sale takes its items back, unless a paid payroll already settled them.</summary>
    internal static async Task RemovePosSaleAsync(NpgsqlConnection connection, NpgsqlTransaction tx, Guid saleId, CancellationToken ct)
    {
        await using var delete = new NpgsqlCommand(
            """
            DELETE FROM employee_goods_items g
            WHERE g.source_type = 'PosSale' AND g.source_id = $1
              AND NOT EXISTS (SELECT 1 FROM payslips p JOIN payroll_runs r ON r.organization_id = p.organization_id AND r.id = p.run_id
                              WHERE p.id = g.payslip_id AND r.status = 'Paid')
            """, connection, tx);
        delete.Parameters.AddWithValue(saleId);
        await delete.ExecuteNonQueryAsync(ct);
    }

    private static async Task InsertItemAsync(
        NpgsqlConnection connection, NpgsqlTransaction tx, Guid organizationId, Guid employeeId, Guid customerId, Guid? branchId, string sourceType,
        Guid sourceId, int lineNo, DateOnly occurredOn, Guid? presentationId, string productName, string presentationName, decimal quantity,
        decimal unitPrice, decimal lineTotal, Guid? actorId, CancellationToken ct)
    {
        await using var insert = new NpgsqlCommand(
            """
            INSERT INTO employee_goods_items (organization_id, id, employee_id, customer_id, branch_id, source_type, source_id, line_no, occurred_on,
                                              presentation_id, product_name, presentation_name, quantity, unit_price, line_total, created_by_user_id)
            VALUES ($1, $2, $3, $4, $5, $6, $7, $8, $9, $10, $11, $12, $13, $14, $15, $16)
            ON CONFLICT (organization_id, source_type, source_id, line_no) DO NOTHING
            """, connection, tx);
        insert.Parameters.AddWithValue(organizationId);
        insert.Parameters.AddWithValue(Guid.NewGuid());
        insert.Parameters.AddWithValue(employeeId);
        insert.Parameters.AddWithValue(customerId);
        insert.Parameters.AddWithValue(NpgsqlTypes.NpgsqlDbType.Uuid, (object?)branchId ?? DBNull.Value);
        insert.Parameters.AddWithValue(sourceType);
        insert.Parameters.AddWithValue(sourceId);
        insert.Parameters.AddWithValue(lineNo);
        insert.Parameters.AddWithValue(occurredOn);
        insert.Parameters.AddWithValue(NpgsqlTypes.NpgsqlDbType.Uuid, (object?)presentationId ?? DBNull.Value);
        insert.Parameters.AddWithValue(productName);
        insert.Parameters.AddWithValue(presentationName);
        insert.Parameters.AddWithValue(quantity);
        insert.Parameters.AddWithValue(Math.Max(unitPrice, 0m));
        insert.Parameters.AddWithValue(Math.Max(lineTotal, 0m));
        insert.Parameters.AddWithValue(NpgsqlTypes.NpgsqlDbType.Uuid, (object?)actorId ?? DBNull.Value);
        await insert.ExecuteNonQueryAsync(ct);
    }

    private async Task<(Guid EmployeeId, Guid? CustomerId)?> EmployeeCustomerAsync(CloudTenantScope scope, Guid employeeId, CancellationToken ct)
    {
        await using var connection = await _dataSource.OpenConnectionAsync(ct);
        await using var tx = await connection.BeginTransactionAsync(ct);
        await TenantScopeSql.ApplyAsync(connection, tx, scope.OrganizationId, null, ct);
        await using var cmd = new NpgsqlCommand("SELECT customer_id FROM employees WHERE id = $1", connection, tx);
        cmd.Parameters.AddWithValue(employeeId);
        var value = await cmd.ExecuteScalarAsync(ct);
        await tx.CommitAsync(ct);
        return value switch
        {
            null => null,
            DBNull => (employeeId, null),
            Guid customer => (employeeId, customer),
            _ => null,
        };
    }

    /// <summary>The lines priced on the default list (Mostrador), never the buyer's own list; null when one has no price.</summary>
    private async Task<GoodsQuote?> PriceAsync(CloudTenantScope scope, IReadOnlyList<GoodsLineInput> lines, DateOnly effectiveOn, CancellationToken ct)
    {
        if (await _priceLists.FindDefaultPriceListAsync(scope, ct) is not { } list)
        {
            return null;
        }

        var pricing = new PricingResolutionService(new PriceListPorts(
            list.Id, new PostgresEffectivePriceSource(_priceLists, scope, list.Id), new PostgresRateComponentSource(_rateComponents, scope, list.Id)));
        var priced = new List<GoodsQuoteLine>(lines.Count);
        foreach (var line in lines)
        {
            if (await pricing.ResolveAsync(line.PresentationId, line.Quantity, null, effectiveOn, ct) is not PriceResolutionOutcome.Resolved resolved
                || await NamesAsync(scope, line.PresentationId, ct) is not { } names)
            {
                return null;
            }

            priced.Add(new GoodsQuoteLine(line.PresentationId, names.Product, names.Presentation, line.Quantity, resolved.UnitNetPrice, resolved.LineTotal));
        }

        return new GoodsQuote(priced, priced.Sum(line => line.LineTotal));
    }

    private async Task<(string Product, string Presentation)?> NamesAsync(CloudTenantScope scope, Guid presentationId, CancellationToken ct)
    {
        await using var connection = await _dataSource.OpenConnectionAsync(ct);
        await using var tx = await connection.BeginTransactionAsync(ct);
        // The catalog is the branch's (row level security by branch): the selected branch's scope.
        await TenantScopeSql.ApplyAsync(connection, tx, scope, ct);
        await using var cmd = new NpgsqlCommand(
            "SELECT p.name, pr.name FROM presentations pr JOIN products p ON p.id = pr.product_id WHERE pr.id = $1", connection, tx);
        cmd.Parameters.AddWithValue(presentationId);
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        return await reader.ReadAsync(ct) ? (reader.GetString(0), reader.GetString(1)) : null;
    }

    private static async Task<bool> PresentationInBranchAsync(
        NpgsqlConnection connection, NpgsqlTransaction tx, Guid branchId, Guid presentationId, CancellationToken ct)
    {
        await using var cmd = new NpgsqlCommand("SELECT 1 FROM presentations WHERE branch_id = $1 AND id = $2", connection, tx);
        cmd.Parameters.AddWithValue(branchId);
        cmd.Parameters.AddWithValue(presentationId);
        return await cmd.ExecuteScalarAsync(ct) is not null;
    }

    /// <summary>"Mercadería: Asado, Chorizo" (at most 200 characters), the account line of the goods.</summary>
    private static string Concept(IReadOnlyList<GoodsQuoteLine> lines)
    {
        var concept = $"Mercadería: {string.Join(", ", lines.Select(line => line.ProductName).Distinct())}";
        return concept.Length <= 200 ? concept : concept[..197] + "...";
    }
}
