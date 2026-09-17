using Npgsql;

namespace Commerce.Cloud.Api.Auditing;

/// <summary>
/// Transaction-participating audit-row writer (commerce-role-taxonomy
/// design.md "Audit write mechanism"), following
/// <c>PostgresUserAccountStore.InsertAsync</c>'s exact convention: the
/// CALLER owns the connection, the transaction, and `set_config`. This
/// method never commits, never rolls back, and never calls `set_config`
/// itself — "same transaction as the mutating write" is structural, not a
/// convention the caller can forget: the audit row and the mutation commit
/// together or not at all. The insert uses no `RETURNING` — `app_runtime`
/// has no SELECT privilege on `audit_log` (append-only by design).
/// </summary>
public static class AuditLogWriter
{
    public static async Task InsertAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        UserManagementAuditEntry entry,
        CancellationToken ct)
    {
        await using var cmd = new NpgsqlCommand(
            """
            INSERT INTO audit_log
                (actor_kind, actor_id, organization_id, entity_type, entity_id, action, old_value, new_value)
            VALUES ($1, $2, $3, $4, $5, $6, $7::jsonb, $8::jsonb)
            """, connection, transaction);

        cmd.Parameters.AddWithValue(entry.ActorKind);
        cmd.Parameters.AddWithValue(entry.ActorId);
        cmd.Parameters.AddWithValue((object?)entry.OrganizationId ?? DBNull.Value);
        cmd.Parameters.AddWithValue(entry.EntityType);
        cmd.Parameters.AddWithValue(entry.EntityId);
        cmd.Parameters.AddWithValue(entry.Action);
        cmd.Parameters.AddWithValue((object?)entry.OldValueJson ?? DBNull.Value);
        cmd.Parameters.AddWithValue((object?)entry.NewValueJson ?? DBNull.Value);

        await cmd.ExecuteNonQueryAsync(ct);
    }
}
