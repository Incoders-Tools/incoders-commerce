namespace Commerce.Cloud.Api.Tenancy;

/// <summary>
/// Minimal API endpoint filter wrapper around <see cref="TenantScopeResolver"/>
/// (design.md "HTTP style" — endpoint filters express the tenant-scope
/// concern more directly than action filters). Applied to every tenant-scoped
/// endpoint group in `Program.cs`. Returns 401 when unauthenticated, 403 when
/// authenticated but the <c>org_id</c> claim is missing/invalid, and
/// otherwise stashes the resolved <see cref="CloudTenantScope"/> for the
/// downstream handler via <see cref="GetScope"/> — handlers never derive
/// scope themselves and never accept one from the request body/route.
/// </summary>
public sealed class TenantScopeEndpointFilter : IEndpointFilter
{
    internal const string ScopeItemKey = "Commerce.Cloud.Api.CloudTenantScope";

    public ValueTask<object?> InvokeAsync(EndpointFilterInvocationContext context, EndpointFilterDelegate next)
    {
        if (!TenantScopeResolver.TryResolve(context.HttpContext.User, out var scope, out var failure))
        {
            return failure == TenantScopeFailureReason.NotAuthenticated
                ? ValueTask.FromResult<object?>(Results.Unauthorized())
                : ValueTask.FromResult<object?>(Results.Forbid());
        }

        context.HttpContext.Items[ScopeItemKey] = scope;
        return next(context);
    }

    /// <summary>
    /// Retrieves the scope stashed by this filter. Throws if the filter was
    /// not applied to the endpoint — a handler must never fall back to
    /// deriving scope another way.
    /// </summary>
    public static CloudTenantScope GetScope(HttpContext httpContext) =>
        httpContext.Items[ScopeItemKey] as CloudTenantScope
            ?? throw new InvalidOperationException(
                $"No {nameof(CloudTenantScope)} resolved on this request. " +
                $"Ensure {nameof(TenantScopeEndpointFilter)} is applied to the endpoint.");
}
