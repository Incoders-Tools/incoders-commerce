using System.Text.Json;
using Commerce.Cloud.Api.Auditing;
using Commerce.Domain.Sales;
using Commerce.Domain.Sync;
using Commerce.Domain.Sync.Payloads;
using Commerce.Domain.Tenancy;
using Microsoft.Extensions.Logging;
using Npgsql;

namespace Commerce.Cloud.Api.Persistence;

/// <summary>
/// Projects a `sale` envelope into `pos_sales` inside the inbox transaction (0023,
/// branch-offline-sync "Sale Number Projection") and verifies the human number the
/// terminal claimed. The terminal numbers sales offline, so the server can only check:
///   - the claim is complete and in range;
///   - the calling installation held THAT register in THAT branch (`terminal_registers`;
///     a released row still counts, a sale may sync after the terminal moved) and the
///     branch code matches `branches.code`;
///   - the number is not already used by another sale.
/// A rejected claim stores the sale with NULL number parts and writes a
/// `sale.number_conflict` audit row. NOTHING here may block ingestion: the whole projection
/// runs in a savepoint, so even a missing table (API deployed ahead of 0023) only skips it.
/// </summary>
internal static class PosSaleProjection
{
    public const string ConflictAction = "sale.number_conflict";
    public const string FailureAction = "sale.projection_failed";

    private const string Savepoint = "pos_sale_projection";

    public static async Task ProjectAsync(
        NpgsqlConnection connection, NpgsqlTransaction tx, SyncEnvelope envelope, Guid? installationId, ILogger? logger, CancellationToken ct)
    {
        if (envelope.PayloadKind != "sale" || !TryReadPayload(envelope, out var payload))
        {
            return;
        }

        await tx.SaveAsync(Savepoint, ct);
        try
        {
            if (FaultInjection is { } inject) await inject(payload);
            await ProjectCoreAsync(connection, tx, envelope, payload, installationId, ct);
            await tx.ReleaseAsync(Savepoint, ct);
        }
        // Numbering must never block ingestion, so ANY failure of the projection (a Postgres error, a
        // driver or cast error, ...) is contained. Only a cancelled request, which abandons the whole
        // inbox transaction anyway, and a dead process are allowed through.
        catch (Exception ex) when (!ct.IsCancellationRequested && ex is not OutOfMemoryException)
        {
            await tx.RollbackAsync(Savepoint, ct);
            logger?.LogWarning(ex,
                "Sale {SaleId} was ingested but not projected into pos_sales ({Failure}); is migration 0023 applied?",
                payload.SaleId, ex is PostgresException pg ? pg.SqlState : ex.GetType().Name);

            // A missing table (API ahead of 0023) is a deployment state, not a per-sale event.
            if (ex is not PostgresException { SqlState: PostgresErrorCodes.UndefinedTable })
            {
                await TryAuditFailureAsync(connection, tx, envelope, payload, installationId, ex, logger, ct);
            }
        }
    }

    /// <summary>Test seam: runs inside the guarded region, before the projection, so a test can make it fail.</summary>
    internal static Func<SalePayloadV1, Task>? FaultInjection { get; set; }

