using System.Text.Json;
using Commerce.Cloud.Api.Auditing;
using Commerce.Domain.CurrentAccounts;
using Commerce.Domain.Sync;
using Commerce.Domain.Sync.Payloads;
using Microsoft.Extensions.Logging;
using Npgsql;

namespace Commerce.Cloud.Api.Persistence;

/// <summary>
/// branch -> cloud: a payment a customer made at the POS against its current account ("cobro"). In the inbox transaction:
/// <list type="bullet">
/// <item><c>customer-payment.received</c>: a Payment (Credit) on the customer's account and the money In the branch
/// treasury account of its method, both keyed by the payment id (a redelivery posts nothing twice), plus the
/// <c>customer-payment.received</c> audit row. A payment whose void was ingested first posts nothing.</item>
/// <item><c>customer-payment.voided</c>: both taken back with Reversals, plus the <c>customer-payment.voided</c> audit row
/// (who authorized it with the PIN, and why).</item>
/// </list>
/// Never blocks ingestion: savepoint, logged and contained.
/// </summary>
internal static class CustomerPaymentProjection
{
    public const string SourceType = "PosCustomerPayment";
    public const string VoidSourceType = "PosCustomerPaymentVoid";

    private const string Savepoint = "customer_payment_projection";

    public static async Task ProjectAsync(
        NpgsqlConnection connection, NpgsqlTransaction tx, SyncEnvelope envelope, ILogger? logger, CancellationToken ct)
    {
        if (envelope.PayloadKind is not (CustomerPaymentPayloadKinds.Received or CustomerPaymentPayloadKinds.Voided))
        {
            return;
        }

        await tx.SaveAsync(Savepoint, ct);
        try
        {
            if (envelope.PayloadKind == CustomerPaymentPayloadKinds.Received)
            {
                await ReceiveAsync(connection, tx, envelope, SyncPayloadCodec.Deserialize<CustomerPaymentReceivedPayloadV1>(envelope.Payload), ct);
            }
            else
            {
                await VoidAsync(connection, tx, envelope, SyncPayloadCodec.Deserialize<CustomerPaymentVoidedPayloadV1>(envelope.Payload), ct);
            }

            await tx.ReleaseAsync(Savepoint, ct);
        }
        catch (Exception ex) when (!ct.IsCancellationRequested && ex is not OutOfMemoryException)
        {
            await tx.RollbackAsync(Savepoint, ct);
            logger?.LogWarning(ex,
                "Customer payment {PaymentId} ({Kind}) was ingested but not posted ({Failure}: {Message})",
                envelope.AggregateId, envelope.PayloadKind, ex is PostgresException pg ? pg.SqlState : ex.GetType().Name, ex.Message);
        }
    }

    private static async Task ReceiveAsync(
        NpgsqlConnection connection, NpgsqlTransaction tx, SyncEnvelope envelope, CustomerPaymentReceivedPayloadV1 payload, CancellationToken ct)
    {
        if (payload.PaymentId == Guid.Empty || payload.Amount <= 0m || await IsVoidedAsync(connection, tx, payload.PaymentId, ct))
        {
            return;
        }

        var date = PosSaleAccountProjection.BusinessDate(payload.ReceivedAtUtc);
        var method = PosSaleAccountProjection.TenderText(payload.Tender.Method);
        var concept = string.IsNullOrWhiteSpace(payload.Note) ? $"Cobro en el local ({method})" : $"Cobro en el local ({method}) · {payload.Note}";
        var party = AccountParty.Customer(payload.CustomerId);

        var credit = await PostgresCurrentAccountStore.InsertAsync(
            connection, tx, envelope.OrganizationId, party,
            new NewAccountMovement(
                Guid.NewGuid(), AccountMovementKind.Payment, CurrentAccountRules.Opposite(PartyAccountRules.DebtDirection(AccountPartyKind.Customer)),
                payload.Amount, date, null, null, concept, envelope.ActorId),
            null, ct, SourceType, payload.PaymentId);
        if (credit is null)
        {
            return; // already posted
        }

        await PostgresCurrentAccountStore.AuditAsync(
            connection, tx, envelope.OrganizationId, "org-user", envelope.ActorId, party, "customer.movement_registered", credit, ct);
        await PostgresTreasuryStore.RecordInAsync(connection, tx, new NewTreasuryMovement(
            envelope.OrganizationId, envelope.BranchId, payload.Tender.Method, "CustomerPayment", payload.Amount, payload.ReceivedAtUtc, date,
            concept, null, payload.CustomerId, SourceType, payload.PaymentId, envelope.ActorId), ct);
        await AuditLogWriter.InsertAsync(connection, tx, new UserManagementAuditEntry(
            "org-user", envelope.ActorId, envelope.OrganizationId, "customer", payload.CustomerId, CustomerPaymentPayloadKinds.Received, null,
            JsonSerializer.Serialize(new
            {
                paymentId = payload.PaymentId,
                branchId = envelope.BranchId,
                amount = payload.Amount,
                method = payload.Tender.Method,
                amountReceived = payload.Tender.AmountReceived,
                change = payload.Tender.ChangeGiven,
                cashSessionId = payload.CashSessionId,
                note = payload.Note,
                receivedAtUtc = payload.ReceivedAtUtc,
            })), ct);
    }

