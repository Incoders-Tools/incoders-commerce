using Commerce.Cloud.Api.Auditing;
using Commerce.Cloud.Api.Tenancy;
using Npgsql;
using Microsoft.Extensions.DependencyInjection;
using Commerce.Cloud.Api.Endpoints;

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
    private readonly NpgsqlDataSource? _platformReadDataSource;
    public const string PlatformReadDataSourceKey = "platform-read";

    public PostgresOrganizationStore(NpgsqlDataSource dataSource, PostgresUserAccountStore userStore, [FromKeyedServices(PlatformReadDataSourceKey)] NpgsqlDataSource? platformReadDataSource = null)
    {
        _dataSource = dataSource;
        _userStore = userStore;
        _platformReadDataSource = platformReadDataSource;
    }

    public Task<BootstrapOutcome> TryCreateBootstrapAsync(
        CloudTenantScope scope, NewOrganization organization, NewBranch branch, NewUserAccount admin, CancellationToken ct) =>
        TryCreateBootstrapAsync(scope, organization, branch, admin, audit: null, ct);

    /// <summary>
    /// SQL otherwise unchanged from the original overload (design.md "File
    /// Changes"): <paramref name="audit"/> is optional so the anonymous
    /// `/account/bootstrap` flow keeps zero behavior change, while
    /// `POST /account/organizations` supplies a `system-admin` audit row
    /// written inside this SAME transaction — the audit row and the
    /// created organization/branch/admin commit together or not at all.
    /// </summary>
    public async Task<BootstrapOutcome> TryCreateBootstrapAsync(
        CloudTenantScope scope, NewOrganization organization, NewBranch branch, NewUserAccount admin,
        UserManagementAuditEntry? audit, CancellationToken ct)
    {
        await using var connection = await _dataSource.OpenConnectionAsync(ct);
        await using var tx = await connection.BeginTransactionAsync(ct);

        await TenantScopeSql.ApplyAsync(connection, tx, scope, ct);

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

        if (audit is not null)
        {
            await AuditLogWriter.InsertAsync(connection, tx, audit, ct);
        }

        await tx.CommitAsync(ct);
        return BootstrapOutcome.Created;
    }

    /// <summary>
    /// Org-scoped, ordered lookup of branches by id (design.md "File
    /// Changes"). Used by `POST /device/pair` to resolve the operator's
    /// `branch_scope` guids into display names — a caller-submitted branch id
    /// outside this list is never trusted, so the caller must intersect
    /// against `branch_scope` BEFORE calling this.
    /// </summary>
    public async Task<IReadOnlyList<BranchOption>> ListBranchesAsync(CloudTenantScope scope, Guid[] branchIds, CancellationToken ct)
    {
        await using var connection = await _dataSource.OpenConnectionAsync(ct);
        await using var tx = await connection.BeginTransactionAsync(ct);

        await TenantScopeSql.ApplyAsync(connection, tx, scope, ct);

        var results = new List<BranchOption>();
        await using (var cmd = new NpgsqlCommand(
            "SELECT id, name FROM branches WHERE id = ANY($1) ORDER BY name", connection, tx))
        {
            cmd.Parameters.AddWithValue(branchIds);
            await using var reader = await cmd.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct))
            {
                results.Add(new BranchOption(reader.GetGuid(0), reader.GetString(1)));
            }
        }

        await tx.CommitAsync(ct);
        return results;
    }
    public Task CreateBranchAsync(CloudTenantScope scope, NewBranch branch, CancellationToken ct) =>
        CreateBranchAsync(scope, branch, audit: null, ct);

    /// <summary>
    /// <paramref name="audit"/> is written in the SAME transaction as the
    /// branch insert (platform-administration spec "Sysadmin Acts On A
    /// Selected Organization": "every write ... is audited"). Every
    /// existing caller keeps passing <c>null</c> (no behavior change for a
    /// same-org caller); only the sysadmin-acting-on-a-selected-organization
    /// path (`POST /account/branches` under
    /// <see cref="Tenancy.CloudTenantScope.IsActingOnSelectedOrganization"/>)
    /// supplies one.
    /// </summary>
    public async Task CreateBranchAsync(CloudTenantScope scope, NewBranch branch, Auditing.UserManagementAuditEntry? audit, CancellationToken ct)
    {
        await using var connection = await _dataSource.OpenConnectionAsync(ct);
        await using var tx = await connection.BeginTransactionAsync(ct);
        await TenantScopeSql.ApplyAsync(connection, tx, scope, ct);
        await using (var cmd = new NpgsqlCommand("INSERT INTO branches (id, organization_id, name) VALUES ($1, $2, $3)", connection, tx))
        {
            cmd.Parameters.AddWithValue(branch.Id); cmd.Parameters.AddWithValue(scope.OrganizationId); cmd.Parameters.AddWithValue(branch.Name);
            await cmd.ExecuteNonQueryAsync(ct);
        }
        if (audit is not null)
        {
            await Auditing.AuditLogWriter.InsertAsync(connection, tx, audit, ct);
        }
        await tx.CommitAsync(ct);
    }

    public async Task<IReadOnlyList<BranchOption>> ListBranchesAsync(CloudTenantScope scope, CancellationToken ct)
    {
        await using var connection = await _dataSource.OpenConnectionAsync(ct);
        await using var tx = await connection.BeginTransactionAsync(ct);
        await TenantScopeSql.ApplyAsync(connection, tx, scope, ct);
        var branches = new List<BranchOption>();
        await using var cmd = new NpgsqlCommand("SELECT id, name FROM branches ORDER BY name", connection, tx);
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct)) branches.Add(new BranchOption(reader.GetGuid(0), reader.GetString(1)));
        await reader.CloseAsync();
        await tx.CommitAsync(ct);
        return branches;
    }
    public bool CanListOrganizations => _platformReadDataSource is not null;

    public async Task<IReadOnlyList<OrganizationSummary>> ListOrganizationsAsync(CancellationToken ct)
    {
        if (_platformReadDataSource is null) throw new InvalidOperationException("platform_readonly datasource is not configured.");
        await using var connection = await _platformReadDataSource.OpenConnectionAsync(ct);
        await using var cmd = new NpgsqlCommand("SELECT id, name, created_at FROM organizations ORDER BY name", connection);
        var organizations = new List<OrganizationSummary>();
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct)) organizations.Add(new OrganizationSummary(reader.GetGuid(0), reader.GetString(1), reader.GetFieldValue<DateTimeOffset>(2)));
        return organizations;
    }

    /// <summary>
    /// Existence check for a caller-submitted organization id (platform-
    /// administration spec "Sysadmin Acts On A Selected Organization"):
    /// used ONLY by <see cref="Tenancy.TenantScopeEndpointFilter"/> to decide
    /// whether a system administrator's selected-organization header names a
    /// real organization before honoring it. Same `set_config` +
    /// scoped-read pattern as <see cref="GetBrandingAsync"/> — RLS still
    /// applies, this never bypasses it.
    /// </summary>
    public async Task<bool> OrganizationExistsAsync(Guid organizationId, CancellationToken ct)
    {
        await using var connection = await _dataSource.OpenConnectionAsync(ct);
        await using var tx = await connection.BeginTransactionAsync(ct);

        await TenantScopeSql.ApplyAsync(connection, tx, organizationId, branchId: null, ct);

        await using var cmd = new NpgsqlCommand("SELECT EXISTS (SELECT 1 FROM organizations WHERE id = $1)", connection, tx);
        cmd.Parameters.AddWithValue(organizationId);
        var exists = (bool)(await cmd.ExecuteScalarAsync(ct))!;

        await tx.CommitAsync(ct);
        return exists;
    }

    /// <summary>
    /// Reads one organization's branding (T5a, organization-persistence spec
    /// "Organization Branding Fields"). <paramref name="organizationId"/> is
    /// ALWAYS a value the CALLER already trusts — either a system-admin-gated
    /// route parameter (the endpoint checks `IsSystemAdmin` before calling
    /// this) or the authenticated caller's own
    /// <see cref="Tenancy.CloudTenantScope.OrganizationId"/> for the
    /// "my organization" endpoint. This method never re-derives or validates
    /// that trust itself — RLS scopes strictly to whatever id `set_config`
    /// receives here, so the caller is what keeps it trustworthy. Returns
    /// <c>null</c> when no organization with that id exists.
    /// </summary>
    public async Task<OrganizationBranding?> GetBrandingAsync(Guid organizationId, CancellationToken ct)
    {
        await using var connection = await _dataSource.OpenConnectionAsync(ct);
        await using var tx = await connection.BeginTransactionAsync(ct);

        await TenantScopeSql.ApplyAsync(connection, tx, organizationId, branchId: null, ct);

        OrganizationBranding? branding = null;
        await using (var cmd = new NpgsqlCommand("SELECT logo_url, primary_color FROM organizations WHERE id = $1", connection, tx))
        {
            cmd.Parameters.AddWithValue(organizationId);
            await using var reader = await cmd.ExecuteReaderAsync(ct);
            if (await reader.ReadAsync(ct))
            {
                branding = new OrganizationBranding(
                    reader.IsDBNull(0) ? null : reader.GetString(0),
                    reader.IsDBNull(1) ? null : reader.GetString(1));
            }
        }

        await tx.CommitAsync(ct);
        return branding;
    }

    /// <summary>
    /// Updates one organization's branding and writes the audit row in the
    /// SAME transaction (same convention as
    /// <see cref="TryCreateBootstrapAsync"/>'s optional audit parameter) —
    /// they commit together or not at all. Same trust note as
    /// <see cref="GetBrandingAsync"/>: the caller (the system-admin-gated
    /// endpoint) is what makes <paramref name="organizationId"/> safe to use.
    /// Returns <c>false</c> when no organization with that id exists, in
    /// which case nothing — including the audit row — is written.
    /// </summary>
    public async Task<bool> UpdateBrandingAsync(
        Guid organizationId, string? logoUrl, string? primaryColor, UserManagementAuditEntry audit, CancellationToken ct)
    {
        await using var connection = await _dataSource.OpenConnectionAsync(ct);
        await using var tx = await connection.BeginTransactionAsync(ct);

        await TenantScopeSql.ApplyAsync(connection, tx, organizationId, branchId: null, ct);

        int rowsAffected;
        await using (var cmd = new NpgsqlCommand(
            "UPDATE organizations SET logo_url = $1, primary_color = $2 WHERE id = $3", connection, tx))
        {
            cmd.Parameters.AddWithValue((object?)logoUrl ?? DBNull.Value);
            cmd.Parameters.AddWithValue((object?)primaryColor ?? DBNull.Value);
            cmd.Parameters.AddWithValue(organizationId);
            rowsAffected = await cmd.ExecuteNonQueryAsync(ct);
        }

        if (rowsAffected == 0)
        {
            await tx.RollbackAsync(ct);
            return false;
        }

        await AuditLogWriter.InsertAsync(connection, tx, audit, ct);
        await tx.CommitAsync(ct);
        return true;
    }
}
