using System.Security.Cryptography;
using System.Text;
using Commerce.Domain.Stock;
using Commerce.Domain.Sync;
using Commerce.Domain.Sync.Payloads;
using Microsoft.Extensions.Logging;
using Npgsql;

namespace Commerce.Cloud.Api.Persistence;

/// <summary>
/// branch -> cloud stock sync (purchases-receptions-and-stock T4). Runs inside the inbox transaction right after
/// <see cref="PosSaleProjection"/> and writes ONE negative `Sale` stock movement per sale line (source `PosSale`,
/// source_id = sale id, occurred_at = the sale time).
/// <para>
/// IDEMPOTENCY: the payload lines carry a per-sale `LineNumber`, not a global id, so the line key is derived
/// deterministically from (sale id, line number) (<see cref="LineKey"/>). The unique index on
/// (organization, branch, source_type, source_line_id) makes a redelivery - even under a different operation id - a no-op.
/// </para>
/// <para>
/// NOTHING here may block ingestion (same rule as the sale number): the whole projection runs in its own savepoint and a
/// failure is logged and contained. A line whose presentation the branch catalog does not know, or whose quantity is not
/// positive, is skipped with a warning (it cannot satisfy the ledger's foreign key / sign rule) while the other lines
/// still move stock. A voided sale's stock is put back by <see cref="PosSaleVoidProjection"/> (source `PosSaleVoid`), and a
/// sale whose void was ingested first moves no stock here.
/// </para>
/// </summary>
internal static class PosSaleStockProjection
{
    public const string SourceType = "PosSale";

    private const string Savepoint = "pos_sale_stock_projection";

    /// <summary>Stable id of a sale line: the first 16 bytes of SHA-256("pos-sale-line:{saleId}:{lineNumber}").</summary>
    public static Guid LineKey(Guid saleId, int lineNumber) =>
        new(SHA256.HashData(Encoding.UTF8.GetBytes($"pos-sale-line:{saleId:D}:{lineNumber}"))[..16]);

    public static async Task ProjectAsync(
        NpgsqlConnection connection, NpgsqlTransaction tx, SyncEnvelope envelope, ILogger? logger, CancellationToken ct)
    {
        if (envelope.PayloadKind != "sale" || !TryReadPayload(envelope, out var payload) || payload.Lines.Count == 0)
        {
            return;
        }

        await tx.SaveAsync(Savepoint, ct);
        try
        {
            await ProjectCoreAsync(connection, tx, envelope, payload, logger, ct);
            await tx.ReleaseAsync(Savepoint, ct);
        }
        catch (Exception ex) when (!ct.IsCancellationRequested && ex is not OutOfMemoryException)
        {
            await tx.RollbackAsync(Savepoint, ct);
            logger?.LogWarning(ex,
                "Sale {SaleId} was ingested but its stock movements were not written ({Failure}: {Message})",
                payload.SaleId, ex is PostgresException pg ? pg.SqlState : ex.GetType().Name, ex.Message);
        }
    }

    private static bool TryReadPayload(SyncEnvelope envelope, out SalePayloadV1 payload)
    {
        try
        {
            payload = SyncPayloadCodec.Deserialize<SalePayloadV1>(envelope.Payload);
            return payload.SaleId != Guid.Empty;
        }
        catch (Exception ex) when (ex is System.Text.Json.JsonException or InvalidOperationException or NotSupportedException)
        {
            payload = null!;
            return false;
        }
    }

    private static async Task ProjectCoreAsync(
        NpgsqlConnection connection, NpgsqlTransaction tx, SyncEnvelope envelope, SalePayloadV1 payload, ILogger? logger, CancellationToken ct)
    {
        // The stock ledger is branch-scoped (RLS on app.current_branch_id): the envelope names its branch.
        await TenantScopeSql.ApplyAsync(connection, tx, envelope.OrganizationId, envelope.BranchId, ct);

        // The void arrived first (this sale's push had failed and was retried): the sale never moves stock at all.
        if (await PosSaleVoidProjection.IsVoidedAsync(connection, tx, payload.SaleId, ct))
        {
            logger?.LogInformation("Sale {SaleId} is already voided: no stock movements written", payload.SaleId);
            return;
        }

        foreach (var line in payload.Lines)
        {
            if (line.Quantity <= 0)
            {
                logger?.LogWarning(
                    "Sale {SaleId} line {Line}: quantity {Quantity} is not a positive quantity, no stock movement written",
                    payload.SaleId, line.LineNumber, line.Quantity);
                continue;
            }

            if (!await PresentationExistsAsync(connection, tx, envelope.BranchId, line.PresentationId, ct))
            {
                logger?.LogWarning(
                    "Sale {SaleId} line {Line}: presentation {PresentationId} is unknown to branch {BranchId}, no stock movement written",
                    payload.SaleId, line.LineNumber, line.PresentationId, envelope.BranchId);
                continue;
            }

            await StockMovementWriter.InsertAsync(
                connection, tx,
                new NewStockMovement(
                    Guid.NewGuid(), envelope.OrganizationId, envelope.BranchId, line.PresentationId, -line.Quantity,
                    StockMovementKind.Sale, SourceType, payload.SaleId, LineKey(payload.SaleId, line.LineNumber),
                    OccurredAtUtc: payload.OccurredAtUtc),
                ct, ignoreDuplicateSourceLine: true);
        }
    }

    private static async Task<bool> PresentationExistsAsync(
        NpgsqlConnection connection, NpgsqlTransaction tx, Guid branchId, Guid presentationId, CancellationToken ct)
    {
        await using var cmd = new NpgsqlCommand("SELECT 1 FROM presentations WHERE branch_id = $1 AND id = $2", connection, tx);
        cmd.Parameters.AddWithValue(branchId);
        cmd.Parameters.AddWithValue(presentationId);
        return await cmd.ExecuteScalarAsync(ct) is not null;
    }
}
