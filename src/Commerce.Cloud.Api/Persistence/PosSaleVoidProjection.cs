using System.Text.Json;
using Commerce.Cloud.Api.Auditing;
using Commerce.Domain.Stock;
using Commerce.Domain.Sync;
using Commerce.Domain.Sync.Payloads;
using Microsoft.Extensions.Logging;
using Npgsql;

namespace Commerce.Cloud.Api.Persistence;

/// <summary>
/// branch -> cloud sale voids (0044). Runs inside the inbox transaction for a <c>sale.voided</c> envelope:
/// <list type="number">
/// <item>records the void in <c>pos_sale_voids</c> (append-only; a redelivery is a no-op);</item>
/// <item>writes the <c>sale.voided</c> audit row (operator, authorizing PIN holder and version, reason, total);</item>
/// <item>puts back the stock the sale took out: one <c>Reversal</c> movement of source <c>PosSaleVoid</c> per original
/// <c>PosSale</c> movement of that sale, keyed by the same line key, so a redelivery writes nothing twice;</item>
/// <item>takes the sale's money back: its treasury movement and its customer account movements (<see cref="PosSaleAccountProjection"/>).</item>
/// </list>
/// A void ingested BEFORE its sale (the sale's push failed and was retried later) records the void and reverses nothing;
/// <see cref="PosSaleStockProjection"/> then skips the stock of a sale that is already voided, so the stock ends the same
/// either way. Like every projection here, nothing blocks ingestion: the work runs in a savepoint and a failure is
/// logged and contained.
/// </summary>
internal static class PosSaleVoidProjection
{
    public const string SourceType = "PosSaleVoid";
    public const string AuditAction = "sale.voided";

    private const string Savepoint = "pos_sale_void_projection";

