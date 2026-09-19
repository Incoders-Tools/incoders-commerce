using Commerce.Application.Audit;
using Commerce.Application.Ordering;
using Commerce.Cloud.Api.Email;
using Commerce.Cloud.Api.Endpoints;
using Commerce.Cloud.Api.Ordering;
using Commerce.Cloud.Api.Persistence;
using Commerce.Cloud.Api.Tenancy;
using Commerce.Domain.Customers;
using Commerce.Domain.Ordering;
using Npgsql;

namespace Commerce.Integration;

/// <summary>
/// commerce-guest-ordering Phase 4 (Unit 4, "Guest submission branch"): the
/// ADR-010 divergence proven end-to-end through the FULL submission path
/// (not the isolated <see cref="Commerce.Application.Pricing.PricingResolutionService"/>
/// unit test), and the guest verification gate wired immediately before
/// <see cref="CloudOrderStore.Submit"/> (public-order-surface spec.md "Guest
/// Verification Gate Before Admission"; guest-ordering spec.md "Guest Price
/// Resolution"). Against LIVE Postgres, mirroring the
/// <see cref="OrderPricingTests"/> skip-if-unreachable convention.
/// </summary>
[Collection("Postgres")]
public sealed class GuestOrderingTests : IDisposable
{
    private readonly bool _postgresAvailable = PostgresTestFixture.TryPing(PostgresTestFixture.DirectConnectionString);
    private readonly NpgsqlDataSource? _dataSource;

    public GuestOrderingTests()
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
        Apply("0010_guest_ordering.sql");