    private static async Task VoidAsync(
        NpgsqlConnection connection, NpgsqlTransaction tx, SyncEnvelope envelope, CustomerPaymentVoidedPayloadV1 payload, CancellationToken ct)
    {
        if (payload.PaymentId == Guid.Empty)
        {
            return;
        }

        var date = PosSaleAccountProjection.BusinessDate(payload.VoidedAtUtc);
        var party = AccountParty.Customer(payload.CustomerId);
        if (await PostgresCurrentAccountStore.FindBySourceAsync(connection, tx, party, SourceType, payload.PaymentId, ct) is { } credit)
        {
            var result = await PostgresCurrentAccountStore.ReverseWithinAsync(
                connection, tx, envelope.OrganizationId, party, credit.Id, $"Anulación: {credit.Concept} ({payload.Reason})", null, date,
                payload.VoidedByOperatorId, ct);
            if (result.Outcome == ReverseMovementOutcome.Reversed)
            {
                await PostgresCurrentAccountStore.AuditAsync(
                    connection, tx, envelope.OrganizationId, "org-user", payload.VoidedByOperatorId, party, "customer.movement_reversed", result.Reversal!, ct);
            }
        }

        await PostgresTreasuryStore.ReverseSourceAsync(
            connection, tx, SourceType, payload.PaymentId, VoidSourceType, $"Anulación de cobro ({payload.Reason})", payload.VoidedAtUtc, date,
            payload.VoidedByOperatorId, ct);
        await AuditLogWriter.InsertAsync(connection, tx, new UserManagementAuditEntry(
            "org-user", payload.VoidedByOperatorId, envelope.OrganizationId, "customer", payload.CustomerId, CustomerPaymentPayloadKinds.Voided, null,
            JsonSerializer.Serialize(new
            {
                paymentId = payload.PaymentId,
                branchId = envelope.BranchId,
                amount = payload.Amount,
                method = payload.Tender.Method,
                reason = payload.Reason,
                authorizationMethod = payload.Authorization.Method,
                authorizedBy = payload.Authorization.OperatorId,
                pinVersion = payload.Authorization.PinVersion,
                cashSessionId = payload.CashSessionId,
                voidedAtUtc = payload.VoidedAtUtc,
            })), ct);
    }

    /// <summary>A void of the payment was already ingested (the void travels with the payment id as its aggregate).</summary>
    private static async Task<bool> IsVoidedAsync(NpgsqlConnection connection, NpgsqlTransaction tx, Guid paymentId, CancellationToken ct)
    {
        await using var cmd = new NpgsqlCommand(
            "SELECT 1 FROM sync_inbox WHERE aggregate_id = $1 AND payload_kind = $2", connection, tx);
        cmd.Parameters.AddWithValue(paymentId);
        cmd.Parameters.AddWithValue(CustomerPaymentPayloadKinds.Voided);
        return await cmd.ExecuteScalarAsync(ct) is not null;
    }
}
