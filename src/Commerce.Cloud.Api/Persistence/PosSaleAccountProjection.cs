using System.Text.Json;
using Commerce.Application.Time;
using Commerce.Cloud.Api.Auditing;
using Commerce.Domain.CurrentAccounts;
using Commerce.Domain.Sales;
using Commerce.Domain.Sync;
using Commerce.Domain.Sync.Payloads;
using Commerce.Domain.Tenancy;
using Microsoft.Extensions.Logging;
using Npgsql;

namespace Commerce.Cloud.Api.Persistence;

/// <summary>
/// branch -> cloud: the MONEY of a POS sale (the stock is <see cref="PosSaleStockProjection"/>). Runs in the inbox
/// transaction for every <c>sale</c> envelope and, as one unit:
/// <list type="bullet">
/// <item><b>Treasury</b>: a sale paid at the counter puts its total In the branch account of its payment method (Caja
/// efectivo, Tarjetas, QR). A sale on current account moves no money.</item>
/// <item><b>The customer's current account</b>, when the sale names a customer: the sale is charged (Invoice, Debit) and,
/// when it was paid at the counter, the payment is credited right away (the balance does not change, the history
/// shows every purchase). A sale on current account stays owed until its due date: the customer's payment terms, else
/// the organization's default (<see cref="PaymentTerms"/>).</item>
/// <item><b>Audit</b>: one <c>pos-sale.recorded</c> row (total, payment method, customer, due date).</item>
/// </list>
/// Every posting is keyed by the sale (source <c>PosSale</c> / <c>PosSalePayment</c> + sale id), so a redelivery, even
/// under another operation id, posts nothing twice. A sale whose void was ingested first posts nothing. Voiding a sale
/// takes all of it back (<see cref="ReverseAsync"/>, called by <see cref="PosSaleVoidProjection"/>). Never blocks
/// ingestion: savepoint, logged and contained.
/// </summary>
internal static class PosSaleAccountProjection
{
    public const string SourceType = "PosSale";
    public const string PaymentSourceType = "PosSalePayment";
    public const string VoidSourceType = "PosSaleVoid";
    public const string RecordedAuditAction = "pos-sale.recorded";

    private const string Savepoint = "pos_sale_money_projection";

    public static async Task ProjectAsync(
        NpgsqlConnection connection, NpgsqlTransaction tx, SyncEnvelope envelope, ILogger? logger, CancellationToken ct)
    {
        if (envelope.PayloadKind != SalePayloadKinds.Sale || !TryReadPayload(envelope, out var payload) || payload.TotalAmount <= 0m)
        {
            return;
        }

        await tx.SaveAsync(Savepoint, ct);
        try
        {
            await ProjectCoreAsync(connection, tx, envelope, payload, ct);
            await tx.ReleaseAsync(Savepoint, ct);
        }
        catch (Exception ex) when (!ct.IsCancellationRequested && ex is not OutOfMemoryException)
        {
            await tx.RollbackAsync(Savepoint, ct);
            logger?.LogWarning(ex,
                "Sale {SaleId} was ingested but its money (treasury, customer account) was not posted ({Failure}: {Message})",
                payload.SaleId, ex is PostgresException pg ? pg.SqlState : ex.GetType().Name, ex.Message);
        }
    }

    private static async Task ProjectCoreAsync(
        NpgsqlConnection connection, NpgsqlTransaction tx, SyncEnvelope envelope, SalePayloadV1 payload, CancellationToken ct)
    {
        if (await PosSaleVoidProjection.IsVoidedAsync(connection, tx, payload.SaleId, ct))
        {
            return;
        }

        var method = payload.Tender?.Method;
        var onAccount = method == SaleTender.Account;
        if (onAccount && payload.CustomerId is null)
        {
            throw new InvalidOperationException("A sale on current account without a customer cannot be charged.");
        }

        var date = BusinessDate(payload.OccurredAtUtc);
        var number = SaleNumberOf(payload);
        var label = number is null ? "Venta en el local" : $"Venta en el local {number}";
        var posted = false;

        // Treasury: the money that came in at the counter.
        posted |= await PostgresTreasuryStore.RecordInAsync(connection, tx, new NewTreasuryMovement(
            envelope.OrganizationId, envelope.BranchId, method ?? string.Empty, "Sale", payload.TotalAmount, payload.OccurredAtUtc,
            date, label, number, payload.CustomerId, SourceType, payload.SaleId, envelope.ActorId), ct);

        DateOnly? dueOn = null;
        if (payload.CustomerId is { } customerId)
        {
            var party = AccountParty.Customer(customerId);
            var terms = onAccount ? await PostgresTreasuryStore.CustomerTermsAsync(connection, tx, customerId, ct) : null;
            dueOn = terms?.DueOn(date) ?? date;
            var charge = await PostgresCurrentAccountStore.InsertAsync(
                connection, tx, envelope.OrganizationId, party,
                new NewAccountMovement(
                    Guid.NewGuid(), AccountMovementKind.Invoice, PartyAccountRules.DebtDirection(AccountPartyKind.Customer),
                    payload.TotalAmount, date, dueOn, number,
                    terms is null ? label : $"{label} · vence a {terms.Days} días ({TermsSourceText(terms)})", envelope.ActorId),
                null, ct, SourceType, payload.SaleId);
            if (charge is not null)
            {
                posted = true;
                await PostgresCurrentAccountStore.AuditAsync(
                    connection, tx, envelope.OrganizationId, "org-user", envelope.ActorId, party, "customer.movement_registered", charge, ct);
            }

            if (!onAccount && method is not null)
            {
                var paid = await PostgresCurrentAccountStore.InsertAsync(
                    connection, tx, envelope.OrganizationId, party,
                    new NewAccountMovement(
                        Guid.NewGuid(), AccountMovementKind.Payment, CurrentAccountRules.Opposite(PartyAccountRules.DebtDirection(AccountPartyKind.Customer)),
                        payload.TotalAmount, date, null, number, $"Pago en el local ({TenderText(method)})", envelope.ActorId),
                    null, ct, PaymentSourceType, payload.SaleId);
                if (paid is not null)
                {
                    await PostgresCurrentAccountStore.AuditAsync(
                        connection, tx, envelope.OrganizationId, "org-user", envelope.ActorId, party, "customer.movement_registered", paid, ct);
                }
            }
        }

        if (posted)
        {
            await AuditLogWriter.InsertAsync(connection, tx, new UserManagementAuditEntry(
                "org-user", envelope.ActorId, envelope.OrganizationId, "pos-sale", payload.SaleId, RecordedAuditAction, null,
                JsonSerializer.Serialize(new
                {
                    branchId = envelope.BranchId,
                    number,
                    total = payload.TotalAmount,
                    paymentMethod = method,
                    customerId = payload.CustomerId,
                    dueOn,
                    occurredAtUtc = payload.OccurredAtUtc,
                    cashSessionId = payload.CashSessionId,
                })), ct);
        }
    }

