using Microsoft.Extensions.Diagnostics.HealthChecks;
using Npgsql;

namespace Commerce.Cloud.Api.HealthChecks;

/// <summary>
/// Readiness check (design.md "Schema/RLS application"): Cloud.Api VERIFIES
/// the schema/RLS shape at readiness — it never applies DDL at runtime.
/// Fails `/health/ready` when the database is unreachable, the `sync_inbox`
/// table is missing, RLS is not forced, or the tenant-isolation policy is
/// missing. `/health` (liveness) has no DB dependency and is a separate,
/// always-succeeding check.
/// </summary>
public sealed class PostgresReadinessHealthCheck : IHealthCheck
{
    private readonly NpgsqlDataSource _dataSource;

    public PostgresReadinessHealthCheck(NpgsqlDataSource dataSource) => _dataSource = dataSource;

    public async Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken cancellationToken = default)
    {
        try
        {
            await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken);

            await using var cmd = new NpgsqlCommand(
                """
                SELECT
                    EXISTS (SELECT 1 FROM pg_tables WHERE tablename = 'sync_inbox') AS table_exists,
                    EXISTS (
                        SELECT 1 FROM pg_class
                        WHERE relname = 'sync_inbox' AND relrowsecurity AND relforcerowsecurity
                    ) AS rls_forced,
                    EXISTS (
                        SELECT 1 FROM pg_policies
                        WHERE tablename = 'sync_inbox' AND policyname = 'sync_inbox_tenant_isolation'
                    ) AS policy_exists,
                    EXISTS (SELECT 1 FROM pg_roles WHERE rolname = 'app_runtime') AS role_exists
                """, connection);

            await using var reader = await cmd.ExecuteReaderAsync(cancellationToken);
            if (!await reader.ReadAsync(cancellationToken))
            {
                return HealthCheckResult.Unhealthy("Readiness query returned no row.");
            }

            var tableExists = reader.GetBoolean(0);
            var rlsForced = reader.GetBoolean(1);
            var policyExists = reader.GetBoolean(2);
            var roleExists = reader.GetBoolean(3);

            if (tableExists && rlsForced && policyExists && roleExists)
            {
                return HealthCheckResult.Healthy("sync_inbox table, forced RLS, tenant-isolation policy, and app_runtime role all verified.");
            }

            return HealthCheckResult.Unhealthy(
                $"Schema/RLS verification failed: table_exists={tableExists}, rls_forced={rlsForced}, " +
                $"policy_exists={policyExists}, role_exists={roleExists}. Apply deploy/db/migrations/0001_init_rls.sql.");
        }
        catch (Exception ex)
        {
            return HealthCheckResult.Unhealthy("Database unreachable.", ex);
        }
    }
}
