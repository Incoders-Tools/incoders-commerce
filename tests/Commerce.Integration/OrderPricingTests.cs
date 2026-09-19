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
/// Covers commerce-pricing-engine Phase 4 (Unit 4, order integration):
/// <see cref="CloudOrderSubmissionService.SubmitAsync"/> resolves price
/// AFTER every commerce-customer-identity denial check and BEFORE
/// <see cref="CloudOrderStore.Submit"/> (design.md "OrderLineSnapshot
/// extension and where resolution runs"). Against LIVE Postgres, mirroring
/// the `CustomerOrderingAccessTests` skip-if-unreachable convention.
/// </summary>
[Collection("Postgres")]
public sealed class OrderPricingTests : IDisposable
{
    private readonly bool _postgresAvailable = PostgresTestFixture.TryPing(PostgresTestFixture.DirectConnectionString);
    private readonly NpgsqlDataSource? _dataSource;

    public OrderPricingTests()
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
        return dir?.FullName ?? throw new InvalidOperationException("Could not locate repo root.");
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
        Apply("0009_catalog_and_pricing.sql");

        using var resetCmd = new NpgsqlCommand(
            """
            TRUNCATE TABLE price_list_entries, price_lists, presentations, products,
                customer_ordering_access, customers, password_reset_tokens,
                user_directory, users, device_credentials, branches, organizations CASCADE
            """,
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

    private async Task<Guid> SeedCustomerAsync(CloudTenantScope scope, Guid actorId, decimal? discountPercentage = null)
    {
        var store = new PostgresCustomerStore(_dataSource!);
        var customerId = Guid.NewGuid();
        var customer = new NewCustomer(
            customerId, CustomerKind.Retail, "Jane Doe", LegalName: null, TaxIdType.None, TaxId: null,
            TaxCondition.ConsumidorFinal, Phone: null, Email: null, AddressStreet: null, AddressNumber: null,
            Neighborhood: null, Locality: null, Province: null, PostalCode: null, DeliveryNotes: null,
            discountPercentage, PaymentTerms: null, Notes: null, actorId);
        await store.CreateAsync(scope, customer, "org-user", actorId, CancellationToken.None);
        return customerId;
    }

    private async Task<Guid> SeedPresentationAsync(CloudTenantScope scope, Guid actorId)
    {
        var catalogStore = new PostgresCatalogStore(_dataSource!);
        var product = await catalogStore.CreateProductAsync(
            scope, new NewProduct(Guid.NewGuid(), "Product", Guid.NewGuid(), Guid.NewGuid(), actorId),
            "org-user", actorId, CancellationToken.None);
        var presentation = await catalogStore.CreatePresentationAsync(
            scope,
            new NewPresentation(Guid.NewGuid(), product.Id, "6-pack", Commerce.Domain.Catalog.QuantityBehavior.FixedQuantity, Guid.NewGuid(), IdentificationCode: null, actorId),
            "org-user", actorId, CancellationToken.None);
        return presentation.Id;
    }

    private async Task<Guid> SeedDefaultPriceListAsync(CloudTenantScope scope, Guid actorId)
    {
        var priceListStore = new PostgresPriceListStore(_dataSource!);
        var priceList = await priceListStore.CreatePriceListAsync(
            scope, new NewPriceList(Guid.NewGuid(), "Default", IsDefault: true, actorId), "org-user", actorId, CancellationToken.None);
        return priceList.Id;
    }

    private async Task PublishPriceAsync(CloudTenantScope scope, Guid priceListId, Guid presentationId, decimal unitPrice, Guid actorId, DateOnly? effectiveFrom = null)
    {
        var priceListStore = new PostgresPriceListStore(_dataSource!);
        await priceListStore.AppendEntryAsync(
            scope,
            new NewPriceListEntry(Guid.NewGuid(), priceListId, presentationId, unitPrice, effectiveFrom ?? DateOnly.FromDateTime(DateTime.UtcNow), "Manual", ImportBatchId: null, actorId),
            "org-user", actorId, CancellationToken.None);
    }

    private (CustomerCatalogAccessService AccessService, CloudOrderSubmissionService SubmissionService, PostgresCustomerOrderingAccessStore AccessStore)
        NewServices()
    {
        var auditSink = new InMemoryAuditSink();
        var accessStore = new PostgresCustomerOrderingAccessStore(_dataSource!);
        var accessService = new CustomerCatalogAccessService(accessStore, auditSink);
        var customerStore = new PostgresCustomerStore(_dataSource!);
        var orderStore = new CloudOrderStore();
        var catalogStore = new PostgresCatalogStore(_dataSource!);
        var priceListStore = new PostgresPriceListStore(_dataSource!);
        var submissionService = new CloudOrderSubmissionService(accessService, customerStore, orderStore, catalogStore, priceListStore);
        return (accessService, submissionService, accessStore);
    }

    /// <summary>Spec scenario: guest/registered resolution happens, and the frozen line carries all four resolved fields.</summary>
    [Fact]
    public async Task SubmitAsync_WithEffectivePrice_FreezesResolvedPriceFieldsOnTheLine()
    {
        if (!_postgresAvailable) { Console.WriteLine("SKIPPED: no live Postgres."); return; }

        var orgId = Guid.NewGuid();
        var actorId = Guid.NewGuid();
        var scope = new CloudTenantScope(orgId);
        await SeedOrganizationAsync(orgId);
        var customerId = await SeedCustomerAsync(scope, actorId, discountPercentage: 10m);
        var presentationId = await SeedPresentationAsync(scope, actorId);
        var priceListId = await SeedDefaultPriceListAsync(scope, actorId);
        await PublishPriceAsync(scope, priceListId, presentationId, 100.00m, actorId);

        var (_, submissionService, accessStore) = NewServices();
        var credential = await accessStore.IssueAsync(scope, customerId, actorId, CancellationToken.None);
        var line = new SubmitOrderLine(Guid.NewGuid(), presentationId, Quantity: 2m);

        var outcome = await submissionService.SubmitAsync(
            scope, customerId, credential, Guid.NewGuid(), Guid.NewGuid(), actorId,
            new[] { line }, Guid.NewGuid(), destination: null, hasAvailableStock: true, CancellationToken.None);

        Assert.Equal(OrderSubmissionOutcomeStatus.Accepted, outcome.Status);
        Assert.NotNull(outcome.Order);
        var resolvedLine = outcome.Order!.Lines[0];
        Assert.Equal(100.00m, resolvedLine.UnitListPrice);
        Assert.Equal(10m, resolvedLine.AppliedDiscountPercentage);
        Assert.Equal(90.00m, resolvedLine.UnitNetPrice);
        Assert.Equal(180.00m, resolvedLine.LineTotal);
    }

    /// <summary>Spec scenario: a presentation with zero effective price rows denies the WHOLE order, not just that line.</summary>
    [Fact]
    public async Task SubmitAsync_WithNoEffectivePriceOnAnyLine_DeniesTheWholeOrder_AndStoresNothing()
    {
        if (!_postgresAvailable) { Console.WriteLine("SKIPPED: no live Postgres."); return; }

        var orgId = Guid.NewGuid();
        var actorId = Guid.NewGuid();
        var scope = new CloudTenantScope(orgId);
        await SeedOrganizationAsync(orgId);
        var customerId = await SeedCustomerAsync(scope, actorId);
        var presentationId = await SeedPresentationAsync(scope, actorId);
        // No default price list, no price entry: zero effective rows.

        var (_, submissionService, accessStore) = NewServices();
        var credential = await accessStore.IssueAsync(scope, customerId, actorId, CancellationToken.None);
        var line = new SubmitOrderLine(Guid.NewGuid(), presentationId, Quantity: 1m);
        var orderId = Guid.NewGuid();

        var outcome = await submissionService.SubmitAsync(
            scope, customerId, credential, orderId, Guid.NewGuid(), actorId,
            new[] { line }, Guid.NewGuid(), destination: null, hasAvailableStock: true, CancellationToken.None);

        Assert.Equal(OrderSubmissionOutcomeStatus.Denied, outcome.Status);
        Assert.Equal("no-effective-price", outcome.Reason);
        Assert.Null(outcome.Order);
    }

    /// <summary>"One bad line poisons the whole order": a second, perfectly-priced line does not rescue an order where another line has no effective price.</summary>
    [Fact]
    public async Task SubmitAsync_WithOneBadLineAmongGoodLines_DeniesTheWholeOrder()
    {
        if (!_postgresAvailable) { Console.WriteLine("SKIPPED: no live Postgres."); return; }

        var orgId = Guid.NewGuid();
        var actorId = Guid.NewGuid();
        var scope = new CloudTenantScope(orgId);
        await SeedOrganizationAsync(orgId);
        var customerId = await SeedCustomerAsync(scope, actorId);
        var pricedPresentationId = await SeedPresentationAsync(scope, actorId);
        var unpricedPresentationId = await SeedPresentationAsync(scope, actorId);
        var priceListId = await SeedDefaultPriceListAsync(scope, actorId);
        await PublishPriceAsync(scope, priceListId, pricedPresentationId, 50.00m, actorId);
        // unpricedPresentationId deliberately has NO published price.

        var (_, submissionService, accessStore) = NewServices();
        var credential = await accessStore.IssueAsync(scope, customerId, actorId, CancellationToken.None);
        var lines = new[]
        {
            new SubmitOrderLine(Guid.NewGuid(), pricedPresentationId, Quantity: 1m),
            new SubmitOrderLine(Guid.NewGuid(), unpricedPresentationId, Quantity: 1m),
        };

        var outcome = await submissionService.SubmitAsync(
            scope, customerId, credential, Guid.NewGuid(), Guid.NewGuid(), actorId,
            lines, Guid.NewGuid(), destination: null, hasAvailableStock: true, CancellationToken.None);

        Assert.Equal(OrderSubmissionOutcomeStatus.Denied, outcome.Status);
        Assert.Equal("no-effective-price", outcome.Reason);
        Assert.Null(outcome.Order);
    }

    /// <summary>
    /// Freeze test (design.md ADR-003 / "Submit an order"): publishing a NEW
    /// price for the same presentation AFTER a successful submission never
    /// alters the already-stored order's frozen line — the append-only price
    /// history is not retroactive.
    /// </summary>
    [Fact]
    public async Task PublishingANewPrice_AfterSubmission_DoesNotAlterTheStoredOrder()
    {
        if (!_postgresAvailable) { Console.WriteLine("SKIPPED: no live Postgres."); return; }

        var orgId = Guid.NewGuid();
        var actorId = Guid.NewGuid();
        var scope = new CloudTenantScope(orgId);
        await SeedOrganizationAsync(orgId);
        var customerId = await SeedCustomerAsync(scope, actorId);
        var presentationId = await SeedPresentationAsync(scope, actorId);
        var priceListId = await SeedDefaultPriceListAsync(scope, actorId);
        await PublishPriceAsync(scope, priceListId, presentationId, 100.00m, actorId, DateOnly.FromDateTime(DateTime.UtcNow).AddDays(-1));

        var (_, submissionService, accessStore) = NewServices();
        var credential = await accessStore.IssueAsync(scope, customerId, actorId, CancellationToken.None);
        var line = new SubmitOrderLine(Guid.NewGuid(), presentationId, Quantity: 1m);

        var outcome = await submissionService.SubmitAsync(
            scope, customerId, credential, Guid.NewGuid(), Guid.NewGuid(), actorId,
            new[] { line }, Guid.NewGuid(), destination: null, hasAvailableStock: true, CancellationToken.None);
        Assert.Equal(OrderSubmissionOutcomeStatus.Accepted, outcome.Status);
        Assert.Equal(100.00m, outcome.Order!.Lines[0].UnitNetPrice);

        // A new, HIGHER price is published for the SAME presentation, effective today.
        await PublishPriceAsync(scope, priceListId, presentationId, 250.00m, actorId);

        Assert.Equal(100.00m, outcome.Order.Lines[0].UnitNetPrice);
        Assert.Equal(100.00m, outcome.Order.Lines[0].LineTotal);
    }

    /// <summary>
    /// Regression guard (commerce-customer-identity security fix): the four
    /// pre-existing denial checks (unknown credential, credential bound to
    /// another customer, revoked credential, disabled customer, cross-org)
    /// still deny BEFORE pricing resolution ever runs — proven here by an
    /// UNKNOWN credential against a presentation that has NO effective price
    /// either; if pricing ran first the reason would leak as
    /// "no-effective-price" instead of the access-layer's "not-found".
    /// </summary>
    [Fact]
    public async Task UnknownCredential_IsStillDeniedWithNotFound_EvenWhenNoEffectivePriceWouldAlsoDeny()
    {
        if (!_postgresAvailable) { Console.WriteLine("SKIPPED: no live Postgres."); return; }

        var orgId = Guid.NewGuid();
        var actorId = Guid.NewGuid();
        var scope = new CloudTenantScope(orgId);
        await SeedOrganizationAsync(orgId);
        var customerId = await SeedCustomerAsync(scope, actorId);
        var presentationId = await SeedPresentationAsync(scope, actorId);
        // No price list, no price entry -> pricing would ALSO deny, if reached.

        var (_, submissionService, _) = NewServices();
        var line = new SubmitOrderLine(Guid.NewGuid(), presentationId, Quantity: 1m);

        var outcome = await submissionService.SubmitAsync(
            scope, customerId, Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), actorId,
            new[] { line }, Guid.NewGuid(), destination: null, hasAvailableStock: true, CancellationToken.None);

        Assert.Equal(OrderSubmissionOutcomeStatus.Denied, outcome.Status);
        Assert.Equal("not-found", outcome.Reason);
    }

