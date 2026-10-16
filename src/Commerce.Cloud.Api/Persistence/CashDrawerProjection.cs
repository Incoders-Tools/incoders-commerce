using Commerce.Domain.CashSessions;
using Commerce.Domain.Sync;
using Commerce.Domain.Sync.Payloads;
using Microsoft.Extensions.Logging;
using Npgsql;

namespace Commerce.Cloud.Api.Persistence;

/// <summary>
/// branch -> cloud: what happens to the drawer outside a sale reaches the treasury. In the inbox transaction:
/// <list type="bullet">
/// <item><c>cash-movement.recorded</c>: a withdrawal takes the money Out of the branch Cash account (to the safe it is a
/// transfer into the branch Safe account; to the bank, an expense or other, an Out with its reason), a deposit puts it
/// In (from the safe, a transfer out of the Safe). Plus the <c>cash-movement.recorded</c> audit row with who
/// authorized it.</item>
/// <item><c>cash-session.closed</c> with a difference: the surplus In / the shortage Out of the branch Cash account
/// ("Sobrante/Faltante de arqueo"), so the treasury matches the money actually counted. (The close audit row is written
/// by <c>CashSessionAudit</c>.)</item>
/// </list>
/// Every posting is keyed by the movement or session id: a redelivery writes nothing twice. Never blocks ingestion:
/// savepoint, logged and contained.
/// </summary>
internal static class CashDrawerProjection
{
    public const string MovementSourceType = "PosCashMovement";
    public const string CountSourceType = "CashSessionClose";

    private const string Savepoint = "cash_drawer_projection";

    public static async Task ProjectAsync(
        NpgsqlConnection connection, NpgsqlTransaction tx, SyncEnvelope envelope, ILogger? logger, CancellationToken ct)
    {
        if (envelope.PayloadKind is not (CashMovementPayloadKinds.Recorded or CashSessionPayloadKinds.Closed))
        {
            return;
        }

        await tx.SaveAsync(Savepoint, ct);
        try
        {
            if (envelope.PayloadKind == CashMovementPayloadKinds.Recorded)
            {
                await MovementAsync(connection, tx, envelope, SyncPayloadCodec.Deserialize<CashMovementRecordedPayloadV1>(envelope.Payload), ct);
            }
            else
            {
                await CountDifferenceAsync(connection, tx, envelope, SyncPayloadCodec.Deserialize<CashSessionClosedPayloadV1>(envelope.Payload), ct);
            }

            await tx.ReleaseAsync(Savepoint, ct);
        }
        catch (Exception ex) when (!ct.IsCancellationRequested && ex is not OutOfMemoryException)
        {
            await tx.RollbackAsync(Savepoint, ct);
            logger?.LogWarning(ex,
                "Cash drawer event {AggregateId} ({Kind}) was ingested but not posted to the treasury ({Failure}: {Message})",
                envelope.AggregateId, envelope.PayloadKind, ex is PostgresException pg ? pg.SqlState : ex.GetType().Name, ex.Message);
        }
    }

