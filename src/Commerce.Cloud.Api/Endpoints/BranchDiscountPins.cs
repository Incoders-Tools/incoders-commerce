using System.Security.Claims;
using Commerce.Cloud.Api.Persistence;
using Commerce.Cloud.Api.Tenancy;
using Commerce.Domain.Discounts;
using Commerce.Domain.Identity;

namespace Commerce.Cloud.Api.Endpoints;

/// <summary>
/// Set/rotate and read the STATUS of a branch discount PIN
/// (branch-discount-pin spec). Authorization mirrors the `/account/branches`
/// group: cookie auth, <see cref="TenantScopeEndpointFilter"/>, a store-loaded
/// caller and <see cref="ActingPermissions"/> (a system administrator acting on
/// a selected organization is elevated), requiring
/// <see cref="Permission.ManageBranchSettings"/>. The branch id comes from the
/// route and is checked against the caller organization first, so a foreign
/// branch is indistinguishable from a missing one (404). The PIN is accepted
/// only on the way in; no response ever carries it or its hash.
/// </summary>
public static class BranchDiscountPinEndpoints
{
    public static RouteGroupBuilder MapBranchDiscountPinEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/account/branches/{branchId:guid}/discount-pin")
            .RequireAuthorization()
            .AddEndpointFilter<TenantScopeEndpointFilter>();

        group.MapGet("", async (
            Guid branchId,
            HttpContext httpContext,
            PostgresUserAccountStore userStore,
            PostgresBranchDiscountPinStore pinStore,
            CancellationToken ct) =>
        {
            var (scope, _, denied) = await AuthorizeAsync(httpContext, userStore, ct);
            if (denied is not null) return denied;
            if (!await pinStore.BranchExistsAsync(scope!, branchId, ct)) return Results.NotFound();

            return Results.Ok(await pinStore.GetStatusAsync(scope!, branchId, ct));
        });

        group.MapPut("", async (
            Guid branchId,
            SetBranchDiscountPinRequest request,
            HttpContext httpContext,
            PostgresUserAccountStore userStore,
            PostgresBranchDiscountPinStore pinStore,
            CancellationToken ct) =>
        {
            var (scope, caller, denied) = await AuthorizeAsync(httpContext, userStore, ct);
            if (denied is not null) return denied;
            if (!await pinStore.BranchExistsAsync(scope!, branchId, ct)) return Results.NotFound();

            if (!BranchDiscountPin.IsValidPin(request.Pin))
            {
                return Results.ValidationProblem(new Dictionary<string, string[]>
                {
                    ["pin"] = [$"pin must be {BranchDiscountPin.MinLength} to {BranchDiscountPin.MaxLength} digits."],
                });
            }

            var status = await pinStore.SetAsync(
                scope!, branchId, BranchDiscountPin.Derive(request.Pin!), "org-user", caller!.Id, ct);
            return Results.Ok(status);
        });

        return group;
    }

    private static async Task<(CloudTenantScope? Scope, UserAccount? Caller, IResult? Denied)> AuthorizeAsync(
        HttpContext httpContext, PostgresUserAccountStore userStore, CancellationToken ct)
    {
        var scope = TenantScopeEndpointFilter.GetScope(httpContext);
        var claim = httpContext.User.FindFirst(ClaimTypes.NameIdentifier)?.Value;
        if (claim is null || !Guid.TryParse(claim, out var callerId))
        {
            return (null, null, Results.StatusCode(StatusCodes.Status403Forbidden));
        }

        var caller = await userStore.LoadActorAsync(scope.IdentityScope, callerId, ct);
        if (caller is null || caller.IsRevoked || !ActingPermissions.For(caller, scope).HasFlag(Permission.ManageBranchSettings))
        {
            return (null, null, Results.StatusCode(StatusCodes.Status403Forbidden));
        }

        return (scope, caller, null);
    }
}

public sealed record SetBranchDiscountPinRequest(string? Pin);
