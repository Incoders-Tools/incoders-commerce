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
                    EXISTS (SELECT 1 FROM pg_roles WHERE rolname = 'platform_readonly') AS platform_readonly_role_exists,
                    EXISTS (SELECT 1 FROM pg_tables WHERE tablename = 'customers') AS customers_table_exists,
                    EXISTS (
                        SELECT 1 FROM pg_class
                        WHERE relname = 'customers' AND relrowsecurity AND relforcerowsecurity
                    ) AS customers_rls_forced,
                    EXISTS (
                        SELECT 1 FROM pg_policies
                        WHERE tablename = 'customers' AND policyname = 'customers_tenant_isolation'
                    ) AS customers_policy_exists,
                    EXISTS (SELECT 1 FROM pg_tables WHERE tablename = 'customer_ordering_access') AS customer_ordering_access_table_exists,
                    EXISTS (
                        SELECT 1 FROM pg_class
                        WHERE relname = 'customer_ordering_access' AND relrowsecurity AND relforcerowsecurity
                    ) AS customer_ordering_access_rls_forced,
                    EXISTS (
                        SELECT 1 FROM pg_policies
                        WHERE tablename = 'customer_ordering_access' AND policyname = 'customer_ordering_access_lookup'
                    ) AS customer_ordering_access_lookup_policy_exists,
                    EXISTS (
                        SELECT 1 FROM pg_policies
                        WHERE tablename = 'customer_ordering_access' AND policyname = 'customer_ordering_access_issue'
                    ) AS customer_ordering_access_issue_policy_exists,
                    EXISTS (
                        SELECT 1 FROM pg_policies
                        WHERE tablename = 'customer_ordering_access' AND policyname = 'customer_ordering_access_revoke'
                    ) AS customer_ordering_access_revoke_policy_exists,
                    EXISTS (SELECT 1 FROM pg_tables WHERE tablename = 'products') AS products_table_exists,
                    EXISTS (
                        SELECT 1 FROM pg_class
                        WHERE relname = 'products' AND relrowsecurity AND relforcerowsecurity
                    ) AS products_rls_forced,
                    EXISTS (
                        SELECT 1 FROM pg_policies
                        WHERE tablename = 'products' AND policyname = 'products_tenant_isolation'
                    ) AS products_policy_exists,
                    EXISTS (SELECT 1 FROM pg_tables WHERE tablename = 'presentations') AS presentations_table_exists,
                    EXISTS (
                        SELECT 1 FROM pg_class
                        WHERE relname = 'presentations' AND relrowsecurity AND relforcerowsecurity
                    ) AS presentations_rls_forced,
                    EXISTS (
                        SELECT 1 FROM pg_policies
                        WHERE tablename = 'presentations' AND policyname = 'presentations_tenant_isolation'
                    ) AS presentations_policy_exists,
                    EXISTS (SELECT 1 FROM pg_tables WHERE tablename = 'price_lists') AS price_lists_table_exists,
                    EXISTS (
                        SELECT 1 FROM pg_class
                        WHERE relname = 'price_lists' AND relrowsecurity AND relforcerowsecurity
                    ) AS price_lists_rls_forced,
                    EXISTS (
                        SELECT 1 FROM pg_policies
                        WHERE tablename = 'price_lists' AND policyname = 'price_lists_tenant_isolation'
                    ) AS price_lists_policy_exists,
                    EXISTS (SELECT 1 FROM pg_tables WHERE tablename = 'price_list_entries') AS price_list_entries_table_exists,
                    EXISTS (
                        SELECT 1 FROM pg_class
                        WHERE relname = 'price_list_entries' AND relrowsecurity AND relforcerowsecurity
                    ) AS price_list_entries_rls_forced,
                    EXISTS (
                        SELECT 1 FROM pg_policies
                        WHERE tablename = 'price_list_entries' AND policyname = 'price_list_entries_tenant_isolation'
                    ) AS price_list_entries_policy_exists,
                    EXISTS (SELECT 1 FROM pg_tables WHERE tablename = 'guest_order_verifications') AS guest_order_verifications_table_exists,
                    EXISTS (
                        SELECT 1 FROM pg_class
                        WHERE relname = 'guest_order_verifications' AND relrowsecurity AND relforcerowsecurity
                    ) AS guest_order_verifications_rls_forced,
                    EXISTS (
                        SELECT 1 FROM pg_policies
                        WHERE tablename = 'guest_order_verifications' AND policyname = 'guest_order_verifications_lookup'
                    ) AS guest_order_verifications_lookup_policy_exists,
                    EXISTS (
                        SELECT 1 FROM pg_policies
                        WHERE tablename = 'guest_order_verifications' AND policyname = 'guest_order_verifications_issue'
                    ) AS guest_order_verifications_issue_policy_exists,
                    EXISTS (
                        SELECT 1 FROM pg_policies
                        WHERE tablename = 'guest_order_verifications' AND policyname = 'guest_order_verifications_update'
                    ) AS guest_order_verifications_update_policy_exists
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
            var customersTableExists = reader.GetBoolean(35);
            var customersRlsForced = reader.GetBoolean(36);
            var customersPolicyExists = reader.GetBoolean(37);
            var customerOrderingAccessTableExists = reader.GetBoolean(38);
            var customerOrderingAccessRlsForced = reader.GetBoolean(39);
            var customerOrderingAccessLookupPolicyExists = reader.GetBoolean(40);
            var customerOrderingAccessIssuePolicyExists = reader.GetBoolean(41);
            var customerOrderingAccessRevokePolicyExists = reader.GetBoolean(42);
            var productsTableExists = reader.GetBoolean(43);
            var productsRlsForced = reader.GetBoolean(44);
            var productsPolicyExists = reader.GetBoolean(45);
            var presentationsTableExists = reader.GetBoolean(46);
            var presentationsRlsForced = reader.GetBoolean(47);
            var presentationsPolicyExists = reader.GetBoolean(48);
            var priceListsTableExists = reader.GetBoolean(49);
            var priceListsRlsForced = reader.GetBoolean(50);
            var priceListsPolicyExists = reader.GetBoolean(51);
            var priceListEntriesTableExists = reader.GetBoolean(52);
            var priceListEntriesRlsForced = reader.GetBoolean(53);
            var priceListEntriesPolicyExists = reader.GetBoolean(54);
            var guestOrderVerificationsTableExists = reader.GetBoolean(55);
            var guestOrderVerificationsRlsForced = reader.GetBoolean(56);
            var guestOrderVerificationsLookupPolicyExists = reader.GetBoolean(57);
            var guestOrderVerificationsIssuePolicyExists = reader.GetBoolean(58);
            var guestOrderVerificationsUpdatePolicyExists = reader.GetBoolean(59);

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
                && platformReadonlyRoleExists
                && customersTableExists && customersRlsForced && customersPolicyExists
                && customerOrderingAccessTableExists && customerOrderingAccessRlsForced
                && customerOrderingAccessLookupPolicyExists && customerOrderingAccessIssuePolicyExists
                && customerOrderingAccessRevokePolicyExists
                && productsTableExists && productsRlsForced && productsPolicyExists
                && presentationsTableExists && presentationsRlsForced && presentationsPolicyExists
                && priceListsTableExists && priceListsRlsForced && priceListsPolicyExists
                && priceListEntriesTableExists && priceListEntriesRlsForced && priceListEntriesPolicyExists
                && guestOrderVerificationsTableExists && guestOrderVerificationsRlsForced
                && guestOrderVerificationsLookupPolicyExists && guestOrderVerificationsIssuePolicyExists
                && guestOrderVerificationsUpdatePolicyExists;

            if (allHealthy)
            {
                return HealthCheckResult.Healthy(
                    "sync_inbox, users, user_directory, organizations, branches, device_credentials, " +
                    "password_reset_tokens, platform_admins, audit_log, customers, customer_ordering_access, " +
                    "products, presentations, price_lists, price_list_entries, and guest_order_verifications " +
                    "tables, forced RLS, tenant-isolation policies, and app_runtime/platform_readonly roles all verified.");
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
                $"platform_readonly_role_exists={platformReadonlyRoleExists}, " +
                $"customers(table={customersTableExists}, rls_forced={customersRlsForced}, policy={customersPolicyExists}), " +
                $"customer_ordering_access(table={customerOrderingAccessTableExists}, rls_forced={customerOrderingAccessRlsForced}, " +
                $"lookup_policy={customerOrderingAccessLookupPolicyExists}, issue_policy={customerOrderingAccessIssuePolicyExists}, revoke_policy={customerOrderingAccessRevokePolicyExists}), " +
                $"products(table={productsTableExists}, rls_forced={productsRlsForced}, policy={productsPolicyExists}), " +
                $"presentations(table={presentationsTableExists}, rls_forced={presentationsRlsForced}, policy={presentationsPolicyExists}), " +
                $"price_lists(table={priceListsTableExists}, rls_forced={priceListsRlsForced}, policy={priceListsPolicyExists}), " +
                $"price_list_entries(table={priceListEntriesTableExists}, rls_forced={priceListEntriesRlsForced}, policy={priceListEntriesPolicyExists}), " +
                $"guest_order_verifications(table={guestOrderVerificationsTableExists}, rls_forced={guestOrderVerificationsRlsForced}, " +
                $"lookup_policy={guestOrderVerificationsLookupPolicyExists}, issue_policy={guestOrderVerificationsIssuePolicyExists}, update_policy={guestOrderVerificationsUpdatePolicyExists}). " +
                "Apply deploy/db/migrations/0001_init_rls.sql through 0010_guest_ordering.sql.");
        }
        catch (Exception ex)
        {
            return HealthCheckResult.Unhealthy("Database unreachable.", ex);
        }
    }
}
