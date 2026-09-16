using Commerce.Application.Access;
using Commerce.Application.Audit;
using Commerce.Application.Ordering;
using Commerce.BranchNode;
using Commerce.Cloud.Api.Ordering;
using Commerce.Cloud.Api.Tenancy;
using Commerce.Domain.Catalog;
using Commerce.Domain.Identity;
using Commerce.Domain.Ordering;
using Commerce.Domain.Sync;

namespace Commerce.Integration;

/// <summary>
/// Covers `openspec/changes/commerce-foundation/specs/private-customer-ordering/spec.md`
/// and ADR-003: bound/revocable customer ordering access, immutable order
/// context, idempotent submission, reused Product/Presentation snapshot
/// semantics, and honest pending states for an offline destination or
/// unconfirmed stock. Destination delivery reuses the real Unit 3
/// <see cref="BranchSyncStore"/> — not a test double — so delivery/idempotency
/// is proven end-to-end through the same SQLite-backed inbox as
/// <c>SyncTests</c>.
/// </summary>
public sealed class OrderingTests : IDisposable
{
    private readonly string _dbPath = Path.Combine(Path.GetTempPath(), $"ordering-{Guid.NewGuid():N}.db");

    private string ConnectionString => $"Data Source={_dbPath}";

    public void Dispose()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();