        using var resetCmd = new NpgsqlCommand(
            """
            TRUNCATE TABLE guest_order_verifications, price_list_entries, price_lists, presentations, products,
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

    /// <summary>Records every message it would have sent, in memory, and NEVER touches the network.</summary>
    private sealed class FakeEmailSender : IEmailSender
    {
        public List<EmailMessage> Sent { get; } = [];

        public Task<bool> SendAsync(EmailMessage message, CancellationToken ct)
        {
            Sent.Add(message);
            return Task.FromResult(true);
        }
    }

    private static string ExtractCode(EmailMessage message)
    {
        var firstSentence = message.TextBody[..message.TextBody.IndexOf('.')];
        return new string(firstSentence.Where(char.IsDigit).ToArray());
    }

    /// <summary>
    /// Issues and confirms a verification for the given contact, returning
    /// its id and the resulting <see cref="GuestContact"/> to submit with.
    /// </summary>
    private static async Task<(Guid VerificationId, GuestContact Contact)> IssueAndConfirmVerificationAsync(
        GuestVerificationService verificationService, FakeEmailSender sender, CloudTenantScope scope,
        string documentId, string email, string displayName = "Guest Buyer")
    {
        var verificationId = await verificationService.RequestAsync(
            scope, documentId, GuestContactChannel.Email, email, CancellationToken.None);
        var code = ExtractCode(sender.Sent[^1]);
        var confirmed = await verificationService.ConfirmAsync(verificationId, code, CancellationToken.None);
        Assert.Equal(GuestVerificationConfirmResult.Confirmed, confirmed);

        var contact = new GuestContact(documentId, GuestContactChannel.Email, email, displayName, DeliveryNotes: null);
        return (verificationId, contact);
    }

    private (CloudOrderStore OrderStore, CloudOrderSubmissionService SubmissionService, GuestVerificationService VerificationService, FakeEmailSender Sender, PostgresCustomerOrderingAccessStore AccessStore)
        NewServicesWithSharedSender()
    {
        var auditSink = new InMemoryAuditSink();
        var accessStore = new PostgresCustomerOrderingAccessStore(_dataSource!);
        var accessService = new CustomerCatalogAccessService(accessStore, auditSink);
        var customerStore = new PostgresCustomerStore(_dataSource!);
        var orderStore = new CloudOrderStore();
        var catalogStore = new PostgresCatalogStore(_dataSource!);
        var priceListStore = new PostgresPriceListStore(_dataSource!);
        var verificationStore = new PostgresGuestVerificationStore(_dataSource!);
        var sender = new FakeEmailSender();
        var verificationService = new GuestVerificationService(verificationStore, sender);
        var submissionService = new CloudOrderSubmissionService(
            accessService, customerStore, orderStore, catalogStore, priceListStore, verificationService);
        return (orderStore, submissionService, verificationService, sender, accessStore);
    }

    // --- ADR-010 divergence, end-to-end through the full submission path ----

    [Fact]
    public async Task ADR010Divergence_RegisteredCustomerWithDiscount_PaysStrictlyLessThanGuest_ForTheSameItem()
    {
        if (!_postgresAvailable) { Console.WriteLine("SKIPPED: no live Postgres."); return; }

        var orgId = Guid.NewGuid();
        var actorId = Guid.NewGuid();
        var scope = new CloudTenantScope(orgId);
        await SeedOrganizationAsync(orgId);
        var presentationId = await SeedPresentationAsync(scope, actorId);
        var priceListId = await SeedDefaultPriceListAsync(scope, actorId);
        await PublishPriceAsync(scope, priceListId, presentationId, 100.00m, actorId);
        var customerId = await SeedCustomerAsync(scope, actorId, discountPercentage: 10m);

        var (_, submissionService, verificationService, sender, accessStore) = NewServicesWithSharedSender();
        var credential = await accessStore.IssueAsync(scope, customerId, actorId, CancellationToken.None);

        var (verificationId, contact) = await IssueAndConfirmVerificationAsync(
            verificationService, sender, scope, "30111222333", "guest@example.com");
        var guestOutcome = await submissionService.SubmitGuestAsync(
            scope, Guid.NewGuid(), verificationId, contact, Guid.NewGuid(),
            new[] { new SubmitOrderLine(Guid.NewGuid(), presentationId, Quantity: 1m) },
            Guid.NewGuid(), destination: null, hasAvailableStock: true, CancellationToken.None);

        var registeredOutcome = await submissionService.SubmitAsync(
            scope, customerId, credential, Guid.NewGuid(), Guid.NewGuid(), actorId,
            new[] { new SubmitOrderLine(Guid.NewGuid(), presentationId, Quantity: 1m) },
            Guid.NewGuid(), destination: null, hasAvailableStock: true, CancellationToken.None);

        Assert.Equal(OrderSubmissionOutcomeStatus.Accepted, guestOutcome.Status);
        Assert.Equal(OrderSubmissionOutcomeStatus.Accepted, registeredOutcome.Status);
        Assert.Equal(100.00m, guestOutcome.Order!.Lines[0].UnitNetPrice);
        Assert.Equal(90.00m, registeredOutcome.Order!.Lines[0].UnitNetPrice);
        Assert.True(registeredOutcome.Order.Lines[0].UnitNetPrice < guestOutcome.Order.Lines[0].UnitNetPrice);
        Assert.Equal(OrderOrigin.Guest, guestOutcome.Order.Origin);
        Assert.Null(guestOutcome.Order.CustomerId);
        Assert.Equal(OrderOrigin.RegisteredCustomer, registeredOutcome.Order.Origin);
    }

    // --- Guest identity + non-staff actor capture ----------------------------

    [Fact]
    public async Task SubmitGuestAsync_Admitted_CarriesGuestContactOnTheOrder_WithNoCustomerId()
    {
        if (!_postgresAvailable) { Console.WriteLine("SKIPPED: no live Postgres."); return; }

        var orgId = Guid.NewGuid();
        var actorId = Guid.NewGuid();
        var scope = new CloudTenantScope(orgId);
        await SeedOrganizationAsync(orgId);
        var presentationId = await SeedPresentationAsync(scope, actorId);
        var priceListId = await SeedDefaultPriceListAsync(scope, actorId);
        await PublishPriceAsync(scope, priceListId, presentationId, 50.00m, actorId);

        var (_, submissionService, verificationService, sender, _) = NewServicesWithSharedSender();
        var (verificationId, contact) = await IssueAndConfirmVerificationAsync(
            verificationService, sender, scope, "30999888777", "buyer@example.com", "Real Guest");

        var outcome = await submissionService.SubmitGuestAsync(
            scope, Guid.NewGuid(), verificationId, contact, Guid.NewGuid(),
            new[] { new SubmitOrderLine(Guid.NewGuid(), presentationId, Quantity: 2m) },
            Guid.NewGuid(), destination: null, hasAvailableStock: true, CancellationToken.None);

        Assert.Equal(OrderSubmissionOutcomeStatus.Accepted, outcome.Status);
        Assert.Equal(contact, outcome.Order!.GuestContact);
        Assert.Null(outcome.Order.CustomerId);
        Assert.Equal(OrderOrigin.Guest, outcome.Order.Origin);
    }

    // --- Verification gate ----------------------------------------------------

    [Fact]
    public async Task SubmitGuestAsync_WithNoPriorVerification_IsRejected_AndNoOrderIsStored()
    {
        if (!_postgresAvailable) { Console.WriteLine("SKIPPED: no live Postgres."); return; }

        var orgId = Guid.NewGuid();
        var actorId = Guid.NewGuid();
        var scope = new CloudTenantScope(orgId);
        await SeedOrganizationAsync(orgId);
        var presentationId = await SeedPresentationAsync(scope, actorId);
        var priceListId = await SeedDefaultPriceListAsync(scope, actorId);
        await PublishPriceAsync(scope, priceListId, presentationId, 30.00m, actorId);

        var (orderStore, submissionService, _, _, _) = NewServicesWithSharedSender();
        var contact = new GuestContact("30111222333", GuestContactChannel.Email, "never-verified@example.com", "Ghost", DeliveryNotes: null);
        var orderId = Guid.NewGuid();

        var outcome = await submissionService.SubmitGuestAsync(
            scope, orderId, Guid.NewGuid(), contact, Guid.NewGuid(),
            new[] { new SubmitOrderLine(Guid.NewGuid(), presentationId, Quantity: 1m) },
            Guid.NewGuid(), destination: null, hasAvailableStock: true, CancellationToken.None);

        Assert.Equal(OrderSubmissionOutcomeStatus.Denied, outcome.Status);
        Assert.Null(outcome.Order);
        Assert.Null(orderStore.Find(scope, orderId));
    }

    [Fact]
    public async Task SubmitGuestAsync_WithAlreadyConsumedVerification_IsRejected_OnTheSecondAttempt()
    {
        if (!_postgresAvailable) { Console.WriteLine("SKIPPED: no live Postgres."); return; }

        var orgId = Guid.NewGuid();
        var actorId = Guid.NewGuid();
        var scope = new CloudTenantScope(orgId);
        await SeedOrganizationAsync(orgId);
        var presentationId = await SeedPresentationAsync(scope, actorId);
        var priceListId = await SeedDefaultPriceListAsync(scope, actorId);
        await PublishPriceAsync(scope, priceListId, presentationId, 30.00m, actorId);

        var (_, submissionService, verificationService, sender, _) = NewServicesWithSharedSender();
        var (verificationId, contact) = await IssueAndConfirmVerificationAsync(
            verificationService, sender, scope, "30111222333", "twice@example.com");
        var line = new SubmitOrderLine(Guid.NewGuid(), presentationId, Quantity: 1m);

        var first = await submissionService.SubmitGuestAsync(
            scope, Guid.NewGuid(), verificationId, contact, Guid.NewGuid(),
            new[] { line }, Guid.NewGuid(), destination: null, hasAvailableStock: true, CancellationToken.None);
        Assert.Equal(OrderSubmissionOutcomeStatus.Accepted, first.Status);

        var second = await submissionService.SubmitGuestAsync(
            scope, Guid.NewGuid(), verificationId, contact, Guid.NewGuid(),
            new[] { line }, Guid.NewGuid(), destination: null, hasAvailableStock: true, CancellationToken.None);

        Assert.Equal(OrderSubmissionOutcomeStatus.Denied, second.Status);
        Assert.Null(second.Order);
    }

    [Fact]
    public async Task SubmitGuestAsync_WithExpiredVerification_IsRejected()
    {
        if (!_postgresAvailable) { Console.WriteLine("SKIPPED: no live Postgres."); return; }

        var orgId = Guid.NewGuid();
        var actorId = Guid.NewGuid();
        var scope = new CloudTenantScope(orgId);
        await SeedOrganizationAsync(orgId);
        var presentationId = await SeedPresentationAsync(scope, actorId);
        var priceListId = await SeedDefaultPriceListAsync(scope, actorId);
        await PublishPriceAsync(scope, priceListId, presentationId, 30.00m, actorId);

        var accessStore = new PostgresCustomerOrderingAccessStore(_dataSource!);
        var accessService = new CustomerCatalogAccessService(accessStore, new InMemoryAuditSink());
        var customerStore = new PostgresCustomerStore(_dataSource!);
        var orderStore = new CloudOrderStore();
        var catalogStore = new PostgresCatalogStore(_dataSource!);
        var priceListStore = new PostgresPriceListStore(_dataSource!);
        var verificationStore = new PostgresGuestVerificationStore(_dataSource!);
        var sender = new FakeEmailSender();
        var now = DateTimeOffset.UtcNow;
        var clockBox = new[] { now };
        var verificationService = new GuestVerificationService(verificationStore, sender, () => clockBox[0]);
        var submissionService = new CloudOrderSubmissionService(
            accessService, customerStore, orderStore, catalogStore, priceListStore, verificationService);

        var (verificationId, contact) = await IssueAndConfirmVerificationAsync(
            verificationService, sender, scope, "30111222333", "expired@example.com");

        // Past the 30-minute confirm-to-submit TTL (design.md "Verification
        // state shape") — the ticket is confirmed but no longer usable.
        clockBox[0] = now.AddMinutes(30).AddSeconds(1);

        var outcome = await submissionService.SubmitGuestAsync(
            scope, Guid.NewGuid(), verificationId, contact, Guid.NewGuid(),
            new[] { new SubmitOrderLine(Guid.NewGuid(), presentationId, Quantity: 1m) },
            Guid.NewGuid(), destination: null, hasAvailableStock: true, CancellationToken.None);

        Assert.Equal(OrderSubmissionOutcomeStatus.Denied, outcome.Status);
        Assert.Null(outcome.Order);
    }

    // --- Pricing denial does not burn the verification ------------------------

    [Fact]
    public async Task SubmitGuestAsync_WithNoEffectivePrice_DeniesTheWholeOrder_AndLeavesVerificationUnconsumed()
    {
        if (!_postgresAvailable) { Console.WriteLine("SKIPPED: no live Postgres."); return; }

        var orgId = Guid.NewGuid();
        var actorId = Guid.NewGuid();
        var scope = new CloudTenantScope(orgId);
        await SeedOrganizationAsync(orgId);
        var presentationId = await SeedPresentationAsync(scope, actorId);
        // No default price list, no price entry: zero effective rows.

        var (_, submissionService, verificationService, sender, _) = NewServicesWithSharedSender();
        var (verificationId, contact) = await IssueAndConfirmVerificationAsync(
            verificationService, sender, scope, "30111222333", "unpriced@example.com");

        var denied = await submissionService.SubmitGuestAsync(
            scope, Guid.NewGuid(), verificationId, contact, Guid.NewGuid(),
            new[] { new SubmitOrderLine(Guid.NewGuid(), presentationId, Quantity: 1m) },
            Guid.NewGuid(), destination: null, hasAvailableStock: true, CancellationToken.None);

        Assert.Equal(OrderSubmissionOutcomeStatus.Denied, denied.Status);
        Assert.Equal("no-effective-price", denied.Reason);
        Assert.Null(denied.Order);

        // The verification was NOT consumed: it can still be used for a
        // second attempt once a price exists (design.md "the guest is not
        // punished for it").
        var priceListId = await SeedDefaultPriceListAsync(scope, actorId);
        await PublishPriceAsync(scope, priceListId, presentationId, 15.00m, actorId);

        var retried = await submissionService.SubmitGuestAsync(
            scope, Guid.NewGuid(), verificationId, contact, Guid.NewGuid(),
            new[] { new SubmitOrderLine(Guid.NewGuid(), presentationId, Quantity: 1m) },
            Guid.NewGuid(), destination: null, hasAvailableStock: true, CancellationToken.None);

        Assert.Equal(OrderSubmissionOutcomeStatus.Accepted, retried.Status);
    }
}
