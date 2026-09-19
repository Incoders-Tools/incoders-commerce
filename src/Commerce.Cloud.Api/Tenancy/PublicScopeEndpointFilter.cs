namespace Commerce.Cloud.Api.Tenancy;

/// <summary>
/// Minimal API endpoint filter that stamps <see cref="CloudTenantScope"/>
/// from the configured <see cref="GuestOrderTarget"/> for a claim-less
/// (anonymous) request — the guest counterpart to
/// <see cref="TenantScopeEndpointFilter"/>, which requires an authenticated
/// principal (design.md "Org/branch resolution point"; tenant-access-
/// foundation spec.md "Anonymous Request Organization Resolution"). Writes
/// to the SAME item key as <see cref="TenantScopeEndpointFilter"/> so
/// <see cref="TenantScopeEndpointFilter.GetScope"/> retrieves it identically
/// regardless of which filter stamped the request.
/// </summary>
public sealed class PublicScopeEndpointFilter : IEndpointFilter
{
    private readonly GuestOrderTarget _target;

    public PublicScopeEndpointFilter(GuestOrderTarget target) => _target = target;

    public ValueTask<object?> InvokeAsync(EndpointFilterInvocationContext context, EndpointFilterDelegate next)
    {
        context.HttpContext.Items[TenantScopeEndpointFilter.ScopeItemKey] = _target.Scope;
        return next(context);
    }

    /// <summary>Retrieves the scope stashed by this filter (or by <see cref="TenantScopeEndpointFilter"/>).</summary>
    public static CloudTenantScope GetScope(HttpContext httpContext) => TenantScopeEndpointFilter.GetScope(httpContext);
}
