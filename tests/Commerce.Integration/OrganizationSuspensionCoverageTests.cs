using Commerce.Cloud.Api.Authentication;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;

namespace Commerce.Integration;

/// <summary>
/// Guard for <see cref="OrganizationSuspensionMiddleware"/> (organization-account-standing T4 review): the block acts
/// only on endpoints that require authorization, so an endpoint that checks the caller inside its handler without
/// declaring <c>RequireAuthorization</c> would stay open to a suspended organization. Every mapped route must be
/// one of: authorization required, explicitly anonymous, explicitly allowed while suspended, or on a surface the block
/// leaves alone on purpose (device/POS, customer, public ordering, test seed, health).
/// </summary>
public sealed class OrganizationSuspensionCoverageTests : IClassFixture<WebApplicationFactory<Program>>
{
    private static readonly string[] SurfacesOutsideTheBlock =
        ["/device", "/sync", "/customer", "/public", "/internal/test-seed", "/health"];

    private readonly WebApplicationFactory<Program> _factory;

    public OrganizationSuspensionCoverageTests(WebApplicationFactory<Program> factory) => _factory = factory;

    [Fact]
    public void EveryStaffRoute_IsCoveredByTheSuspensionBlock_OrExplicitlyOutsideIt()
    {
        var uncovered = _factory.Services.GetRequiredService<EndpointDataSource>().Endpoints
            .OfType<RouteEndpoint>()
            .Where(endpoint => endpoint.RoutePattern.RawText is { } route
                && !route.StartsWith("{", StringComparison.Ordinal) // the SPA fallback (index.html) must load while suspended
                && !SurfacesOutsideTheBlock.Any(surface => IsUnder(route, surface)))
            .Where(endpoint =>
                endpoint.Metadata.GetMetadata<IAuthorizeData>() is null
                && endpoint.Metadata.GetMetadata<IAllowAnonymous>() is null
                && endpoint.Metadata.GetMetadata<AllowWhileOrganizationSuspendedAttribute>() is null)
            .Select(endpoint => endpoint.DisplayName ?? endpoint.RoutePattern.RawText)
            .ToList();

        Assert.True(uncovered.Count == 0,
            "Routes that neither require authorization nor opt out of the suspension block:\n" + string.Join("\n", uncovered));
    }

    [Theory]
    [InlineData("/customer", true)]
    [InlineData("/customer/me", true)]
    [InlineData("/customers", false)]
    [InlineData("/customers/{id:guid}", false)]
    [InlineData("/devices", false)]
    public void ASurfaceMatchesWholeSegmentsOnly(string route, bool under)
    {
        Assert.Equal(under, IsUnder(route, route.StartsWith("/dev", StringComparison.Ordinal) ? "/device" : "/customer"));
    }

    /// <summary>
    /// Whole path segments only: "/customer" covers "/customer" and "/customer/...", never the staff "/customers" routes.
    /// </summary>
    private static bool IsUnder(string route, string surface) =>
        route.Equals(surface, StringComparison.OrdinalIgnoreCase)
        || route.StartsWith(surface + "/", StringComparison.OrdinalIgnoreCase);
}
