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
                    EXISTS (SELECT 1 FROM pg_tables WHERE tablename = 'sync_inbox') AS sync_inbox_table_exists,
                    EXISTS (
                        SELECT 1 FROM pg_class
                        WHERE relname = 'sync_inbox' AND relrowsecurity AND relforcerowsecurity
                    ) AS sync_inbox_rls_forced,
                    EXISTS (
                        SELECT 1 FROM pg_policies
                        WHERE tablename = 'sync_inbox' AND policyname = 'sync_inbox_tenant_isolation'
                    ) AS sync_inbox_policy_exists,
                    EXISTS (SELECT 1 FROM pg_roles WHERE rolname = 'app_runtime') AS role_exists,
                    EXISTS (SELECT 1 FROM pg_tables WHERE tablename = 'users') AS users_table_exists,
                    EXISTS (
                        SELECT 1 FROM pg_class
                        WHERE relname = 'users' AND relrowsecurity AND relforcerowsecurity
                    ) AS users_rls_forced,
                    EXISTS (
                        SELECT 1 FROM pg_policies
                        WHERE tablename = 'users' AND policyname = 'users_tenant_isolation'
                    ) AS users_policy_exists,
                    EXISTS (SELECT 1 FROM pg_tables WHERE tablename = 'user_directory') AS user_directory_table_exists,
                    EXISTS (
                        SELECT 1 FROM pg_class
                        WHERE relname = 'user_directory' AND relrowsecurity AND relforcerowsecurity
                    ) AS user_directory_rls_forced,
                    EXISTS (
                        SELECT 1 FROM pg_policies
                        WHERE tablename = 'user_directory' AND policyname = 'user_directory_lookup'
                    ) AS user_directory_policy_exists,
                    EXISTS (SELECT 1 FROM pg_tables WHERE tablename = 'organizations') AS organizations_table_exists,
                    EXISTS (
                        SELECT 1 FROM pg_class
                        WHERE relname = 'organizations' AND relrowsecurity AND relforcerowsecurity
                    ) AS organizations_rls_forced,
                    EXISTS (
                        SELECT 1 FROM pg_policies
                        WHERE tablename = 'organizations' AND policyname = 'organizations_tenant_isolation'
                    ) AS organizations_policy_exists,
                    EXISTS (SELECT 1 FROM pg_tables WHERE tablename = 'branches') AS branches_table_exists,
                    EXISTS (
                        SELECT 1 FROM pg_class
                        WHERE relname = 'branches' AND relrowsecurity AND relforcerowsecurity
                    ) AS branches_rls_forced,
                    EXISTS (
                        SELECT 1 FROM pg_policies
                        WHERE tablename = 'branches' AND policyname = 'branches_tenant_isolation'
                    ) AS branches_policy_exists,
                    EXISTS (SELECT 1 FROM pg_tables WHERE tablename = 'device_credentials') AS device_credentials_table_exists,
                    EXISTS (
                        SELECT 1 FROM pg_class
                        WHERE relname = 'device_credentials' AND relrowsecurity AND relforcerowsecurity
                    ) AS device_credentials_rls_forced,
                    EXISTS (
                        SELECT 1 FROM pg_policies
                        WHERE tablename = 'device_credentials' AND policyname = 'device_credentials_lookup'
                    ) AS device_credentials_lookup_policy_exists,
                    EXISTS (
                        SELECT 1 FROM pg_policies
                        WHERE tablename = 'device_credentials' AND policyname = 'device_credentials_issue'
                    ) AS device_credentials_issue_policy_exists,
                    EXISTS (
                        SELECT 1 FROM pg_policies
                        WHERE tablename = 'device_credentials' AND policyname = 'device_credentials_revoke'
                    ) AS device_credentials_revoke_policy_exists,
                    EXISTS (SELECT 1 FROM pg_tables WHERE tablename = 'password_reset_tokens') AS password_reset_tokens_table_exists,
                    EXISTS (
                        SELECT 1 FROM pg_class
                        WHERE relname = 'password_reset_tokens' AND relrowsecurity AND relforcerowsecurity
                    ) AS password_reset_tokens_rls_forced,
                    EXISTS (
                        SELECT 1 FROM pg_policies
                        WHERE tablename = 'password_reset_tokens' AND policyname = 'password_reset_tokens_lookup'
                    ) AS password_reset_tokens_lookup_policy_exists,
                    EXISTS (
                        SELECT 1 FROM pg_policies
                        WHERE tablename = 'password_reset_tokens' AND policyname = 'password_reset_tokens_issue'
                    ) AS password_reset_tokens_issue_policy_exists,
                    EXISTS (
                        SELECT 1 FROM pg_policies
                        WHERE tablename = 'password_reset_tokens' AND policyname = 'password_reset_tokens_consume'
                    ) AS password_reset_tokens_consume_policy_exists,
                    EXISTS (SELECT 1 FROM pg_tables WHERE tablename = 'platform_admins') AS platform_admins_table_exists,
                    EXISTS (
                        SELECT 1 FROM pg_class
                        WHERE relname = 'platform_admins' AND relrowsecurity AND relforcerowsecurity
                    ) AS platform_admins_rls_forced,
                    EXISTS (
                        SELECT 1 FROM pg_policies
                        WHERE tablename = 'platform_admins' AND policyname = 'platform_admins_lookup'
                    ) AS platform_admins_lookup_policy_exists,
                    EXISTS (
                        SELECT 1 FROM pg_policies
                        WHERE tablename = 'platform_admins' AND policyname = 'platform_admins_touch'
                    ) AS platform_admins_touch_policy_exists,
                    EXISTS (
                        SELECT 1 FROM pg_policies
                        WHERE tablename = 'platform_admins' AND policyname = 'platform_admins_genesis'
                    ) AS platform_admins_genesis_policy_exists,
                    EXISTS (SELECT 1 FROM pg_tables WHERE tablename = 'audit_log') AS audit_log_table_exists,
                    EXISTS (
                        SELECT 1 FROM pg_class
                        WHERE relname = 'audit_log' AND relrowsecurity AND relforcerowsecurity
                    ) AS audit_log_rls_forced,
                    EXISTS (
                        SELECT 1 FROM pg_policies
                        WHERE tablename = 'audit_log' AND policyname = 'audit_log_append'
                    ) AS audit_log_append_policy_exists,
                    EXISTS (SELECT 1 FROM pg_roles WHERE rolname = 'platform_readonly') AS platform_readonly_role_exists
                """, connection);

            await using var reader = await cmd.ExecuteReaderAsync(cancellationToken);
            if (!await reader.ReadAsync(cancellationToken))
            {
                return HealthCheckResult.Unhealthy("Readiness query returned no row.");
            }

            var syncInboxTableExists = reader.GetBoolean(0);
            var syncInboxRlsForced = reader.GetBoolean(1);
            var syncInboxPolicyExists = reader.GetBoolean(2);
            var roleExists = reader.GetBoolean(3);
            var usersTableExists = reader.GetBoolean(4);
            var usersRlsForced = reader.GetBoolean(5);
            var usersPolicyExists = reader.GetBoolean(6);
            var userDirectoryTableExists = reader.GetBoolean(7);
            var userDirectoryRlsForced = reader.GetBoolean(8);
            var userDirectoryPolicyExists = reader.GetBoolean(9);
            var organizationsTableExists = reader.GetBoolean(10);
            var organizationsRlsForced = reader.GetBoolean(11);
            var organizationsPolicyExists = reader.GetBoolean(12);
            var branchesTableExists = reader.GetBoolean(13);
            var branchesRlsForced = reader.GetBoolean(14);
            var branchesPolicyExists = reader.GetBoolean(15);
            var deviceCredentialsTableExists = reader.GetBoolean(16);
            var deviceCredentialsRlsForced = reader.GetBoolean(17);
            var deviceCredentialsLookupPolicyExists = reader.GetBoolean(18);
            var deviceCredentialsIssuePolicyExists = reader.GetBoolean(19);
            var deviceCredentialsRevokePolicyExists = reader.GetBoolean(20);
            var passwordResetTokensTableExists = reader.GetBoolean(21);
            var passwordResetTokensRlsForced = reader.GetBoolean(22);
            var passwordResetTokensLookupPolicyExists = reader.GetBoolean(23);
            var passwordResetTokensIssuePolicyExists = reader.GetBoolean(24);
            var passwordResetTokensConsumePolicyExists = reader.GetBoolean(25);
            var platformAdminsTableExists = reader.GetBoolean(26);
            var platformAdminsRlsForced = reader.GetBoolean(27);
            var platformAdminsLookupPolicyExists = reader.GetBoolean(28);
            var platformAdminsTouchPolicyExists = reader.GetBoolean(29);
            var platformAdminsGenesisPolicyExists = reader.GetBoolean(30);
            var auditLogTableExists = reader.GetBoolean(31);
            var auditLogRlsForced = reader.GetBoolean(32);
            var auditLogAppendPolicyExists = reader.GetBoolean(33);
            var platformReadonlyRoleExists = reader.GetBoolean(34);

            var allHealthy = syncInboxTableExists && syncInboxRlsForced && syncInboxPolicyExists && roleExists
                && usersTableExists && usersRlsForced && usersPolicyExists
                && userDirectoryTableExists && userDirectoryRlsForced && userDirectoryPolicyExists
                && organizationsTableExists && organizationsRlsForced && organizationsPolicyExists
                && branchesTableExists && branchesRlsForced && branchesPolicyExists
                && deviceCredentialsTableExists && deviceCredentialsRlsForced
                && deviceCredentialsLookupPolicyExists && deviceCredentialsIssuePolicyExists && deviceCredentialsRevokePolicyExists
                && passwordResetTokensTableExists && passwordResetTokensRlsForced
                && passwordResetTokensLookupPolicyExists && passwordResetTokensIssuePolicyExists && passwordResetTokensConsumePolicyExists
                && platformAdminsTableExists && platformAdminsRlsForced
                && platformAdminsLookupPolicyExists && platformAdminsTouchPolicyExists && platformAdminsGenesisPolicyExists
                && auditLogTableExists && auditLogRlsForced && auditLogAppendPolicyExists
                && platformReadonlyRoleExists;

            if (allHealthy)
            {
                return HealthCheckResult.Healthy(
                    "sync_inbox, users, user_directory, organizations, branches, device_credentials, " +
                    "password_reset_tokens, platform_admins, and audit_log tables, forced RLS, tenant-isolation " +
                    "policies, and app_runtime/platform_readonly roles all verified.");
            }

            return HealthCheckResult.Unhealthy(
                $"Schema/RLS verification failed: sync_inbox(table={syncInboxTableExists}, rls_forced={syncInboxRlsForced}, policy={syncInboxPolicyExists}), " +
                $"role_exists={roleExists}, users(table={usersTableExists}, rls_forced={usersRlsForced}, policy={usersPolicyExists}), " +
                $"user_directory(table={userDirectoryTableExists}, rls_forced={userDirectoryRlsForced}, policy={userDirectoryPolicyExists}), " +
                $"organizations(table={organizationsTableExists}, rls_forced={organizationsRlsForced}, policy={organizationsPolicyExists}), " +
                $"branches(table={branchesTableExists}, rls_forced={branchesRlsForced}, policy={branchesPolicyExists}), " +
                $"device_credentials(table={deviceCredentialsTableExists}, rls_forced={deviceCredentialsRlsForced}, " +
                $"lookup_policy={deviceCredentialsLookupPolicyExists}, issue_policy={deviceCredentialsIssuePolicyExists}, revoke_policy={deviceCredentialsRevokePolicyExists}), " +
                $"password_reset_tokens(table={passwordResetTokensTableExists}, rls_forced={passwordResetTokensRlsForced}, " +
                $"lookup_policy={passwordResetTokensLookupPolicyExists}, issue_policy={passwordResetTokensIssuePolicyExists}, consume_policy={passwordResetTokensConsumePolicyExists}), " +
                $"platform_admins(table={platformAdminsTableExists}, rls_forced={platformAdminsRlsForced}, " +
                $"lookup_policy={platformAdminsLookupPolicyExists}, touch_policy={platformAdminsTouchPolicyExists}, genesis_policy={platformAdminsGenesisPolicyExists}), " +
                $"audit_log(table={auditLogTableExists}, rls_forced={auditLogRlsForced}, append_policy={auditLogAppendPolicyExists}), " +
                $"platform_readonly_role_exists={platformReadonlyRoleExists}. " +
                "Apply deploy/db/migrations/0001_init_rls.sql through 0007_platform_administration.sql.");
        }
        catch (Exception ex)
        {
            return HealthCheckResult.Unhealthy("Database unreachable.", ex);
        }
    }
}
