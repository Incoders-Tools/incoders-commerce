using Commerce.Cloud.Api.Tenancy;
using Microsoft.AspNetCore.Http;

namespace Commerce.Integration;

/// <summary>
/// commerce-guest-ordering Phase 4 (Unit 4, task 4.3): the claim-less
/// counterpart to <see cref="TenantScopeEndpointFilter"/> — stamps
/// <see cref="CloudTenantScope"/> from the configured
/// <see cref="GuestOrderTarget"/> instead of an authenticated principal's
/// claims (tenant-access-foundation spec.md "Anonymous Request Organization
/// Resolution"). No I/O; not yet wired into `Program.cs` (Unit 5).
/// </summary>
public sealed class PublicScopeEndpointFilterTests
{
    [Fact]
    public async Task InvokeAsync_StampsScopeFromGuestOrderTarget_ForClaimLessRequest()
    {
        var target = new GuestOrderTarget(Guid.NewGuid(), Guid.NewGuid());
        var filter = new PublicScopeEndpointFilter(target);
        var httpContext = new DefaultHttpContext();
        var invocationContext = new DefaultEndpointFilterInvocationContext(httpContext);

        await filter.InvokeAsync(invocationContext, _ => ValueTask.FromResult<object?>(Results.Ok()));

        var scope = PublicScopeEndpointFilter.GetScope(httpContext);
        Assert.Equal(target.OrganizationId, scope.OrganizationId);
    }
}
