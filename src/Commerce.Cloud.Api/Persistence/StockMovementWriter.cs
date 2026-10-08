using Commerce.Domain.Stock;
using Npgsql;

namespace Commerce.Cloud.Api.Persistence;

/// <summary>A stock movement to append. `Quantity` is SIGNED (see <see cref="StockMovementKind"/>).</summary>
public sealed record NewStockMovement(
    Guid Id,
    Guid OrganizationId,
    Guid BranchId,
    Guid PresentationId,
    decimal Quantity,
    StockMovementKind Kind,
    string? SourceType = null,
    Guid? SourceId = null,
    Guid? SourceLineId = null,
    Guid? ReversesMovementId = null,
    string? Reason = null,
    string? LotCode = null,
    Guid? CreatedByUserId = null,
    DateTimeOffset? OccurredAtUtc = null);

/// <summary>
/// The single writer of `stock_movements` (append-only, branch RLS). Participates in the CALLER's transaction (which has
/// already applied the tenant scope with `set_config`), so a reception confirmation, a void or a synced sale line writes
/// its movements atomically with the rest of its work.
/// <para>
/// IDEMPOTENCY: a movement with a `SourceLineId` is keyed by (organization, branch, SourceType, SourceLineId)
/// (`stock_movements_source_line_uk`). With <c>ignoreDuplicateSourceLine</c> a second delivery of the same line is a
/// silent no-op (returns false) - the contract the branch -> cloud sale projection relies on.
/// </para>
/// </summary>
public static class StockMovementWriter
{
    /// <returns>true when the movement was appended; false only when it was a duplicate source line and duplicates are ignored.</returns>
    public static async Task<bool> InsertAsync(
        NpgsqlConnection connection, NpgsqlTransaction tx, NewStockMovement movement, CancellationToken ct,
        bool ignoreDuplicateSourceLine = false)
    {
        var conflict = ignoreDuplicateSourceLine
            ? "ON CONFLICT (organization_id, branch_id, source_type, source_line_id) WHERE source_line_id IS NOT NULL DO NOTHING"
            : string.Empty;

        await using var cmd = new NpgsqlCommand(
            $"""
            INSERT INTO stock_movements
                (id, organization_id, branch_id, presentation_id, quantity, kind, source_type, source_id, source_line_id,
                 reverses_movement_id, reason, lot_code, occurred_at_utc, created_by_user_id)
            VALUES ($1, $2, $3, $4, $5, $6, $7, $8, $9, $10, $11, $12, COALESCE($14, now()), $13)
            {conflict}
            """, connection, tx);
        cmd.Parameters.AddWithValue(movement.Id);
        cmd.Parameters.AddWithValue(movement.OrganizationId);
        cmd.Parameters.AddWithValue(movement.BranchId);
        cmd.Parameters.AddWithValue(movement.PresentationId);
        cmd.Parameters.AddWithValue(movement.Quantity);
        cmd.Parameters.AddWithValue(movement.Kind.ToString());
        cmd.Parameters.AddWithValue(NpgsqlTypes.NpgsqlDbType.Text, (object?)movement.SourceType ?? DBNull.Value);
        cmd.Parameters.AddWithValue(NpgsqlTypes.NpgsqlDbType.Uuid, (object?)movement.SourceId ?? DBNull.Value);
        cmd.Parameters.AddWithValue(NpgsqlTypes.NpgsqlDbType.Uuid, (object?)movement.SourceLineId ?? DBNull.Value);
        cmd.Parameters.AddWithValue(NpgsqlTypes.NpgsqlDbType.Uuid, (object?)movement.ReversesMovementId ?? DBNull.Value);
        cmd.Parameters.AddWithValue(NpgsqlTypes.NpgsqlDbType.Text, (object?)movement.Reason ?? DBNull.Value);
        cmd.Parameters.AddWithValue(NpgsqlTypes.NpgsqlDbType.Text, (object?)movement.LotCode ?? DBNull.Value);
        cmd.Parameters.AddWithValue(NpgsqlTypes.NpgsqlDbType.Uuid, (object?)movement.CreatedByUserId ?? DBNull.Value);
        cmd.Parameters.AddWithValue(NpgsqlTypes.NpgsqlDbType.TimestampTz, (object?)movement.OccurredAtUtc ?? DBNull.Value);
        return await cmd.ExecuteNonQueryAsync(ct) == 1;
    }
}
