using Commerce.Cloud.Api.Tenancy;
using Commerce.Domain.Tenancy;
using Npgsql;

namespace Commerce.Cloud.Api.Persistence;

/// <summary>What a paired terminal needs to label itself: its branch and its register.</summary>
public sealed record TerminalIdentity(string BranchName, int BranchCode, int RegisterNumber);

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
    /// tenant scope of <paramref name="organizationId"/>.
    /// </summary>
    /// <exception cref="RegisterNumbersExhaustedException">The branch used up 1..999.</exception>
    internal static async Task<int> AssignAsync(
        NpgsqlConnection connection, NpgsqlTransaction tx, Guid organizationId, Guid branchId, Guid installationId, CancellationToken ct)
    {
        await using var cmd = new NpgsqlCommand("SELECT terminal_registers_assign($1, $2, $3)", connection, tx);
        cmd.Parameters.AddWithValue(organizationId);
        cmd.Parameters.AddWithValue(branchId);
        cmd.Parameters.AddWithValue(installationId);
        try
        {
            return (short)(await cmd.ExecuteScalarAsync(ct))!;
        }
        catch (PostgresException ex) when (ex.SqlState == PostgresErrorCodes.CheckViolation && ex.ConstraintName == "terminal_registers_number_ck")
        {
            throw new RegisterNumbersExhaustedException();
        }
    }

    /// <summary>
    /// The identity of the terminal behind a device credential: branch name and
    /// code read from the credential's own branch, plus its register number
    /// (allocated now when missing). Null when the branch is not visible in the scope.
    /// </summary>
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

        var register = await AssignAsync(connection, tx, scope.OrganizationId, branchId, installationId, ct);
        await tx.CommitAsync(ct);
        return new TerminalIdentity(name, code, register);
    }
}
