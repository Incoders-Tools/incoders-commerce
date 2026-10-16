using System.Net;
using System.Security.Claims;
using Commerce.Cloud.Api.Tenancy;
using Microsoft.AspNetCore.Mvc.Testing;

namespace Commerce.Integration;

/// <summary>
/// Covers Unit 2 task 2.1: `/health` liveness without DB, `/health/ready`
/// unhealthy on a broken DB, and `TenantScopeResolver`'s claim ->
/// `CloudTenantScope` derivation/rejection (the pure logic
/// `TenantScopeEndpointFilter` wraps — see design.md "Browser auth" /
/// "Device auth"). The host tests use a deliberately unreachable connection
/// string so they never depend on `deploy/dev/compose.yaml` being up; the
/// live-Postgres and pooler PoC scenarios are covered separately by
/// `PostgresCloudInboxStoreTests` and `PoolerScopingTests`. Joined to the
/// shared "Postgres" collection (even though it needs no live database
/// itself) so its `WebApplicationFactory<Program>` host — which still boots
/// every other DI-registered connection pool at startup — never runs
/// concurrently with the rest of the live-Postgres test suite and spikes
/// `max_connections`.
/// </summary>
[Collection("Postgres")]
public sealed class CloudApiHostTests : IClassFixture<WebApplicationFactory<Program>>, IDisposable
{
    private readonly WebApplicationFactory<Program> _factory;

    public CloudApiHostTests(WebApplicationFactory<Program> factory)
    {
        _factory = factory.WithWebHostBuilder(builder =>
        {
            builder.UseSetting(
                "ConnectionStrings:Commerce",
                // Unreachable on purpose: proves /health/ready degrades
                // gracefully instead of crashing the process, and proves
                // /health (liveness) never touches the database at all.
                "Host=169.254.0.1;Port=5432;Database=nope;Username=nope;Password=nope;Timeout=1");
        });
    }

    public void Dispose() => _factory.Dispose();

    [Fact]
    public async Task Health_Liveness_Succeeds_WithoutDatabase()
    {
        var client = _factory.CreateClient();

        var response = await client.GetAsync("/health");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task HealthReady_ReportsUnhealthy_WhenDatabaseUnreachable_WithoutCrashingProcess()
    {
        var client = _factory.CreateClient();

        var response = await client.GetAsync("/health/ready");

        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);

        // The process itself must still be up and answering other routes —
        // an unhealthy dependency never takes the whole host down.
        var liveness = await client.GetAsync("/health");
        Assert.Equal(HttpStatusCode.OK, liveness.StatusCode);
    }

    // --- Tenant scope claim derivation / rejection --------------------------

    [Fact]
    public void TenantScopeResolver_DerivesScope_FromOrgIdClaim()
    {
        var organizationId = Guid.NewGuid();
        var user = AuthenticatedUser(organizationId);

        var resolved = TenantScopeResolver.TryResolve(user, out var scope, out _);

        Assert.True(resolved);
        Assert.Equal(organizationId, scope!.OrganizationId);
    }

    [Fact]
    public void TenantScopeResolver_Rejects_UnauthenticatedPrincipal()
    {
        var anonymous = new ClaimsPrincipal(new ClaimsIdentity()); // IsAuthenticated == false

        var resolved = TenantScopeResolver.TryResolve(anonymous, out var scope, out var failure);

        Assert.False(resolved);
        Assert.Null(scope);
        Assert.Equal(TenantScopeFailureReason.NotAuthenticated, failure);
    }

    [Fact]
    public void TenantScopeResolver_Rejects_MissingOrgClaim()
    {
        var identity = new ClaimsIdentity(authenticationType: "TestAuth"); // authenticated, no org_id claim
        var user = new ClaimsPrincipal(identity);

        var resolved = TenantScopeResolver.TryResolve(user, out var scope, out var failure);

        Assert.False(resolved);
        Assert.Null(scope);
        Assert.Equal(TenantScopeFailureReason.MissingOrInvalidClaim, failure);
    }

    [Fact]
    public void TenantScopeResolver_Rejects_MalformedOrgClaim()
    {
        var user = AuthenticatedUserWithRawClaim("not-a-guid");

        var resolved = TenantScopeResolver.TryResolve(user, out var scope, out var failure);

        Assert.False(resolved);
        Assert.Null(scope);
        Assert.Equal(TenantScopeFailureReason.MissingOrInvalidClaim, failure);
    }

    [Fact]
    public void TenantScopeResolver_Rejects_EmptyGuidOrgClaim()
    {
        // A spoofed/degenerate claim value must never resolve to a usable scope.
        var user = AuthenticatedUserWithRawClaim(Guid.Empty.ToString());

        var resolved = TenantScopeResolver.TryResolve(user, out var scope, out var failure);

        Assert.False(resolved);
        Assert.Null(scope);
        Assert.Equal(TenantScopeFailureReason.MissingOrInvalidClaim, failure);
    }

    private static ClaimsPrincipal AuthenticatedUser(Guid organizationId) =>
        AuthenticatedUserWithRawClaim(organizationId.ToString());

    private static ClaimsPrincipal AuthenticatedUserWithRawClaim(string orgClaimValue)
    {
        var identity = new ClaimsIdentity(
            new[] { new Claim(TenantScopeResolver.OrganizationClaimType, orgClaimValue) },
            authenticationType: "TestAuth");
        return new ClaimsPrincipal(identity);
    }
}
