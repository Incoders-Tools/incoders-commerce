using Commerce.Cloud.Api.Tenancy;
using Commerce.Domain.Sync;

namespace Commerce.Cloud.Api.Endpoints;

/// <summary>
/// Thin mapping onto <see cref="CloudSyncReceiver"/> / <see cref="ICloudInboxStore"/>
/// (design.md "HTTP style" — no business logic here). Used by
/// Commerce.Pos.Windows / Commerce.BranchNode over the device bearer scheme.
/// </summary>
public static class SyncEndpoints
{
    public static RouteGroupBuilder MapSyncEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/sync")
            .RequireAuthorization("DeviceBearer")
            .AddEndpointFilter<TenantScopeEndpointFilter>();

        group.MapPost("/inbox", (SyncEnvelope envelope, HttpContext httpContext, CloudSyncReceiver receiver) =>
        {
            var scope = TenantScopeEndpointFilter.GetScope(httpContext);
            var result = receiver.Receive(scope, envelope);
            return Results.Ok(result);
        });

        group.MapPost("/inbox/{operationId:guid}/ack", (Guid operationId, HttpContext httpContext, CloudSyncReceiver receiver) =>
        {
            var scope = TenantScopeEndpointFilter.GetScope(httpContext);
            return receiver.Acknowledge(scope, operationId) ? Results.Ok() : Results.NotFound();
        });

        group.MapGet("/inbox", (HttpContext httpContext, ICloudInboxStore store) =>
        {
            var scope = TenantScopeEndpointFilter.GetScope(httpContext);
            return Results.Ok(store.GetInboxFor(scope));
        });

        group.MapGet("/inbox/{operationId:guid}/status", (Guid operationId, HttpContext httpContext, ICloudInboxStore store) =>
        {
            var scope = TenantScopeEndpointFilter.GetScope(httpContext);
            var status = store.GetStatus(scope, operationId);
            return status is null ? Results.NotFound() : Results.Ok(status);
        });

        return group;
    }
}
