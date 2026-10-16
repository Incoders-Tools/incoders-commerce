using Commerce.Cloud.Api.Tenancy;
using Npgsql;

namespace Commerce.Cloud.Api.Persistence;

/// <summary>
/// Shared per-request Postgres GUC setup (B7 U1 design note: "extract a
/// shared helper ... ~12 call sites"). Every store that scopes a connection
/// to a tenant used to hand-roll its own
/// <c>SELECT set_config('app.current_org_id', $1, true)</c> call before this
/// helper existed — now every one of those call sites goes through here
/// instead, so the GUC shape only needs to change in one place.
///
/// Always sets <c>app.current_org_id</c>. Additionally sets
/// <c>app.current_branch_id</c> when a branch is selected
/// (<see cref="CloudTenantScope.BranchId"/> is non-null) — <c>set_config(...,
/// true)</c> scopes both to the transaction (<c>SET LOCAL</c> semantics), so
/// a caller that never selects a branch leaves <c>app.current_branch_id</c>
/// exactly as unset as before this helper existed. No RLS policy reads that
/// GUC yet — each branch-owned module's own migration adds its policy
/// (design.md, B7 U1 note: "without changing any RLS policy yet").
/// </summary>
public static class TenantScopeSql
{
    public static Task ApplyAsync(NpgsqlConnection connection, NpgsqlTransaction tx, CloudTenantScope scope, CancellationToken ct) =>
        ApplyAsync(connection, tx, scope.OrganizationId, scope.BranchId, ct);

    public static async Task ApplyAsync(
        NpgsqlConnection connection, NpgsqlTransaction tx, Guid organizationId, Guid? branchId, CancellationToken ct)
    {
        await using (var orgCmd = new NpgsqlCommand("SELECT set_config('app.current_org_id', $1, true)", connection, tx))
        {
            orgCmd.Parameters.AddWithValue(organizationId.ToString());
            await orgCmd.ExecuteNonQueryAsync(ct);
        }

        if (branchId is { } selectedBranchId)
        {
            await using var branchCmd = new NpgsqlCommand("SELECT set_config('app.current_branch_id', $1, true)", connection, tx);
            branchCmd.Parameters.AddWithValue(selectedBranchId.ToString());
            await branchCmd.ExecuteNonQueryAsync(ct);
        }
    }
}