    private static async Task MovementAsync(
        NpgsqlConnection connection, NpgsqlTransaction tx, SyncEnvelope envelope, CashMovementRecordedPayloadV1 payload, CancellationToken ct)
    {
        if (payload.MovementId == Guid.Empty || payload.Amount <= 0m
            || !CashMovement.IsValidKind(payload.Kind) || !CashMovement.IsValidCounterpart(payload.Kind, payload.Counterpart))
        {
            return;
        }

        var org = envelope.OrganizationId;
        var date = PosSaleAccountProjection.BusinessDate(payload.OccurredAtUtc);
        var concept = $"{CashMovement.Describe(payload.Kind, payload.Counterpart)} (POS): {payload.Reason}";
        var cash = await PostgresTreasuryStore.EnsureAccountAsync(connection, tx, org, envelope.BranchId, PostgresTreasuryStore.Cash, ct);

        bool written;
        if (payload.Counterpart == CashMovement.Safe)
        {
            var safe = await PostgresTreasuryStore.EnsureAccountAsync(connection, tx, org, envelope.BranchId, PostgresTreasuryStore.Safe, ct);
            var (from, to) = payload.Kind == CashMovement.Withdrawal ? (cash, safe) : (safe, cash);
            written = await AlreadyPostedAsync(connection, tx, payload.MovementId, ct) is false;
            if (written)
            {
                await PostgresTreasuryStore.WriteTransferAsync(
                    connection, tx, org, payload.MovementId, from, (await PostgresTreasuryStore.NameOfAsync(connection, tx, from, ct))!,
                    to, (await PostgresTreasuryStore.NameOfAsync(connection, tx, to, ct))!, payload.Amount, payload.OccurredAtUtc, date,
                    $"{payload.Reason} (POS)", null, (MovementSourceType, payload.MovementId), payload.OperatorId, ct);
            }
        }
        else
        {
            var withdrawal = payload.Kind == CashMovement.Withdrawal;
            written = await PostgresTreasuryStore.InsertAsync(
                connection, tx, org, Guid.NewGuid(), cash, withdrawal ? "CashWithdrawal" : "CashDeposit", withdrawal ? "Out" : "In",
                payload.Amount, payload.OccurredAtUtc, date, concept, null, null, MovementSourceType, payload.MovementId, null,
                payload.OperatorId, ct);
        }

        if (!written)
        {
            return; // already posted
        }

        await PostgresTreasuryStore.AuditAsync(connection, tx, org, payload.OperatorId, "cash-session", payload.CashSessionId,
            CashMovementPayloadKinds.Recorded, new
            {
                movementId = payload.MovementId,
                branchId = envelope.BranchId,
                kind = payload.Kind,
                counterpart = payload.Counterpart,
                amount = payload.Amount,
                reason = payload.Reason,
                authorizationMethod = payload.Authorization?.Method,
                authorizedBy = payload.Authorization?.OperatorId,
                pinVersion = payload.Authorization?.PinVersion,
                occurredAtUtc = payload.OccurredAtUtc,
            }, ct);
    }

    private static async Task CountDifferenceAsync(
        NpgsqlConnection connection, NpgsqlTransaction tx, SyncEnvelope envelope, CashSessionClosedPayloadV1 payload, CancellationToken ct)
    {
        if (payload.SessionId == Guid.Empty || payload.Difference == 0m)
        {
            return;
        }

        var surplus = payload.Difference > 0m;
        var cash = await PostgresTreasuryStore.EnsureAccountAsync(
            connection, tx, envelope.OrganizationId, envelope.BranchId, PostgresTreasuryStore.Cash, ct);
        var concept = string.Create(
            Money,
            $"{(surplus ? "Sobrante" : "Faltante")} de arqueo al cerrar la caja (esperado {payload.ExpectedCash:C}, contado {payload.CountedCash:C})");
        await PostgresTreasuryStore.InsertAsync(
            connection, tx, envelope.OrganizationId, Guid.NewGuid(), cash, "CashCountDifference", surplus ? "In" : "Out",
            Math.Abs(payload.Difference), payload.ClosedAtUtc, PosSaleAccountProjection.BusinessDate(payload.ClosedAtUtc), concept,
            null, null, CountSourceType, payload.SessionId, null, payload.ClosedByOperatorId, ct);
    }

    private static readonly System.Globalization.CultureInfo Money = System.Globalization.CultureInfo.GetCultureInfo("es-AR");

    /// <summary>Whether the movement's transfer to or from the safe was already written (its Out leg is keyed by the movement).</summary>
    private static async Task<bool> AlreadyPostedAsync(NpgsqlConnection connection, NpgsqlTransaction tx, Guid movementId, CancellationToken ct)
    {
        await using var cmd = new NpgsqlCommand(
            "SELECT 1 FROM treasury_movements WHERE source_type = $1 AND source_id = $2", connection, tx);
        cmd.Parameters.AddWithValue(MovementSourceType);
        cmd.Parameters.AddWithValue(movementId);
        return await cmd.ExecuteScalarAsync(ct) is not null;
    }
}
