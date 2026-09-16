using Commerce.Cloud.Api.Tenancy;
using Npgsql;

namespace Commerce.Cloud.Api.Persistence;

/// <summary>
/// Owns the SINGLE bootstrap transaction that creates an organization, its
/// one default branch, and the first admin user together (design.md
/// "Transaction composition (the core decision)"). Mirrors
/// <see cref="PostgresCloudInboxStore"/>'s shape: raw
/// <see cref="NpgsqlDataSource"/>, `set_config` is always the FIRST statement
/// in the transaction.
///
/// Order: set_config -> org-exists guard -> zero-users guard -> INSERT
/// organizations -> INSERT branches -> userStore.InsertAsync (same
/// connection, same transaction) -> COMMIT. No explicit rollback code is
/// needed: on any exception the `await using` transaction disposes without
/// committing, and Postgres aborts the whole transaction on the first error
/// — a mid-flow failure can never commit a prefix (design.md "Atomicity
/// argument").
/// </summary>
public sealed class PostgresOrganizationStore
{
    private readonly NpgsqlDataSource _dataSource;
    private readonly PostgresUserAccountStore _userStore;

    public PostgresOrganizationStore(NpgsqlDataSource dataSource, PostgresUserAccountStore userStore)
    {
        _dataSource = dataSource;
        _userStore = userStore;
    }

    public async Task<BootstrapOutcome> TryCreateBootstrapAsync(
        CloudTenantScope scope, NewOrganization organization, NewBranch branch, NewUserAccount admin, CancellationToken ct)
    {
        await using var connection = await _dataSource.OpenConnectionAsync(ct);
        await using var tx = await connection.BeginTransactionAsync(ct);

        await using (var scopeCmd = new NpgsqlCommand(
            "SELECT set_config('app.current_org_id', $1, true)", connection, tx))
        {
            scopeCmd.Parameters.AddWithValue(scope.OrganizationId.ToString());
            await scopeCmd.ExecuteNonQueryAsync(ct);
        }

        // Reject-if-org-exists (design.md): checked INSIDE the write
        // transaction, immediately after set_config, before any insert.
        await using (var orgExistsCmd = new NpgsqlCommand(
            "SELECT EXISTS (SELECT 1 FROM organizations WHERE id = $1)", connection, tx))
        {
            orgExistsCmd.Parameters.AddWithValue(organization.Id);
            var orgAlreadyExists = (bool)(await orgExistsCmd.ExecuteScalarAsync(ct))!;
            if (orgAlreadyExists)
            {
                await tx.RollbackAsync(ct);
                return BootstrapOutcome.OrganizationAlreadyExists;
            }
        }

        // Pre-existing zero-users guard (kept exactly as TryCreateAsync
        // already runs it for users), re-checked in the SAME transaction to
        // close the TOCTOU window against a concurrent bootstrap.
        await using (var usersExistCmd = new NpgsqlCommand("SELECT EXISTS (SELECT 1 FROM users)", connection, tx))
        {
            var alreadyHasUsers = (bool)(await usersExistCmd.ExecuteScalarAsync(ct))!;
            if (alreadyHasUsers)
            {
                await tx.RollbackAsync(ct);
                return BootstrapOutcome.OrganizationAlreadyHasUsers;
            }
        }

        await using (var insertOrgCmd = new NpgsqlCommand(
            "INSERT INTO organizations (id, name) VALUES ($1, $2)", connection, tx))
        {
            insertOrgCmd.Parameters.AddWithValue(organization.Id);
            insertOrgCmd.Parameters.AddWithValue(organization.Name);
            await insertOrgCmd.ExecuteNonQueryAsync(ct);
        }

        await using (var insertBranchCmd = new NpgsqlCommand(
            "INSERT INTO branches (id, organization_id, name) VALUES ($1, $2, $3)", connection, tx))
        {
            insertBranchCmd.Parameters.AddWithValue(branch.Id);
            insertBranchCmd.Parameters.AddWithValue(organization.Id);
            insertBranchCmd.Parameters.AddWithValue(branch.Name);
            await insertBranchCmd.ExecuteNonQueryAsync(ct);
        }

        try
        {
            // Same connection, same transaction: PostgresUserAccountStore
            // does not own this transaction and never calls set_config or
            // commits it (design.md "Transaction composition"). If this
            // throws (e.g. a unique violation on user_directory's email
            // primary key because the email is already registered to a
            // DIFFERENT organization), the organization+branch rows already
            // inserted above are rolled back too — Postgres aborts the
            // entire transaction on first error, so no orphaned
            // organization/branch row is representable.
            await _userStore.InsertAsync(connection, tx, scope, admin, ct);
        }
        catch (PostgresException ex) when (ex.SqlState == PostgresErrorCodes.UniqueViolation)
        {
            await tx.RollbackAsync(ct);
            return BootstrapOutcome.EmailAlreadyRegistered;
        }

        await tx.CommitAsync(ct);
        return BootstrapOutcome.Created;
    }
}
