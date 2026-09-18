using Commerce.Application.Audit;
using Commerce.Application.Ordering;
using Commerce.Cloud.Api.Endpoints;
using Commerce.Cloud.Api.Ordering;
using Commerce.Cloud.Api.Persistence;
using Commerce.Cloud.Api.Tenancy;
using Commerce.Domain.Customers;
using Npgsql;

namespace Commerce.Integration;

/// <summary>
/// Covers commerce-customer-identity Unit 3 task 3.4: the MANDATORY security
/// fix, end to end, against LIVE Postgres (`PostgresCustomerOrderingAccessStore`
/// + `PostgresCustomerStore` + `CloudOrderSubmissionService.SubmitAsync`). This
/// is "the single most important reviewable in the whole change" (orchestrator
/// instruction) — every denial case here proves the request body can no
/// longer assert its own authorization. If Postgres is not reachable, tests
/// report the gap clearly and return without asserting pass/fail, matching
/// the existing fixture convention (`PostgresTestFixture`, `CustomerRegistryTests`).
/// </summary>
[Collection("Postgres")]
public sealed class CustomerOrderingAccessTests : IDisposable
{
    private readonly bool _postgresAvailable = PostgresTestFixture.TryPing(PostgresTestFixture.DirectConnectionString);
    private readonly NpgsqlDataSource? _dataSource;

    public CustomerOrderingAccessTests()
    {
        if (!_postgresAvailable)
        {
            return;
        }

        ApplyMigrationsAndReset();
        _dataSource = NpgsqlDataSource.Create(PostgresTestFixture.DirectConnectionString);
    }