    /// <summary>Best effort and itself savepointed: a failing audit insert must not abort the inbox transaction.</summary>
    private static async Task TryAuditFailureAsync(
        NpgsqlConnection connection, NpgsqlTransaction tx, SyncEnvelope envelope, SalePayloadV1 payload,
        Guid? installationId, Exception failure, ILogger? logger, CancellationToken ct)
    {
        const string auditSavepoint = "pos_sale_projection_audit";
        await tx.SaveAsync(auditSavepoint, ct);
        try
        {
            var entry = Audit(envelope, payload, installationId, FailureAction, new
            {
                reason = "projection-error",
                error = failure.GetType().Name,
                branchId = envelope.BranchId,
                installationId,
                operationId = envelope.OperationId,
            });
            await AuditLogWriter.InsertAsync(connection, tx, entry, ct);
            await tx.ReleaseAsync(auditSavepoint, ct);
        }
        catch (Exception ex) when (!ct.IsCancellationRequested && ex is not OutOfMemoryException)
        {
            await tx.RollbackAsync(auditSavepoint, ct);
            logger?.LogWarning(ex, "The projection failure of sale {SaleId} could not be audited.", payload.SaleId);
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

    private static async Task ProjectCoreAsync(
        NpgsqlConnection connection, NpgsqlTransaction tx, SyncEnvelope envelope, SalePayloadV1 payload, Guid? installationId, CancellationToken ct)
    {
        var claimed = payload.BranchCode is not null || payload.RegisterNumber is not null || payload.SaleSequence is not null;
        string? conflict = null;

        if (claimed)
        {
            conflict = await ValidateClaimAsync(connection, tx, envelope, payload, installationId, ct);
            if (conflict is null)
            {
                var inserted = await InsertAsync(connection, tx, envelope, payload, (short)payload.RegisterNumber!.Value, payload.SaleSequence, ct);
                if (inserted) return;

                // Nothing was inserted: either this sale id is already projected (nothing to do) or
                // the number is taken by another sale.
                if (await SaleExistsAsync(connection, tx, envelope.OrganizationId, payload.SaleId, ct)) return;
                conflict = "number-already-used";
            }
        }

        await InsertAsync(connection, tx, envelope, payload, register: null, sequence: null, ct);
        if (conflict is not null)
        {
            await AuditLogWriter.InsertAsync(connection, tx, ConflictAudit(envelope, payload, installationId, conflict), ct);
        }
    }

    /// <summary>Null when the claim is verified; otherwise the reason it is rejected.</summary>
    private static async Task<string?> ValidateClaimAsync(
        NpgsqlConnection connection, NpgsqlTransaction tx, SyncEnvelope envelope, SalePayloadV1 payload, Guid? installationId, CancellationToken ct)
    {
        if (payload.BranchCode is not { } code || payload.RegisterNumber is not { } register || payload.SaleSequence is not { } sequence)
        {
            return "incomplete-claim";
        }
        if (code is < BranchCode.MinValue or > BranchCode.MaxValue
            || register is < RegisterNumber.MinValue or > RegisterNumber.MaxValue
            || sequence < 1)
        {
            return "out-of-range";
        }
        if (installationId is null)
        {
            return "installation-unknown";
        }

        await using (var branchCmd = new NpgsqlCommand("SELECT code FROM branches WHERE id = $1", connection, tx))
        {
            branchCmd.Parameters.AddWithValue(envelope.BranchId);
            if (await branchCmd.ExecuteScalarAsync(ct) is not short actualCode || actualCode != code)
            {
                return "branch-code-mismatch";
            }
        }

        await using var registerCmd = new NpgsqlCommand(
            """
            SELECT 1 FROM terminal_registers
             WHERE organization_id = $1 AND branch_id = $2 AND installation_id = $3 AND register_number = $4
            """, connection, tx);
        registerCmd.Parameters.AddWithValue(envelope.OrganizationId);
        registerCmd.Parameters.AddWithValue(envelope.BranchId);
        registerCmd.Parameters.AddWithValue(installationId.Value);
        registerCmd.Parameters.AddWithValue((short)register);
        return await registerCmd.ExecuteScalarAsync(ct) is null ? "register-not-assigned" : null;
    }

    /// <summary>ON CONFLICT DO NOTHING: a clash on the sale id or on the number is an outcome, never an error that aborts the transaction.</summary>
    private static async Task<bool> InsertAsync(
        NpgsqlConnection connection, NpgsqlTransaction tx, SyncEnvelope envelope, SalePayloadV1 payload,
        short? register, int? sequence, CancellationToken ct)
    {
        await using var cmd = new NpgsqlCommand(
            """
            INSERT INTO pos_sales
                (organization_id, branch_id, sale_id, register_number, sale_sequence, operation_id, occurred_at_utc, total_amount)
            VALUES ($1, $2, $3, $4, $5, $6, $7, $8)
            ON CONFLICT DO NOTHING
            """, connection, tx);
        cmd.Parameters.AddWithValue(envelope.OrganizationId);
        cmd.Parameters.AddWithValue(envelope.BranchId);
        cmd.Parameters.AddWithValue(payload.SaleId);
        cmd.Parameters.Add(new NpgsqlParameter { Value = (object?)register ?? DBNull.Value, NpgsqlDbType = NpgsqlTypes.NpgsqlDbType.Smallint });
        cmd.Parameters.Add(new NpgsqlParameter { Value = (object?)sequence ?? DBNull.Value, NpgsqlDbType = NpgsqlTypes.NpgsqlDbType.Integer });
        cmd.Parameters.AddWithValue(envelope.OperationId);
        cmd.Parameters.AddWithValue(envelope.OccurredAtUtc);
        cmd.Parameters.AddWithValue(payload.TotalAmount);
        return await cmd.ExecuteNonQueryAsync(ct) == 1;
    }

    private static async Task<bool> SaleExistsAsync(
        NpgsqlConnection connection, NpgsqlTransaction tx, Guid organizationId, Guid saleId, CancellationToken ct)
    {
        await using var cmd = new NpgsqlCommand("SELECT 1 FROM pos_sales WHERE organization_id = $1 AND sale_id = $2", connection, tx);
        cmd.Parameters.AddWithValue(organizationId);
        cmd.Parameters.AddWithValue(saleId);
        return await cmd.ExecuteScalarAsync(ct) is not null;
    }

    private static UserManagementAuditEntry ConflictAudit(SyncEnvelope envelope, SalePayloadV1 payload, Guid? installationId, string reason)
    {
        var claimedNumber = payload.BranchCode is { } b && payload.RegisterNumber is { } r && payload.SaleSequence is { } s
            ? $"{SaleNumber.TypeLetter}{b:00}-{RegisterNumber.Prefix}{r}-{s}"
            : null;
        return Audit(envelope, payload, installationId, ConflictAction, new
        {
            reason,
            claimedNumber,
            branchId = envelope.BranchId,
            installationId,
            operationId = envelope.OperationId,
        });
    }

    private static UserManagementAuditEntry Audit(
        SyncEnvelope envelope, SalePayloadV1 payload, Guid? installationId, string action, object detail) =>
        new(
            ActorKind: AuditActorKinds.OrgUser,
            ActorId: envelope.ActorId,
            OrganizationId: envelope.OrganizationId,
            EntityType: "sale",
            EntityId: payload.SaleId,
            Action: action,
            OldValueJson: null,
            NewValueJson: JsonSerializer.Serialize(detail));
}
