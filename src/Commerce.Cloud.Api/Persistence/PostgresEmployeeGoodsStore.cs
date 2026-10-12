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
    int? RunNumber);

public enum EmployeeGoodsOutcome
{
    Done,
    NotFound,
    /// <summary>The employee has no linked customer ("lleva mercadería" off): there is no account to charge.</summary>
    NoGoodsAccount,
    /// <summary>A presentation has no effective price on the default list (or no default list exists).</summary>
    NoPrice,
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
        if (goodsId is not null && await RegisteredAsync(scope, employeeId, id, ct) is { } earlier)
        {
            return (EmployeeGoodsOutcome.Done, earlier);
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

        if (await ReadRegistrationAsync(connection, tx, employeeId, id, ct) is { } raced)
        {
            await tx.RollbackAsync(ct);
            return (EmployeeGoodsOutcome.Done, raced);
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

    private async Task<GoodsRegistration?> RegisteredAsync(CloudTenantScope scope, Guid employeeId, Guid goodsId, CancellationToken ct)
    {
        await using var connection = await _dataSource.OpenConnectionAsync(ct);
        await using var tx = await connection.BeginTransactionAsync(ct);
        await TenantScopeSql.ApplyAsync(connection, tx, scope.OrganizationId, null, ct);
        var registration = await ReadRegistrationAsync(connection, tx, employeeId, goodsId, ct);
        await tx.CommitAsync(ct);
        return registration;
    }

    /// <summary>A web registration already written under <paramref name="goodsId"/>, rebuilt from its items; null when none.</summary>
    private static async Task<GoodsRegistration?> ReadRegistrationAsync(
        NpgsqlConnection connection, NpgsqlTransaction tx, Guid employeeId, Guid goodsId, CancellationToken ct)
    {
        var items = await ReadItemsAsync(connection, tx, "WHERE g.source_type = 'Web' AND g.source_id = $1 ORDER BY g.line_no", goodsId, ct);
        if (items.Count == 0)
        {
            return null;
        }

        var lines = items.Select(item => new GoodsQuoteLine(
            item.PresentationId ?? Guid.Empty, item.ProductName, item.PresentationName, item.Quantity, item.UnitPrice, item.LineTotal)).ToList();
        return new GoodsRegistration(goodsId, lines.Sum(line => line.LineTotal), lines);
    }

    /// <summary>The employee's goods items, newest day first, with where each stands.</summary>
    public async Task<IReadOnlyList<GoodsItemRecord>> ListAsync(CloudTenantScope scope, Guid employeeId, CancellationToken ct)
    {
        await using var connection = await _dataSource.OpenConnectionAsync(ct);
        await using var tx = await connection.BeginTransactionAsync(ct);
        await TenantScopeSql.ApplyAsync(connection, tx, scope.OrganizationId, null, ct);
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
                   CASE WHEN g.payslip_id IS NULL THEN 'Pending' WHEN run.status = 'Paid' THEN 'Paid' ELSE 'InPayroll' END, run.run_number
            FROM employee_goods_items g
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
                reader.GetDecimal(9), reader.GetDecimal(10), reader.GetDecimal(11), reader.GetString(12), reader.IsDBNull(13) ? null : reader.GetInt32(13)));
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