    /// <summary>
    /// Takes back what a voided sale posted: its treasury money (an Out Reversal) and its customer account movements (a
    /// Reversal of the charge and of the payment). Inside the void projection's transaction and scope; nothing to do for
    /// what was never posted.
    /// </summary>
    public static async Task ReverseAsync(
        NpgsqlConnection connection, NpgsqlTransaction tx, Guid organizationId, Guid saleId, string reason,
        DateTimeOffset voidedAtUtc, Guid actorId, CancellationToken ct)
    {
        var voidedOn = BusinessDate(voidedAtUtc);
        await PostgresTreasuryStore.ReverseSourceAsync(
            connection, tx, SourceType, saleId, VoidSourceType, $"Anulación de venta ({reason})", voidedAtUtc, voidedOn, actorId, ct);

        foreach (var sourceType in new[] { PaymentSourceType, SourceType })
        {
            await using var find = new NpgsqlCommand(
                "SELECT customer_id FROM current_account_movements WHERE source_type = $1 AND source_id = $2 AND customer_id IS NOT NULL",
                connection, tx);
            find.Parameters.AddWithValue(sourceType);
            find.Parameters.AddWithValue(saleId);
            if (await find.ExecuteScalarAsync(ct) is not Guid customerId)
            {
                continue;
            }

            var party = AccountParty.Customer(customerId);
            if (await PostgresCurrentAccountStore.FindBySourceAsync(connection, tx, party, sourceType, saleId, ct) is not { } posted)
            {
                continue;
            }

            var result = await PostgresCurrentAccountStore.ReverseWithinAsync(
                connection, tx, organizationId, party, posted.Id, $"Anulación: {posted.Concept} ({reason})", null, voidedOn, actorId, ct);
            if (result.Outcome == ReverseMovementOutcome.Reversed)
            {
                await PostgresCurrentAccountStore.AuditAsync(
                    connection, tx, organizationId, "org-user", actorId, party, "customer.movement_reversed", result.Reversal!, ct);
            }
        }
    }

    /// <summary>The business date (Argentina by default) a moment falls on: account and treasury movements are dated then.</summary>
    public static DateOnly BusinessDate(DateTimeOffset occurredAtUtc) =>
        DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(occurredAtUtc, Zone.Value).DateTime);

    private static readonly Lazy<TimeZoneInfo> Zone = new(() => BusinessTimeZone.Resolve(null));

    internal static string TermsSourceText(PaymentTerms terms) =>
        terms.Source == PaymentTermsSource.Customer ? "plazo del cliente" : "plazo general";

    internal static string TenderText(string method) => method switch
    {
        SaleTender.Cash => "efectivo",
        SaleTender.Card => "tarjeta",
        SaleTender.Qr => "QR",
        _ => method,
    };

    private static string? SaleNumberOf(SalePayloadV1 payload)
    {
        if (payload.BranchCode is not { } branch || payload.RegisterNumber is not { } register || payload.SaleSequence is not { } sequence)
        {
            return null;
        }

        try
        {
            return new SaleNumber(new BranchCode(branch), new RegisterNumber(register), sequence).Format();
        }
        catch (ArgumentOutOfRangeException)
        {
            return null;
        }
    }

    private static bool TryReadPayload(SyncEnvelope envelope, out SalePayloadV1 payload)
    {
        try
        {
            payload = SyncPayloadCodec.Deserialize<SalePayloadV1>(envelope.Payload);
            return payload.SaleId != Guid.Empty;
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException or NotSupportedException)
        {
            payload = null!;
            return false;
        }
    }
}
