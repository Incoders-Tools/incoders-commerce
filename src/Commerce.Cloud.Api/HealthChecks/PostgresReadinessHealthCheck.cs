using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Logging;
using Npgsql;

namespace Commerce.Cloud.Api.HealthChecks;

/// <summary>
/// Readiness check (design.md "Schema/RLS application"): Cloud.Api VERIFIES
/// the schema/RLS shape at readiness — it never applies DDL at runtime.
/// Fails `/health/ready` when the database is unreachable, the `sync_inbox`
/// table is missing, RLS is not forced, or the tenant-isolation policy is
/// missing. `/health` (liveness) has no DB dependency and is a separate,
/// always-succeeding check.
///
/// Every table the running API HARD-DEPENDS on belongs in the query below,
/// which is why `rate_component_sets`/`rate_components` were added when
/// commerce-price-composition put composition on the order-pricing path: the
/// gate is what turns "deployed ahead of its migration" from a silent,
/// total ordering outage into an instance that never takes traffic and says
/// which migration to run.
/// </summary>
public sealed class PostgresReadinessHealthCheck : IHealthCheck
{
    private readonly NpgsqlDataSource _dataSource;
    private readonly ILogger<PostgresReadinessHealthCheck>? _logger;

    public PostgresReadinessHealthCheck(NpgsqlDataSource dataSource, ILogger<PostgresReadinessHealthCheck>? logger = null)
    {
        _dataSource = dataSource;
        _logger = logger;
    }

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
                    ) AS guest_order_verifications_update_policy_exists,
                    EXISTS (SELECT 1 FROM pg_tables WHERE tablename = 'payment_entries') AS payment_entries_table_exists,
                    EXISTS (
                        SELECT 1 FROM pg_class
                        WHERE relname = 'payment_entries' AND relrowsecurity AND relforcerowsecurity
                    ) AS payment_entries_rls_forced,
                    EXISTS (
                        SELECT 1 FROM pg_policies
                        WHERE tablename = 'payment_entries' AND policyname = 'payment_entries_tenant_isolation'
                    ) AS payment_entries_policy_exists,
                    -- R4-deploy-order-hard-dependency (commerce-price-composition).
                    -- Order pricing composes rate components on EVERY priced
                    -- line, so an API deployed ahead of migration 0013 fails
                    -- every line with "relation does not exist" — a total
                    -- ordering outage reported as an unexplained denial. These
                    -- two rows make that a red /health/ready instead: the
                    -- instance never takes traffic, and the message below names
                    -- the migration to run.
                    EXISTS (SELECT 1 FROM pg_tables WHERE tablename = 'rate_component_sets') AS rate_component_sets_table_exists,
                    EXISTS (
                        SELECT 1 FROM pg_class
                        WHERE relname = 'rate_component_sets' AND relrowsecurity AND relforcerowsecurity
                    ) AS rate_component_sets_rls_forced,
                    EXISTS (
                        SELECT 1 FROM pg_policies
                        WHERE tablename = 'rate_component_sets' AND policyname = 'rate_component_sets_tenant_isolation'
                    ) AS rate_component_sets_policy_exists,
                    EXISTS (SELECT 1 FROM pg_tables WHERE tablename = 'rate_components') AS rate_components_table_exists,
                    EXISTS (
                        SELECT 1 FROM pg_class
                        WHERE relname = 'rate_components' AND relrowsecurity AND relforcerowsecurity
                    ) AS rate_components_rls_forced,
                    EXISTS (
                        SELECT 1 FROM pg_policies
                        WHERE tablename = 'rate_components' AND policyname = 'rate_components_tenant_isolation'
                    ) AS rate_components_policy_exists,
                    -- Human document numbers (0021/0022). Pairing hard-depends on
                    -- branches.code and on terminal_registers_assign(): an API
                    -- deployed ahead of those migrations would answer every
                    -- POST /device/pair with a 500. Red readiness instead names
                    -- the migration to run.
                    EXISTS (
                        SELECT 1 FROM information_schema.columns
                        WHERE table_name = 'branches' AND column_name = 'code'
                    ) AS branches_code_column_exists,
                    EXISTS (SELECT 1 FROM pg_tables WHERE tablename = 'terminal_registers') AS terminal_registers_table_exists,
                    EXISTS (
                        SELECT 1 FROM pg_class
                        WHERE relname = 'terminal_registers' AND relrowsecurity AND relforcerowsecurity
                    ) AS terminal_registers_rls_forced,
                    EXISTS (
                        SELECT 1 FROM pg_proc
                        WHERE proname = 'terminal_registers_assign' AND pronargs = 4
                          AND 'newly_allocated' = ANY(proargnames)  -- result shape of 0024
                    ) AS terminal_registers_assign_exists,
                    -- Web orders (0025). Every submission writes orders/order_lines;
                    -- an API deployed ahead of the migration would 500 on each order.
                    EXISTS (SELECT 1 FROM pg_tables WHERE tablename = 'orders') AS orders_table_exists,
                    EXISTS (
                        SELECT 1 FROM pg_class
                        WHERE relname = 'orders' AND relrowsecurity AND relforcerowsecurity
                    ) AS orders_rls_forced,
                    EXISTS (
                        SELECT 1 FROM pg_policies
                        WHERE tablename = 'orders' AND policyname = 'orders_tenant_isolation'
                    ) AS orders_policy_exists,
                    EXISTS (SELECT 1 FROM pg_tables WHERE tablename = 'order_lines') AS order_lines_table_exists,
                    EXISTS (
                        SELECT 1 FROM pg_class
                        WHERE relname = 'order_lines' AND relrowsecurity AND relforcerowsecurity
                    ) AS order_lines_rls_forced,
                    EXISTS (
                        SELECT 1 FROM pg_policies
                        WHERE tablename = 'order_lines' AND policyname = 'order_lines_tenant_isolation'
                    ) AS order_lines_policy_exists
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
            var passwordResetTokensConsumePolicyExists = reader.GetBoolean(25);            var auditLogTableExists = reader.GetBoolean(26);
            var auditLogRlsForced = reader.GetBoolean(27);
            var auditLogAppendPolicyExists = reader.GetBoolean(28);
            var platformReadonlyRoleExists = reader.GetBoolean(29);
            var customersTableExists = reader.GetBoolean(30);
            var customersRlsForced = reader.GetBoolean(31);
            var customersPolicyExists = reader.GetBoolean(32);
            var customerOrderingAccessTableExists = reader.GetBoolean(33);
            var customerOrderingAccessRlsForced = reader.GetBoolean(34);
            var customerOrderingAccessLookupPolicyExists = reader.GetBoolean(35);
            var customerOrderingAccessIssuePolicyExists = reader.GetBoolean(36);
            var customerOrderingAccessRevokePolicyExists = reader.GetBoolean(37);
            var productsTableExists = reader.GetBoolean(38);
            var productsRlsForced = reader.GetBoolean(39);
            var productsPolicyExists = reader.GetBoolean(40);
            var presentationsTableExists = reader.GetBoolean(41);
            var presentationsRlsForced = reader.GetBoolean(42);
            var presentationsPolicyExists = reader.GetBoolean(43);
            var priceListsTableExists = reader.GetBoolean(44);
            var priceListsRlsForced = reader.GetBoolean(45);
            var priceListsPolicyExists = reader.GetBoolean(46);
            var priceListEntriesTableExists = reader.GetBoolean(47);
            var priceListEntriesRlsForced = reader.GetBoolean(48);
            var priceListEntriesPolicyExists = reader.GetBoolean(49);
            var guestOrderVerificationsTableExists = reader.GetBoolean(50);
            var guestOrderVerificationsRlsForced = reader.GetBoolean(51);
            var guestOrderVerificationsLookupPolicyExists = reader.GetBoolean(52);
            var guestOrderVerificationsIssuePolicyExists = reader.GetBoolean(53);
            var guestOrderVerificationsUpdatePolicyExists = reader.GetBoolean(54);
            var paymentEntriesTableExists = reader.GetBoolean(55);
            var paymentEntriesRlsForced = reader.GetBoolean(56);
            var paymentEntriesPolicyExists = reader.GetBoolean(57);
            var rateComponentSetsTableExists = reader.GetBoolean(58);
            var rateComponentSetsRlsForced = reader.GetBoolean(59);
            var rateComponentSetsPolicyExists = reader.GetBoolean(60);
            var rateComponentsTableExists = reader.GetBoolean(61);
            var rateComponentsRlsForced = reader.GetBoolean(62);
            var rateComponentsPolicyExists = reader.GetBoolean(63);
            var branchesCodeColumnExists = reader.GetBoolean(64);
            var terminalRegistersTableExists = reader.GetBoolean(65);
            var terminalRegistersRlsForced = reader.GetBoolean(66);
            var terminalRegistersAssignExists = reader.GetBoolean(67);
            var ordersTableExists = reader.GetBoolean(68);
            var ordersRlsForced = reader.GetBoolean(69);
            var ordersPolicyExists = reader.GetBoolean(70);
            var orderLinesTableExists = reader.GetBoolean(71);
            var orderLinesRlsForced = reader.GetBoolean(72);
            var orderLinesPolicyExists = reader.GetBoolean(73);
            var ordersSchemaOk = ordersTableExists && ordersRlsForced && ordersPolicyExists
                && orderLinesTableExists && orderLinesRlsForced && orderLinesPolicyExists;

            var allHealthy = syncInboxTableExists && syncInboxRlsForced && syncInboxPolicyExists && roleExists
                && usersTableExists && usersRlsForced && usersPolicyExists
                && userDirectoryTableExists && userDirectoryRlsForced && userDirectoryPolicyExists
                && organizationsTableExists && organizationsRlsForced && organizationsPolicyExists
                && branchesTableExists && branchesRlsForced && branchesPolicyExists
                && deviceCredentialsTableExists && deviceCredentialsRlsForced
                && deviceCredentialsLookupPolicyExists && deviceCredentialsIssuePolicyExists && deviceCredentialsRevokePolicyExists
                && passwordResetTokensTableExists && passwordResetTokensRlsForced
                && passwordResetTokensLookupPolicyExists && passwordResetTokensIssuePolicyExists && passwordResetTokensConsumePolicyExists

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
                && guestOrderVerificationsUpdatePolicyExists
                && paymentEntriesTableExists && paymentEntriesRlsForced && paymentEntriesPolicyExists
                && rateComponentSetsTableExists && rateComponentSetsRlsForced && rateComponentSetsPolicyExists
                && rateComponentsTableExists && rateComponentsRlsForced && rateComponentsPolicyExists
                && branchesCodeColumnExists && terminalRegistersTableExists && terminalRegistersRlsForced && terminalRegistersAssignExists
                && ordersSchemaOk;

            if (allHealthy)
            {
                return HealthCheckResult.Healthy(
                    "sync_inbox, users, user_directory, organizations, branches, device_credentials, " +
                    "password_reset_tokens, audit_log, customers, customer_ordering_access, " +
                    "products, presentations, price_lists, price_list_entries, guest_order_verifications, " +
                    "payment_entries, rate_component_sets, rate_components, terminal_registers, orders, and order_lines tables, forced RLS, " +
                    "tenant-isolation policies, and app_runtime/platform_readonly roles all verified.");
            }

            if (!(branchesCodeColumnExists && terminalRegistersTableExists && terminalRegistersRlsForced && terminalRegistersAssignExists))
            {
                _logger?.LogError("Readiness failed: migration 0021/0022/0024 missing (branches.code={Code}, terminal_registers={Table}, rls_forced={Rls}, terminal_registers_assign={Fn}); device pairing would fail. Apply deploy/db/migrations before this API version.",
                    branchesCodeColumnExists, terminalRegistersTableExists, terminalRegistersRlsForced, terminalRegistersAssignExists);
            }

            if (!ordersSchemaOk)
            {
                _logger?.LogError("Readiness failed: migration 0025 missing (orders={Orders}, orders_rls_forced={OrdersRls}, orders_policy={OrdersPolicy}, order_lines={Lines}, order_lines_rls_forced={LinesRls}, order_lines_policy={LinesPolicy}); web orders could not be stored. Apply deploy/db/migrations/0025_orders.sql before this API version.",
                    ordersTableExists, ordersRlsForced, ordersPolicyExists, orderLinesTableExists, orderLinesRlsForced, orderLinesPolicyExists);
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
                $"lookup_policy={guestOrderVerificationsLookupPolicyExists}, issue_policy={guestOrderVerificationsIssuePolicyExists}, update_policy={guestOrderVerificationsUpdatePolicyExists}), " +
                $"payment_entries(table={paymentEntriesTableExists}, rls_forced={paymentEntriesRlsForced}, policy={paymentEntriesPolicyExists}), " +
                $"rate_component_sets(table={rateComponentSetsTableExists}, rls_forced={rateComponentSetsRlsForced}, policy={rateComponentSetsPolicyExists}), " +
                $"rate_components(table={rateComponentsTableExists}, rls_forced={rateComponentsRlsForced}, policy={rateComponentsPolicyExists}), " +
                $"branches.code(column={branchesCodeColumnExists}), terminal_registers(table={terminalRegistersTableExists}, rls_forced={terminalRegistersRlsForced}, " +
                $"assign_function={terminalRegistersAssignExists}), " +
                $"orders(table={ordersTableExists}, rls_forced={ordersRlsForced}, policy={ordersPolicyExists}), " +
                $"order_lines(table={orderLinesTableExists}, rls_forced={orderLinesRlsForced}, policy={orderLinesPolicyExists}). " +
                "Apply deploy/db/migrations/0001_init_rls.sql through 0025_orders.sql (0022/0024 missing means pairing would fail, 0025 missing means orders could not be stored).");
        }
        catch (Exception ex)
        {
            return HealthCheckResult.Unhealthy("Database unreachable.", ex);
        }
    }
}
