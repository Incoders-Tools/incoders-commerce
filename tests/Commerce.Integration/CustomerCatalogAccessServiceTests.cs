using Commerce.Application.Audit;
using Commerce.Application.Ordering;
using Commerce.Domain.Catalog;
using Commerce.Domain.Ordering;

namespace Commerce.Integration;

/// <summary>
/// Covers commerce-customer-identity Unit 3 task 3.1: `CustomerCatalogAccessService`
/// no longer accepts a body-built `CustomerOrderingAccess` at all — the ONLY
/// way it can learn about an access record is through the injected
/// `ICustomerOrderingAccessResolver` (design.md "How the persisted access
/// record is read"). These are pure unit tests: a stub resolver, no I/O, no
/// live Postgres. The real store-backed resolution is proven by
/// `CustomerOrderingAccessTests` (task 3.4).
/// </summary>
public sealed class CustomerCatalogAccessServiceTests
{
    /// <summary>
    /// Deterministic stand-in for <see cref="Commerce.Cloud.Api.Persistence.PostgresCustomerOrderingAccessStore"/>
    /// (never exercises Npgsql), so <see cref="CustomerCatalogAccessService"/>
    /// is testable without Postgres — exactly the reason the resolver
    /// abstraction exists.
    /// </summary>
    private sealed class StubResolver : ICustomerOrderingAccessResolver
    {
        private readonly CustomerOrderingAccess? _access;

        public StubResolver(CustomerOrderingAccess? access) => _access = access;

        public Task<CustomerOrderingAccess?> ResolveAsync(Guid organizationId, Guid credential, CancellationToken ct) =>
            Task.FromResult(_access);
    }

    private static Product NewProduct(Guid organizationId) => new(
        id: Guid.NewGuid(),
        organizationId: organizationId,
        name: "Original Product",
        categoryId: Guid.NewGuid(),
        defaultUnitId: Guid.NewGuid());

    [Fact]
    public async Task UnknownCredential_IsDenied_WithNotFound()
    {
        var auditSink = new InMemoryAuditSink();
        var service = new CustomerCatalogAccessService(new StubResolver(null), auditSink);
        var organizationId = Guid.NewGuid();

        var result = await service.AuthorizeAsync(organizationId, Guid.NewGuid(), Guid.NewGuid(), CancellationToken.None);

        Assert.False(result.Allowed);
        Assert.Equal("not-found", result.Reason);
        var entry = Assert.Single(auditSink.Entries);
        Assert.Equal("denied", entry.Outcome);
    }

    [Fact]
    public async Task DisabledCredential_IsDenied_WithCredentialRevoked()
    {
        var auditSink = new InMemoryAuditSink();
        var organizationId = Guid.NewGuid();
        var access = new CustomerOrderingAccess(organizationId, Guid.NewGuid(), Guid.NewGuid(), isEnabled: false);
        var service = new CustomerCatalogAccessService(new StubResolver(access), auditSink);

        var result = await service.AuthorizeAsync(organizationId, Guid.NewGuid(), Guid.NewGuid(), CancellationToken.None);

        Assert.False(result.Allowed);
        Assert.Equal("credential-revoked", result.Reason);
        var entry = Assert.Single(auditSink.Entries);
        Assert.Equal("denied", entry.Outcome);
    }

    [Fact]
    public async Task CrossOrganizationRow_IsDenied_WithTheIdenticalReasonStringAsUnknown()
    {
        var auditSink = new InMemoryAuditSink();
        var organizationId = Guid.NewGuid();
        var otherOrganizationId = Guid.NewGuid();
        var access = new CustomerOrderingAccess(organizationId, Guid.NewGuid(), Guid.NewGuid(), isEnabled: true);
        var service = new CustomerCatalogAccessService(new StubResolver(access), auditSink);

        var unknownResult = await new CustomerCatalogAccessService(new StubResolver(null), new InMemoryAuditSink())
            .AuthorizeAsync(otherOrganizationId, Guid.NewGuid(), Guid.NewGuid(), CancellationToken.None);
        var crossOrgResult = await service.AuthorizeAsync(otherOrganizationId, Guid.NewGuid(), Guid.NewGuid(), CancellationToken.None);

        Assert.False(crossOrgResult.Allowed);
        // Deliberate collision (design.md): a probe cannot distinguish "wrong
        // org" from "no such credential" — asserted by STRING EQUALITY, not
        // just both being "some denial".
        Assert.Equal(unknownResult.Reason, crossOrgResult.Reason);
        Assert.Equal("not-found", crossOrgResult.Reason);
    }

    [Fact]
    public async Task AllowedAndDeniedDecisions_AreBothAudited()
    {
        var auditSink = new InMemoryAuditSink();
        var organizationId = Guid.NewGuid();
        var access = new CustomerOrderingAccess(organizationId, Guid.NewGuid(), Guid.NewGuid(), isEnabled: true);
        var service = new CustomerCatalogAccessService(new StubResolver(access), auditSink);

        var allowed = await service.AuthorizeAsync(organizationId, Guid.NewGuid(), Guid.NewGuid(), CancellationToken.None);
        var denied = await service.AuthorizeAsync(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), CancellationToken.None);

        Assert.True(allowed.Allowed);
        Assert.False(denied.Allowed);
        Assert.Equal(2, auditSink.Entries.Count);
        Assert.Equal("allowed", auditSink.Entries[0].Outcome);
        Assert.Equal("denied", auditSink.Entries[1].Outcome);
    }

    [Fact]
    public async Task EnabledSameOrgCredential_IsAllowed_AndCataloguePermitted()
    {
        var organizationId = Guid.NewGuid();
        var access = new CustomerOrderingAccess(organizationId, Guid.NewGuid(), Guid.NewGuid(), isEnabled: true);
        var service = new CustomerCatalogAccessService(new StubResolver(access), new InMemoryAuditSink());
        var catalogue = new[] { NewProduct(organizationId) };

        var result = await service.GetPermittedCatalogueAsync(organizationId, Guid.NewGuid(), catalogue, Guid.NewGuid(), CancellationToken.None);

        Assert.True(result.Allowed);
        Assert.Single(result.Products);
    }

    /// <summary>
    /// Structural proof, not a runtime check: there is no overload of
    /// <see cref="CustomerCatalogAccessService"/> that accepts a
    /// <see cref="CustomerOrderingAccess"/> built from request data. If this
    /// test project compiles, the unrepresentable property holds — the old
    /// `Authorize(CustomerOrderingAccess, Guid, Guid)` shape is gone.
    /// </summary>
    [Fact]
    public void PublicSurface_HasNoOverloadAcceptingACustomerOrderingAccessInstance()
    {
        var methods = typeof(CustomerCatalogAccessService).GetMethods()
            .Where(m => m.DeclaringType == typeof(CustomerCatalogAccessService));

        Assert.All(methods, m => Assert.DoesNotContain(
            m.GetParameters(), p => p.ParameterType == typeof(CustomerOrderingAccess)));
    }
}