    public void Dispose() => _dataSource?.Dispose();

    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "Commerce.sln")))
        {
            dir = dir.Parent;
        }
        if (dir is null)
        {
            throw new InvalidOperationException("Could not locate repo root.");
        }
        return dir.FullName;
    }

    private static void ApplyMigrationsAndReset()
    {
        using var owner = new NpgsqlConnection(PostgresTestFixture.OwnerConnectionString);
        owner.Open();

        var repoRoot = RepoRoot();

        void Apply(string file, string? placeholder = null, string? replacement = null)
        {
            var sql = File.ReadAllText(Path.Combine(repoRoot, "deploy", "db", "migrations", file));
            if (placeholder is not null)
            {
                sql = sql.Replace(placeholder, replacement);
            }
            using var cmd = new NpgsqlCommand(sql, owner);
            cmd.ExecuteNonQuery();
        }

        Apply("0001_init_rls.sql", "__APP_RUNTIME_PASSWORD__", "dev-only-password");
        Apply("0002_users.sql");
        Apply("0003_organizations_branches.sql");
        Apply("0004_device_credentials.sql");
        Apply("0005_password_recovery.sql");
        Apply("0006_role_taxonomy.sql");
        Apply("0007_platform_administration.sql", "__PLATFORM_READONLY_PASSWORD__", "dev-only-platform-readonly-password");
        Apply("0008_customer_registry.sql");

        using var resetCmd = new NpgsqlCommand(
            "TRUNCATE TABLE customer_ordering_access, customers, password_reset_tokens, user_directory, users, device_credentials, branches, organizations CASCADE",
            owner);
        resetCmd.ExecuteNonQuery();
    }

    private async Task SeedOrganizationAsync(Guid orgId)
    {
        await using var connection = new NpgsqlConnection(PostgresTestFixture.OwnerConnectionString);
        await connection.OpenAsync();
        await using var cmd = new NpgsqlCommand("INSERT INTO organizations (id, name) VALUES ($1, 'Org')", connection);
        cmd.Parameters.AddWithValue(orgId);
        await cmd.ExecuteNonQueryAsync();
    }

    private async Task<Guid> SeedEnabledCustomerAsync(CloudTenantScope scope, Guid actorId)
    {
        var store = new PostgresCustomerStore(_dataSource!);
        var customerId = Guid.NewGuid();
        var customer = new NewCustomer(
            customerId, CustomerKind.Retail, "Jane Doe", LegalName: null, TaxIdType.None, TaxId: null,
            TaxCondition.ConsumidorFinal, Phone: null, Email: null, AddressStreet: null, AddressNumber: null,
            Neighborhood: null, Locality: null, Province: null, PostalCode: null, DeliveryNotes: null,
            DiscountPercentage: null, PaymentTerms: null, Notes: null, actorId);
        await store.CreateAsync(scope, customer, "org-user", actorId, CancellationToken.None);
        return customerId;
    }

    private (CustomerCatalogAccessService AccessService, CloudOrderSubmissionService SubmissionService, PostgresCustomerOrderingAccessStore AccessStore, InMemoryAuditSink AuditSink)
        NewServices()
    {
        var auditSink = new InMemoryAuditSink();
        var accessStore = new PostgresCustomerOrderingAccessStore(_dataSource!);
        var accessService = new CustomerCatalogAccessService(accessStore, auditSink);
        var customerStore = new PostgresCustomerStore(_dataSource!);
        var orderStore = new CloudOrderStore();
        var submissionService = new CloudOrderSubmissionService(accessService, customerStore, orderStore);
        return (accessService, submissionService, accessStore, auditSink);
    }

    [Fact]
    public async Task ValidEnabledSameOrgCredential_Succeeds()
    {
        if (!_postgresAvailable) { Console.WriteLine("SKIPPED: no live Postgres."); return; }

        var orgId = Guid.NewGuid();
        var actorId = Guid.NewGuid();
        var scope = new CloudTenantScope(orgId);
        await SeedOrganizationAsync(orgId);
        var customerId = await SeedEnabledCustomerAsync(scope, actorId);

        var (_, submissionService, accessStore, _) = NewServices();
        var credential = await accessStore.IssueAsync(scope, customerId, actorId, CancellationToken.None);

        var outcome = await submissionService.SubmitAsync(
            scope, customerId, credential, Guid.NewGuid(), Guid.NewGuid(), actorId,
            Array.Empty<Commerce.Domain.Ordering.OrderLineSnapshot>(), Guid.NewGuid(),
            destination: null, hasAvailableStock: true, CancellationToken.None);

        Assert.Equal(OrderSubmissionOutcomeStatus.Accepted, outcome.Status);
        Assert.NotNull(outcome.Order);
    }

    [Fact]
    public async Task UnknownCredential_IsDenied_WithNotFound()
    {
        if (!_postgresAvailable) { Console.WriteLine("SKIPPED: no live Postgres."); return; }

        var orgId = Guid.NewGuid();
        var actorId = Guid.NewGuid();
        var scope = new CloudTenantScope(orgId);
        await SeedOrganizationAsync(orgId);
        var customerId = await SeedEnabledCustomerAsync(scope, actorId);

        var (_, submissionService, _, _) = NewServices();

        var outcome = await submissionService.SubmitAsync(
            scope, customerId, Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), actorId,
            Array.Empty<Commerce.Domain.Ordering.OrderLineSnapshot>(), Guid.NewGuid(),
            destination: null, hasAvailableStock: true, CancellationToken.None);

        Assert.Equal(OrderSubmissionOutcomeStatus.Denied, outcome.Status);
        Assert.Equal("not-found", outcome.Reason);
        Assert.Null(outcome.Order);
    }

    [Fact]
    public async Task CredentialBoundToAnotherCustomer_IsDenied_WithSameReasonAsUnknown()
    {
        if (!_postgresAvailable) { Console.WriteLine("SKIPPED: no live Postgres."); return; }

        var orgId = Guid.NewGuid();
        var actorId = Guid.NewGuid();
        var scope = new CloudTenantScope(orgId);
        await SeedOrganizationAsync(orgId);
        var ownerCustomerId = await SeedEnabledCustomerAsync(scope, actorId);
        var claimedCustomerId = await SeedEnabledCustomerAsync(scope, actorId);

        var (_, submissionService, accessStore, _) = NewServices();
        var credential = await accessStore.IssueAsync(scope, ownerCustomerId, actorId, CancellationToken.None);

        // The credential really is enabled, in the right org — but bound to
        // ownerCustomerId, not the customerId this caller declares.
        var outcome = await submissionService.SubmitAsync(
            scope, claimedCustomerId, credential, Guid.NewGuid(), Guid.NewGuid(), actorId,
            Array.Empty<Commerce.Domain.Ordering.OrderLineSnapshot>(), Guid.NewGuid(),
            destination: null, hasAvailableStock: true, CancellationToken.None);

        Assert.Equal(OrderSubmissionOutcomeStatus.Denied, outcome.Status);
        Assert.Equal("not-found", outcome.Reason);
    }

    [Fact]
    public async Task RevokingAccess_DeniesTheVeryNextSubmission_AndTheDenialIsAudited()
    {
        if (!_postgresAvailable) { Console.WriteLine("SKIPPED: no live Postgres."); return; }

        var orgId = Guid.NewGuid();
        var actorId = Guid.NewGuid();
        var scope = new CloudTenantScope(orgId);
        await SeedOrganizationAsync(orgId);
        var customerId = await SeedEnabledCustomerAsync(scope, actorId);

        var (_, submissionService, accessStore, auditSink) = NewServices();
        var credential = await accessStore.IssueAsync(scope, customerId, actorId, CancellationToken.None);

        var accepted = await submissionService.SubmitAsync(
            scope, customerId, credential, Guid.NewGuid(), Guid.NewGuid(), actorId,
            Array.Empty<Commerce.Domain.Ordering.OrderLineSnapshot>(), Guid.NewGuid(),
            destination: null, hasAvailableStock: true, CancellationToken.None);
        Assert.Equal(OrderSubmissionOutcomeStatus.Accepted, accepted.Status);

        var revoked = await accessStore.RevokeAsync(credential, CancellationToken.None);
        Assert.True(revoked);

        var deniedOutcome = await submissionService.SubmitAsync(
            scope, customerId, credential, Guid.NewGuid(), Guid.NewGuid(), actorId,
            Array.Empty<Commerce.Domain.Ordering.OrderLineSnapshot>(), Guid.NewGuid(),
            destination: null, hasAvailableStock: true, CancellationToken.None);

        Assert.Equal(OrderSubmissionOutcomeStatus.Denied, deniedOutcome.Status);
        Assert.Equal("credential-revoked", deniedOutcome.Reason);

        var deniedEntries = auditSink.Entries.Where(e => e.Outcome == "denied").ToList();
        Assert.Single(deniedEntries);
        Assert.Equal("credential-revoked", deniedEntries[0].Reason);
    }

    [Fact]
    public async Task CredentialValidInOrganizationA_IsDeniedInOrganizationB()
    {
        if (!_postgresAvailable) { Console.WriteLine("SKIPPED: no live Postgres."); return; }

        var orgAId = Guid.NewGuid();
        var orgBId = Guid.NewGuid();
        var actorId = Guid.NewGuid();
        var scopeA = new CloudTenantScope(orgAId);
        var scopeB = new CloudTenantScope(orgBId);
        await SeedOrganizationAsync(orgAId);
        await SeedOrganizationAsync(orgBId);
        var customerAId = await SeedEnabledCustomerAsync(scopeA, actorId);

        var (_, submissionService, accessStore, _) = NewServices();
        var credential = await accessStore.IssueAsync(scopeA, customerAId, actorId, CancellationToken.None);

        var outcomeInOwnOrg = await submissionService.SubmitAsync(
            scopeA, customerAId, credential, Guid.NewGuid(), Guid.NewGuid(), actorId,
            Array.Empty<Commerce.Domain.Ordering.OrderLineSnapshot>(), Guid.NewGuid(),
            destination: null, hasAvailableStock: true, CancellationToken.None);
        Assert.Equal(OrderSubmissionOutcomeStatus.Accepted, outcomeInOwnOrg.Status);

        var outcomeInOtherOrg = await submissionService.SubmitAsync(
            scopeB, customerAId, credential, Guid.NewGuid(), Guid.NewGuid(), actorId,
            Array.Empty<Commerce.Domain.Ordering.OrderLineSnapshot>(), Guid.NewGuid(),
            destination: null, hasAvailableStock: true, CancellationToken.None);

        Assert.Equal(OrderSubmissionOutcomeStatus.Denied, outcomeInOtherOrg.Status);
        Assert.Equal("not-found", outcomeInOtherOrg.Reason);
    }

    /// <summary>
    /// `customer_ordering_access.customer_id` carries an FK to `customers`
    /// (defense in depth), so a credential can never be bound to a customer
    /// id that has no row at all — that state is unrepresentable at the
    /// database layer. The reachable "no usable customer" case is therefore
    /// a DISABLED customer row (e.g. disabled after the credential was
    /// issued), which is what this proves: the submission path denies before
    /// `CloudOrderStore.Submit` is ever reached (design.md "Order.CustomerId
    /// referential integrity").
    /// </summary>
    [Fact]
    public async Task DisabledCustomerRow_IsDenied_WithCustomerDisabledReason()
    {
        if (!_postgresAvailable) { Console.WriteLine("SKIPPED: no live Postgres."); return; }

        var orgId = Guid.NewGuid();
        var actorId = Guid.NewGuid();
        var scope = new CloudTenantScope(orgId);
        await SeedOrganizationAsync(orgId);
        var customerId = await SeedEnabledCustomerAsync(scope, actorId);

        var (_, submissionService, accessStore, _) = NewServices();
        var credential = await accessStore.IssueAsync(scope, customerId, actorId, CancellationToken.None);

        await using (var connection = new NpgsqlConnection(PostgresTestFixture.OwnerConnectionString))
        {
            await connection.OpenAsync();
            await using var cmd = new NpgsqlCommand("UPDATE customers SET is_enabled = false WHERE id = $1", connection);
            cmd.Parameters.AddWithValue(customerId);
            await cmd.ExecuteNonQueryAsync();
        }

        var outcome = await submissionService.SubmitAsync(
            scope, customerId, credential, Guid.NewGuid(), Guid.NewGuid(), actorId,
            Array.Empty<Commerce.Domain.Ordering.OrderLineSnapshot>(), Guid.NewGuid(),
            destination: null, hasAvailableStock: true, CancellationToken.None);

        Assert.Equal(OrderSubmissionOutcomeStatus.Denied, outcome.Status);
        Assert.Equal("customer-disabled", outcome.Reason);
    }

    [Fact]
    public void SubmitOrderRequest_HasNoAccessEnabledMember()
    {
        // Structural proof (design.md "unrepresentable, not validated-away"):
        // `AccessEnabled` does not exist on the DTO at all, so a body carrying
        // it cannot be honoured — there is no member to deserialize into.
        var properties = typeof(SubmitOrderRequest).GetProperties();

        Assert.DoesNotContain(properties, p => p.Name == "AccessEnabled");
    }
}