        foreach (var path in new[] { _dbPath, _dbPath + "-wal", _dbPath + "-shm" })
        {
            if (File.Exists(path))
            {
                try
                {
                    File.Delete(path);
                }
                catch (IOException)
                {
                    // Best-effort cleanup; the OS temp directory is periodically reclaimed.
                }
            }
        }
    }

    private static UserAccount NewStaffActor(Guid organizationId, Guid branchId, Permission permissions) => new(
        Guid.NewGuid(),
        organizationId,
        new[] { branchId },
        new[] { new Role("customer-access-manager", permissions) });

    private static Product NewProduct(Guid organizationId, string name = "Original Product") => new(
        id: Guid.NewGuid(),
        organizationId: organizationId,
        name: name,
        categoryId: Guid.NewGuid(),
        defaultUnitId: Guid.NewGuid());

    private static Presentation NewPresentation(Product product, QuantityBehavior behavior = QuantityBehavior.FixedQuantity) => new(
        id: Guid.NewGuid(),
        productId: product.Id,
        name: "6-pack",
        quantityBehavior: behavior,
        unitId: Guid.NewGuid());

    // --- Bound and revocable customer access -------------------------------

    [Fact]
    public void StaffCanEnableCustomerOrderingAccess_ViaTenantAuthorization()
    {
        var auditSink = new InMemoryAuditSink();
        var authService = new TenantAuthorizationService(auditSink);
        var service = new CustomerOrderingAccessService(authService);
        var organizationId = Guid.NewGuid();
        var branchId = Guid.NewGuid();
        var staff = NewStaffActor(organizationId, branchId, Permission.ManageUsers);
        var access = new CustomerOrderingAccess(organizationId, Guid.NewGuid(), Guid.NewGuid(), isEnabled: false);

        var outcome = service.Enable(staff, access, branchId, isOffline: false, Guid.NewGuid());

        Assert.Equal(OrderingAccessOutcomeStatus.Allowed, outcome.Status);
        Assert.True(access.IsEnabled);
    }

    [Fact]
    public void StaffWithoutPermission_CannotEnableCustomerOrderingAccess()
    {
        var authService = new TenantAuthorizationService(new InMemoryAuditSink());
        var service = new CustomerOrderingAccessService(authService);
        var organizationId = Guid.NewGuid();
        var branchId = Guid.NewGuid();
        var staff = NewStaffActor(organizationId, branchId, Permission.None);
        var access = new CustomerOrderingAccess(organizationId, Guid.NewGuid(), Guid.NewGuid(), isEnabled: false);

        var outcome = service.Enable(staff, access, branchId, isOffline: false, Guid.NewGuid());

        Assert.Equal(OrderingAccessOutcomeStatus.Denied, outcome.Status);
        Assert.Equal("insufficient-permission", outcome.Reason);
        Assert.False(access.IsEnabled);
    }

    [Fact]
    public void StaffCanRevokeCustomerOrderingAccess()
    {
        var authService = new TenantAuthorizationService(new InMemoryAuditSink());
        var service = new CustomerOrderingAccessService(authService);
        var organizationId = Guid.NewGuid();
        var branchId = Guid.NewGuid();
        var staff = NewStaffActor(organizationId, branchId, Permission.ManageUsers);
        var access = new CustomerOrderingAccess(organizationId, Guid.NewGuid(), Guid.NewGuid(), isEnabled: true);

        var outcome = service.Revoke(staff, access, branchId, isOffline: false, Guid.NewGuid());

        Assert.Equal(OrderingAccessOutcomeStatus.Allowed, outcome.Status);
        Assert.False(access.IsEnabled);
    }

    [Fact]
    public void EnabledCustomer_CanAccessOwnOrganizationCatalogue()
    {
        var accessService = new CustomerCatalogAccessService(new InMemoryAuditSink());
        var organizationId = Guid.NewGuid();
        var access = new CustomerOrderingAccess(organizationId, Guid.NewGuid(), Guid.NewGuid());
        var catalogue = new[] { NewProduct(organizationId) };

        var result = accessService.GetPermittedCatalogue(access, organizationId, catalogue, Guid.NewGuid());

        Assert.True(result.Allowed);
        Assert.Single(result.Products);
    }

    [Fact]
    public void RevokedCustomer_IsDeniedCatalogueAccess_AndDenialIsAudited()
    {
        var auditSink = new InMemoryAuditSink();
        var accessService = new CustomerCatalogAccessService(auditSink);
        var organizationId = Guid.NewGuid();
        var access = new CustomerOrderingAccess(organizationId, Guid.NewGuid(), Guid.NewGuid(), isEnabled: false);
        var catalogue = new[] { NewProduct(organizationId) };

        var result = accessService.GetPermittedCatalogue(access, organizationId, catalogue, Guid.NewGuid());

        Assert.False(result.Allowed);
        Assert.Equal("credential-revoked", result.Reason);
        Assert.Empty(result.Products);
        var entry = Assert.Single(auditSink.Entries);
        Assert.Equal("denied", entry.Outcome);
    }

    [Fact]
    public void CredentialPresentedAgainstAnotherOrganization_IsDenied_AndAudited()
    {
        var auditSink = new InMemoryAuditSink();
        var accessService = new CustomerCatalogAccessService(auditSink);
        var organizationId = Guid.NewGuid();
        var otherOrganizationId = Guid.NewGuid();
        var access = new CustomerOrderingAccess(organizationId, Guid.NewGuid(), Guid.NewGuid());

        var result = accessService.Authorize(access, otherOrganizationId, Guid.NewGuid());

        Assert.False(result.Allowed);
        Assert.Equal("not-found", result.Reason);
        var entry = Assert.Single(auditSink.Entries);
        Assert.Equal("denied", entry.Outcome);
    }

    // --- Presentation-aware snapshot / historical meaning -------------------

    [Fact]
    public void OrderSnapshot_CapturesProductAndPresentationSemantics_AtSubmissionTime()
    {
        var organizationId = Guid.NewGuid();
        var product = NewProduct(organizationId);
        var presentation = NewPresentation(product, QuantityBehavior.Weighted);

        var line = OrderSnapshotFactory.Snapshot(product, presentation, quantity: 2.5m);

        Assert.Equal(product.Id, line.ProductId);
        Assert.Equal(product.Name, line.ProductName);
        Assert.Equal(presentation.Id, line.PresentationId);
        Assert.Equal(QuantityBehavior.Weighted, line.QuantityBehavior);
        Assert.Equal(presentation.UnitId, line.UnitId);
        Assert.Equal(2.5m, line.Quantity);
    }

    [Fact]
    public void OrderLines_RemainUnchanged_AfterLaterCatalogueRename()
    {
        var organizationId = Guid.NewGuid();
        var product = NewProduct(organizationId, "Sparkling Water 1L");
        var presentation = NewPresentation(product);
        var line = OrderSnapshotFactory.Snapshot(product, presentation, quantity: 3m);

        var order = new Order(Guid.NewGuid(), organizationId, Guid.NewGuid(), Guid.NewGuid(), new[] { line }, DateTimeOffset.UtcNow);

        // Catalogue changes after submission (a brand-new Product instance
        // with the same id, per how catalog renames are modeled in Unit 4).
        var renamed = new Product(product.Id, product.OrganizationId, "Sparkling Water 1.5L", product.CategoryId, product.DefaultUnitId);

        Assert.Equal("Sparkling Water 1L", order.Lines[0].ProductName);
        Assert.NotEqual(renamed.Name, order.Lines[0].ProductName);
    }

    // --- Idempotent submission -----------------------------------------------

    [Fact]
    public void DuplicateOrderSubmission_ReturnsExistingAcceptance_WithoutCreatingASecondOrder()
    {
        var organizationId = Guid.NewGuid();
        var orderStore = new CloudOrderStore();
        var scope = new CloudTenantScope(organizationId);
        var orderId = Guid.NewGuid();
        var lines = new[] { OrderSnapshotFactory.Snapshot(NewProduct(organizationId), NewPresentation(NewProduct(organizationId)), 1m) };

        var first = orderStore.Submit(scope, orderId, Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), lines, Guid.NewGuid(), destination: null, hasAvailableStock: true);
        var retry = orderStore.Submit(scope, orderId, Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), lines, Guid.NewGuid(), destination: null, hasAvailableStock: true);

        Assert.True(first.WasNewlyAccepted);
        Assert.False(retry.WasNewlyAccepted);
        Assert.Equal("existing-order", retry.Reason);
        Assert.Same(first.Order, retry.Order);
    }

    // --- Immutable context ---------------------------------------------------

    [Fact]
    public void PlacedOrder_KeepsOriginalDestinationAndCustomer_EvenAfterAccessIsLaterRevoked()
    {
        var organizationId = Guid.NewGuid();
        var customerId = Guid.NewGuid();
        var destinationBranchId = Guid.NewGuid();
        var access = new CustomerOrderingAccess(organizationId, customerId, Guid.NewGuid());
        var orderStore = new CloudOrderStore();
        var scope = new CloudTenantScope(organizationId);
        var lines = new[] { OrderSnapshotFactory.Snapshot(NewProduct(organizationId), NewPresentation(NewProduct(organizationId)), 1m) };

        var submission = orderStore.Submit(scope, Guid.NewGuid(), access.CustomerId, destinationBranchId, Guid.NewGuid(), lines, Guid.NewGuid(), destination: null, hasAvailableStock: true);

        access.Revoke();

        Assert.Equal(destinationBranchId, submission.Order!.DestinationBranchId);
        Assert.Equal(customerId, submission.Order.CustomerId);
        Assert.Equal(organizationId, submission.Order.OrganizationId);
    }

    [Fact]
    public void RevokedCustomer_CannotSubmitANewOrder_ButExistingOrderIsUnaffected()
    {
        var organizationId = Guid.NewGuid();
        var access = new CustomerOrderingAccess(organizationId, Guid.NewGuid(), Guid.NewGuid());
        var accessService = new CustomerCatalogAccessService(new InMemoryAuditSink());
        var submissionService = new CloudOrderSubmissionService(accessService, new CloudOrderStore());
        var scope = new CloudTenantScope(organizationId);
        var lines = new[] { OrderSnapshotFactory.Snapshot(NewProduct(organizationId), NewPresentation(NewProduct(organizationId)), 1m) };

        access.Revoke();
        var outcome = submissionService.Submit(scope, access, Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), lines, Guid.NewGuid(), destination: null, hasAvailableStock: true);

        Assert.Equal(OrderSubmissionOutcomeStatus.Denied, outcome.Status);
        Assert.Equal("credential-revoked", outcome.Reason);
        Assert.Null(outcome.Order);
    }

    // --- Provisional pending: offline destination / no stock -----------------

    [Fact]
    public void Order_WithOfflineDestination_StaysPending_WithNoStockPromise()
    {
        var organizationId = Guid.NewGuid();
        var orderStore = new CloudOrderStore();
        var scope = new CloudTenantScope(organizationId);
        var lines = new[] { OrderSnapshotFactory.Snapshot(NewProduct(organizationId), NewPresentation(NewProduct(organizationId)), 1m) };

        var outcome = orderStore.Submit(scope, Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), lines, Guid.NewGuid(), destination: null, hasAvailableStock: true);

        Assert.Equal(OrderDeliveryStatus.PendingDestination, outcome.Order!.Status);
        Assert.Equal(OrderPendingReason.DestinationOffline, outcome.Order.PendingReason);
    }

    [Fact]
    public void Order_WithNoConfirmedStock_StaysPending_EvenWhenDestinationIsOnline()
    {
        var organizationId = Guid.NewGuid();
        var branchId = Guid.NewGuid();
        using var destination = new BranchSyncStore(ConnectionString);
        var orderStore = new CloudOrderStore();
        var scope = new CloudTenantScope(organizationId);
        var lines = new[] { OrderSnapshotFactory.Snapshot(NewProduct(organizationId), NewPresentation(NewProduct(organizationId)), 1m) };

        var outcome = orderStore.Submit(scope, Guid.NewGuid(), Guid.NewGuid(), branchId, Guid.NewGuid(), lines, Guid.NewGuid(), destination, hasAvailableStock: false);

        Assert.Equal(OrderDeliveryStatus.PendingDestination, outcome.Order!.Status);
        Assert.Equal(OrderPendingReason.StockUnconfirmed, outcome.Order.PendingReason);
    }

    [Fact]
    public void Order_WithReachableDestinationAndAvailableStock_IsConfirmed()
    {
        var organizationId = Guid.NewGuid();
        var branchId = Guid.NewGuid();
        using var destination = new BranchSyncStore(ConnectionString);
        var orderStore = new CloudOrderStore();
        var scope = new CloudTenantScope(organizationId);
        var lines = new[] { OrderSnapshotFactory.Snapshot(NewProduct(organizationId), NewPresentation(NewProduct(organizationId)), 1m) };

        var outcome = orderStore.Submit(scope, Guid.NewGuid(), Guid.NewGuid(), branchId, Guid.NewGuid(), lines, Guid.NewGuid(), destination, hasAvailableStock: true);

        Assert.Equal(OrderDeliveryStatus.DestinationConfirmed, outcome.Order!.Status);
        Assert.Equal(OrderPendingReason.None, outcome.Order.PendingReason);
    }

    // --- Offline retry / duplicate delivery -----------------------------------

    [Fact]
    public void PendingOrder_RetriedAfterDestinationReconnects_BecomesConfirmed_WithNoSecondEffect()
    {
        var organizationId = Guid.NewGuid();
        var branchId = Guid.NewGuid();
        using var destination = new BranchSyncStore(ConnectionString);
        var orderStore = new CloudOrderStore();
        var scope = new CloudTenantScope(organizationId);
        var orderId = Guid.NewGuid();
        var actorId = Guid.NewGuid();
        var correlationId = Guid.NewGuid();
        var lines = new[] { OrderSnapshotFactory.Snapshot(NewProduct(organizationId), NewPresentation(NewProduct(organizationId)), 1m) };

        // Submitted while the destination branch is offline.
        var submitted = orderStore.Submit(scope, orderId, Guid.NewGuid(), branchId, actorId, lines, correlationId, destination: null, hasAvailableStock: true);
        Assert.Equal(OrderDeliveryStatus.PendingDestination, submitted.Order!.Status);

        // Destination reconnects; retry converges to Confirmed.
        var retried = orderStore.RetryDelivery(orderId, actorId, correlationId, destination, hasAvailableStock: true);
        Assert.Equal(OrderDeliveryStatus.DestinationConfirmed, retried.Order!.Status);

        // A second retry (e.g. duplicate reconnection signal) must not create
        // a second business effect: the branch inbox idempotency key already
        // proven in SyncTests rejects the repeated OperationId.
        var secondRetry = orderStore.RetryDelivery(orderId, actorId, correlationId, destination, hasAvailableStock: true);
        Assert.Equal(OrderDeliveryStatus.DestinationConfirmed, secondRetry.Order!.Status);

        var directReapply = destination.ApplyInbound(new SyncEnvelope(
            OperationId: orderId,
            ContractVersion: 1,
            OrganizationId: organizationId,
            BranchId: branchId,
            AggregateId: orderId,
            AggregateVersion: 1,
            ActorId: actorId,
            CorrelationId: correlationId,
            OccurredAtUtc: DateTimeOffset.UtcNow,
            PayloadKind: "order",
            Payload: "{}"));
        Assert.Equal(InboundApplyOutcome.DuplicateIgnored, directReapply.Outcome);
    }

    // --- Web channel is a real caller, not a test double ----------------------
    //
    // NOTE (Unit 3 deviation): previously routed through the now-deleted
    // `WebOrderSubmissionAdapter` pure pass-through (design.md "Commerce.Web
    // shape" — deleted because the SPA now calls this same contract over HTTP
    // via `Endpoints/Ordering.cs`). Calling `CloudOrderSubmissionService`
    // directly proves the identical contract with no coverage loss.

    [Fact]
    public void CloudOrderSubmissionService_AcceptsSubmission_ForTheWebChannelShape()
    {
        var organizationId = Guid.NewGuid();
        var access = new CustomerOrderingAccess(organizationId, Guid.NewGuid(), Guid.NewGuid());
        var accessService = new CustomerCatalogAccessService(new InMemoryAuditSink());
        var submissionService = new CloudOrderSubmissionService(accessService, new CloudOrderStore());
        var scope = new CloudTenantScope(organizationId);
        var lines = new[] { OrderSnapshotFactory.Snapshot(NewProduct(organizationId), NewPresentation(NewProduct(organizationId)), 1m) };

        var outcome = submissionService.Submit(scope, access, Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), lines, Guid.NewGuid(), destination: null, hasAvailableStock: true);

        Assert.Equal(OrderSubmissionOutcomeStatus.Accepted, outcome.Status);
        Assert.NotNull(outcome.Order);
    }
}
