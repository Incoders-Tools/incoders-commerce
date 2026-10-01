using Commerce.Cloud.Api.Auditing;
using Commerce.Cloud.Api.Tenancy;
using Commerce.Domain.Tenancy;
using Npgsql;

namespace Commerce.Cloud.Api.Persistence;

/// <summary>What a paired terminal needs to label itself: its branch and its register.</summary>
public sealed record TerminalIdentity(string BranchName, int BranchCode, int RegisterNumber);

/// <summary>The device credential behind an identity request was revoked (the terminal re-paired) while the request ran.</summary>
public sealed class DeviceCredentialNotLiveException : Exception
{
    public DeviceCredentialNotLiveException() : base("The device credential is no longer live.") { }
}

/// <summary>
/// Register numbers of POS terminals (pos-installation-identity "Register
/// Number"). Allocation is ONE database function, `terminal_registers_assign`
/// (0022): <see cref="AssignAsync"/> is what `PostgresDeviceCredentialStore.IssueAsync`
/// calls inside the pairing transaction, and
/// <see cref="GetIdentityAsync"/> calls the same function for a terminal that
/// was paired before registers existed (or whose number was never stored).
/// </summary>
public sealed class PostgresTerminalRegisterStore
{
    private readonly NpgsqlDataSource _dataSource;

    public PostgresTerminalRegisterStore(NpgsqlDataSource dataSource) => _dataSource = dataSource;

    /// <summary>
    /// Idempotent per (branch, installation): returns the live number, re-activates
    /// the one the installation held in this branch before, or allocates the
    /// next never-used one. The caller's transaction must already carry the
    /// tenant scope of <paramref name="organizationId"/>. A number that is
    /// NEWLY allocated is audited (`terminal.register.assigned`) in the same
    /// transaction so a burst of allocations is visible; the actor is whoever
    /// triggered it (the pairing user, or the device itself).
    /// </summary>
    /// <param name="releaseOthers">True ONLY for pairing: frees the installation's live register elsewhere.
    /// False for the identity refresh, which never releases and returns null when the credential is no longer live.</param>
    /// <returns>The register number, or null when <paramref name="releaseOthers"/> is false and no live credential binds the installation to the branch.</returns>
    /// <exception cref="RegisterNumbersExhaustedException">The branch used up 1..999.</exception>
    internal static async Task<int?> AssignAsync(
        NpgsqlConnection connection, NpgsqlTransaction tx, Guid organizationId, Guid branchId, Guid installationId,
        bool releaseOthers, string actorKind, Guid actorId, CancellationToken ct)
    {
        short? number;
        await using (var cmd = new NpgsqlCommand("SELECT terminal_registers_assign($1, $2, $3, $4)", connection, tx))
        {
            cmd.Parameters.AddWithValue(organizationId);
            cmd.Parameters.AddWithValue(branchId);
            cmd.Parameters.AddWithValue(installationId);
            cmd.Parameters.AddWithValue(releaseOthers);
            try
            {
                var scalar = await cmd.ExecuteScalarAsync(ct);
                number = scalar is short value ? value : null;
            }
            catch (PostgresException ex) when (ex.SqlState == PostgresErrorCodes.CheckViolation && ex.ConstraintName == "terminal_registers_number_ck")
            {
                throw new RegisterNumbersExhaustedException();
            }
        }
        if (number is null) return null;

        // A row stamped by THIS transaction (assigned_at defaults to now(), the
        // transaction start) is a new allocation; a re-activation or an
        // already-live number is not audited.
        await using (var freshCmd = new NpgsqlCommand(
            """
            SELECT assigned_at = now() FROM terminal_registers
             WHERE organization_id = $1 AND branch_id = $2 AND installation_id = $3
            """, connection, tx))
        {
            freshCmd.Parameters.AddWithValue(organizationId);
            freshCmd.Parameters.AddWithValue(branchId);
            freshCmd.Parameters.AddWithValue(installationId);
            if (await freshCmd.ExecuteScalarAsync(ct) is true)
            {
                await AuditLogWriter.InsertAsync(connection, tx, new UserManagementAuditEntry(
                    actorKind, actorId, organizationId, "terminal-register", installationId, "terminal.register.assigned",
                    null, $"{{\"branchId\":\"{branchId}\",\"registerNumber\":{number}}}"), ct);
            }
        }
        return number;
    }

    /// <summary>
    /// The identity of the terminal behind a device credential: branch name and
    /// code read from the credential's own branch, plus its register number
    /// (allocated now when missing). Null when the branch is not visible in the scope.
    /// This path NEVER releases a register: inside its own transaction it
    /// re-verifies that a live credential still binds the installation to
    /// <paramref name="branchId"/> (the bearer was only checked at request
    /// start) and only fills in a missing number for that branch.
    /// </summary>
    /// <exception cref="DeviceCredentialNotLiveException">A re-pairing revoked the credential meanwhile.</exception>
    public async Task<TerminalIdentity?> GetIdentityAsync(CloudTenantScope scope, Guid branchId, Guid installationId, CancellationToken ct)
    {
        await using var connection = await _dataSource.OpenConnectionAsync(ct);
        await using var tx = await connection.BeginTransactionAsync(ct);
        await TenantScopeSql.ApplyAsync(connection, tx, scope, ct);

        string? name = null;
        short code = 0;
        await using (var branchCmd = new NpgsqlCommand("SELECT name, code FROM branches WHERE id = $1", connection, tx))
        {
            branchCmd.Parameters.AddWithValue(branchId);
            await using var reader = await branchCmd.ExecuteReaderAsync(ct);
            if (await reader.ReadAsync(ct))
            {
                name = reader.GetString(0);
                code = reader.GetInt16(1);
            }
        }
        if (name is null) return null;

        var register = await AssignAsync(
            connection, tx, scope.OrganizationId, branchId, installationId, releaseOthers: false, "device", installationId, ct)
            ?? throw new DeviceCredentialNotLiveException();
        await tx.CommitAsync(ct);
        return new TerminalIdentity(name, code, register);
    }
}