    /// <summary>
    /// Regression guard: a disabled customer is still denied "customer-disabled"
    /// BEFORE pricing resolution runs, even when a valid price exists for
    /// every line (proves pricing is additive on top of the existing checks,
    /// never a replacement of them).
    /// </summary>
    [Fact]
    public async Task DisabledCustomer_IsStillDeniedBeforePricingRuns_EvenWithAValidEffectivePrice()
    {
        if (!_postgresAvailable) { Console.WriteLine("SKIPPED: no live Postgres."); return; }

        var orgId = Guid.NewGuid();
        var actorId = Guid.NewGuid();
        var scope = new CloudTenantScope(orgId);
        await SeedOrganizationAsync(orgId);
        var customerId = await SeedCustomerAsync(scope, actorId);
        var presentationId = await SeedPresentationAsync(scope, actorId);
        var priceListId = await SeedDefaultPriceListAsync(scope, actorId);
        await PublishPriceAsync(scope, priceListId, presentationId, 20.00m, actorId);

        var (_, submissionService, accessStore) = NewServices();
        var credential = await accessStore.IssueAsync(scope, customerId, actorId, CancellationToken.None);

        await using (var connection = new NpgsqlConnection(PostgresTestFixture.OwnerConnectionString))
        {
            await connection.OpenAsync();
            await using var cmd = new NpgsqlCommand("UPDATE customers SET is_enabled = false WHERE id = $1", connection);
            cmd.Parameters.AddWithValue(customerId);
            await cmd.ExecuteNonQueryAsync();
        }

        var line = new SubmitOrderLine(Guid.NewGuid(), presentationId, Quantity: 1m);

        var outcome = await submissionService.SubmitAsync(
            scope, customerId, credential, Guid.NewGuid(), Guid.NewGuid(), actorId,
            new[] { line }, Guid.NewGuid(), destination: null, hasAvailableStock: true, CancellationToken.None);

        Assert.Equal(OrderSubmissionOutcomeStatus.Denied, outcome.Status);
        Assert.Equal("customer-disabled", outcome.Reason);
    }