    public static async Task ProjectAsync(
        NpgsqlConnection connection, NpgsqlTransaction tx, SyncEnvelope envelope, ILogger? logger, CancellationToken ct)
    {
        if (envelope.PayloadKind != SalePayloadKinds.Voided || !TryReadPayload(envelope, out var payload))
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
                "Void of sale {SaleId} was ingested but not projected ({Failure}: {Message})",
                payload.SaleId, ex is PostgresException pg ? pg.SqlState : ex.GetType().Name, ex.Message);
        }
    }

    /// <summary>True when the sale has a recorded void (the stock projection then leaves the sale's stock alone).</summary>
    public static async Task<bool> IsVoidedAsync(NpgsqlConnection connection, NpgsqlTransaction tx, Guid saleId, CancellationToken ct)
    {
        await using var cmd = new NpgsqlCommand("SELECT 1 FROM pos_sale_voids WHERE sale_id = $1", connection, tx);
        cmd.Parameters.AddWithValue(saleId);
        return await cmd.ExecuteScalarAsync(ct) is not null;
    }

    private static bool TryReadPayload(SyncEnvelope envelope, out SaleVoidedPayloadV1 payload)
    {
        try
        {
            payload = SyncPayloadCodec.Deserialize<SaleVoidedPayloadV1>(envelope.Payload);
            return payload.SaleId != Guid.Empty && payload.Authorization is not null;
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException or NotSupportedException)
        {
            payload = null!;
            return false;
        }
    }

    private static async Task ProjectCoreAsync(
        NpgsqlConnection connection, NpgsqlTransaction tx, SyncEnvelope envelope, SaleVoidedPayloadV1 payload, CancellationToken ct)
    {
        // The stock ledger is branch-scoped (RLS on app.current_branch_id): the envelope names its branch.
        await TenantScopeSql.ApplyAsync(connection, tx, envelope.OrganizationId, envelope.BranchId, ct);

        int inserted;
        await using (var insert = new NpgsqlCommand(
            """
            INSERT INTO pos_sale_voids
                (organization_id, branch_id, sale_id, operation_id, voided_at_utc, voided_by_operator_id,
                 authorized_by, pin_version, reason, total_amount, cash_session_id)
            VALUES ($1, $2, $3, $4, $5, $6, $7, $8, $9, $10, $11)
            ON CONFLICT (organization_id, sale_id) DO NOTHING
            """, connection, tx))
        {
            insert.Parameters.AddWithValue(envelope.OrganizationId);
            insert.Parameters.AddWithValue(envelope.BranchId);
            insert.Parameters.AddWithValue(payload.SaleId);
            insert.Parameters.AddWithValue(envelope.OperationId);
            insert.Parameters.AddWithValue(payload.VoidedAtUtc);
            insert.Parameters.AddWithValue(payload.VoidedByOperatorId);
            insert.Parameters.AddWithValue(payload.Authorization.OperatorId);
            insert.Parameters.AddWithValue(payload.Authorization.PinVersion);
            insert.Parameters.AddWithValue(payload.Reason ?? string.Empty);
            insert.Parameters.AddWithValue(payload.TotalAmount);
            insert.Parameters.AddWithValue(NpgsqlTypes.NpgsqlDbType.Uuid, (object?)payload.CashSessionId ?? DBNull.Value);
            inserted = await insert.ExecuteNonQueryAsync(ct);
        }

        if (inserted == 0)
        {
            return; // Already projected (same void under another operation id): nothing more to write.
        }

        await AuditLogWriter.InsertAsync(connection, tx, new UserManagementAuditEntry(
            ActorKind: "org-user",
            ActorId: payload.VoidedByOperatorId,
            OrganizationId: envelope.OrganizationId,
            EntityType: "pos-sale",
            EntityId: payload.SaleId,
            Action: AuditAction,
            OldValueJson: null,
            NewValueJson: JsonSerializer.Serialize(new
            {
                branchId = envelope.BranchId,
                totalAmount = payload.TotalAmount,
                tenderMethod = payload.Tender?.Method,
                cashSessionId = payload.CashSessionId,
                reason = payload.Reason,
                authorizationMethod = payload.Authorization.Method,
                authorizedBy = payload.Authorization.OperatorId,
                pinVersion = payload.Authorization.PinVersion,
                voidedAtUtc = payload.VoidedAtUtc,
            })), ct);

        // Its money comes back: the treasury movement and the customer's account movements of the sale.
        await PosSaleAccountProjection.ReverseAsync(
            connection, tx, envelope.OrganizationId, payload.SaleId, payload.Reason ?? string.Empty,
            payload.VoidedAtUtc, payload.VoidedByOperatorId, ct);

        var originals = new List<(Guid Id, Guid PresentationId, decimal Quantity, Guid? SourceLineId)>();
        await using (var find = new NpgsqlCommand(
            """
            SELECT id, presentation_id, quantity, source_line_id FROM stock_movements
            WHERE source_type = $1 AND source_id = $2 AND kind = 'Sale'
            """, connection, tx))
        {
            find.Parameters.AddWithValue(PosSaleStockProjection.SourceType);
            find.Parameters.AddWithValue(payload.SaleId);
            await using var reader = await find.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct))
            {
                originals.Add((reader.GetGuid(0), reader.GetGuid(1), reader.GetDecimal(2), reader.IsDBNull(3) ? null : reader.GetGuid(3)));
            }
        }

        foreach (var original in originals)
        {
            await StockMovementWriter.InsertAsync(
                connection, tx,
                new NewStockMovement(
                    Guid.NewGuid(), envelope.OrganizationId, envelope.BranchId, original.PresentationId, -original.Quantity,
                    StockMovementKind.Reversal, SourceType, payload.SaleId, original.SourceLineId ?? original.Id,
                    ReversesMovementId: original.Id, Reason: payload.Reason, OccurredAtUtc: payload.VoidedAtUtc),
                ct, ignoreDuplicateSourceLine: true);
        }
    }
}