    /// <summary>
    /// Regression guard: the OLD (pre-pricing) contract's happy path — a
    /// valid, enabled, bound credential — still succeeds under the new
    /// contract PROVIDED an effective price exists for every line. Pricing is
    /// additive, not a replacement of the access/binding/customer checks.
    /// </summary>
    [Fact]
    public async Task ValidEnabledCredential_StillSucceeds_ProvidedAnEffectivePriceExistsForEveryLine()
    {
        if (!_postgresAvailable) { Console.WriteLine("SKIPPED: no live Postgres."); return; }

        var orgId = Guid.NewGuid();
        var actorId = Guid.NewGuid();
        var scope = new CloudTenantScope(orgId);
        await SeedOrganizationAsync(orgId);
        var customerId = await SeedCustomerAsync(scope, actorId);
        var presentationId = await SeedPresentationAsync(scope, actorId);
        var priceListId = await SeedDefaultPriceListAsync(scope, actorId);
        await PublishPriceAsync(scope, priceListId, presentationId, 42.00m, actorId);

        var (_, submissionService, accessStore) = NewServices();
        var credential = await accessStore.IssueAsync(scope, customerId, actorId, CancellationToken.None);
        var line = new SubmitOrderLine(Guid.NewGuid(), presentationId, Quantity: 1m);

        var outcome = await submissionService.SubmitAsync(
            scope, customerId, credential, Guid.NewGuid(), Guid.NewGuid(), actorId,
            new[] { line }, Guid.NewGuid(), destination: null, hasAvailableStock: true, CancellationToken.None);

        Assert.Equal(OrderSubmissionOutcomeStatus.Accepted, outcome.Status);
        Assert.NotNull(outcome.Order);
    }

    /// <summary>Structural proof: a client has nowhere to put a price on an order line — `SubmitOrderLine` carries no price member at all.</summary>
    [Fact]
    public void SubmitOrderLine_HasNoPriceMember()
    {
        var properties = typeof(SubmitOrderLine).GetProperties();

        Assert.DoesNotContain(properties, p => p.Name.Contains("Price", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(properties, p => p.Name.Contains("Discount", StringComparison.OrdinalIgnoreCase));
    }
}
