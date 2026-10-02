using System.Security.Claims;
using Commerce.Cloud.Api.Persistence;
using Commerce.Cloud.Api.Tenancy;
using Commerce.Domain.Customers;
using Commerce.Domain.Identity;
using Npgsql;

namespace Commerce.Cloud.Api.Endpoints;

/// <summary>
/// Organization-scoped customer catalog: business types
/// (`/customers/business-types`). Cities are NOT here any more: since 0028 they
/// are global core geography (<see cref="GeographyEndpoints"/>, `/geo/cities`).
/// Same authorization shape as
/// <see cref="CategoryEndpoints"/> - default cookie auth,
/// <see cref="TenantScopeEndpointFilter"/>, a store-loaded caller,
/// <see cref="ActingPermissions"/> - except that READING needs any permission
/// at all (the POS and the web customer form both populate their pickers from
/// it) and WRITING needs <see cref="Permission.ManageUsers"/>, the permission
/// that already guards the customer registry. There is no DELETE: an entry is
/// disabled with `isActive = false` so customers keep their reference.
/// `organization_id` is never a request field and a cross-organization id is
/// invisible under RLS (404).
/// </summary>
public static class MasterDataEndpoints
{
    public static IEndpointRouteBuilder MapMasterDataEndpoints(this IEndpointRouteBuilder app)
    {
        MapCatalog<PostgresBusinessTypeStore>(app, "/customers/business-types", "business-type");
        return app;
    }

    private static void MapCatalog<TStore>(IEndpointRouteBuilder app, string route, string errorPrefix)
        where TStore : PostgresMasterDataStore
    {
        var group = app.MapGroup(route)
            .RequireAuthorization()
            .AddEndpointFilter<TenantScopeEndpointFilter>();

        group.MapGet("", async (
            bool? includeInactive,
            HttpContext httpContext,
            PostgresUserAccountStore userStore,
            TStore store,
            CancellationToken ct) =>
        {
            var auth = await AuthorizeCallerAsync(httpContext, userStore, requireManage: false, ct);
            if (auth is null)
            {
                return Results.Forbid();
            }

            return Results.Ok(await store.ListAsync(auth.Value.Scope, includeInactive ?? false, ct));
        });

        group.MapPost("", async (
            MasterDataRequest request,
            HttpContext httpContext,
            PostgresUserAccountStore userStore,
            TStore store,
            CancellationToken ct) =>
        {
            var auth = await AuthorizeCallerAsync(httpContext, userStore, requireManage: true, ct);
            if (auth is null)
            {
                return Results.Forbid();
            }
            var (scope, caller) = auth.Value;

            var name = request.Name?.Trim() ?? string.Empty;
            var key = ResolveKey(request.Key, name);
            if (Validate(name, key) is { } invalid)
            {
                return invalid;
            }

            try
            {
                var created = await store.CreateAsync(
                    scope, new NewMasterDataEntry(Guid.NewGuid(), name, key, request.SortOrder ?? 0, request.IsActive ?? true),
                    "org-user", caller.Id, ct);
                return Results.Created($"{route}/{created.Id}", created);
            }
            catch (PostgresException ex) when (ex.SqlState == PostgresErrorCodes.UniqueViolation)
            {
                return Conflict(errorPrefix, ex);
            }
        });

        group.MapPut("/{id:guid}", async (
            Guid id,
            MasterDataRequest request,
            HttpContext httpContext,
            PostgresUserAccountStore userStore,
            TStore store,
            CancellationToken ct) =>
        {
            var auth = await AuthorizeCallerAsync(httpContext, userStore, requireManage: true, ct);
            if (auth is null)
            {
                return Results.Forbid();
            }
            var (scope, caller) = auth.Value;

            // A supplied key is normalized; an omitted one keeps the stored key
            // (renaming an entry must not silently change its stable key).
            var name = request.Name?.Trim() ?? string.Empty;
            var key = string.IsNullOrWhiteSpace(request.Key) ? null : MasterDataKey.FromName(request.Key);
            if (Validate(name, key ?? "-") is { } invalid)
            {
                return invalid;
            }

            try
            {
                var updated = await store.UpdateAsync(
                    scope, id, new UpdateMasterDataEntry(name, key, request.SortOrder, request.IsActive),
                    "org-user", caller.Id, ct);
                return updated is null ? Results.NotFound() : Results.Ok(updated);
            }
            catch (PostgresException ex) when (ex.SqlState == PostgresErrorCodes.UniqueViolation)
            {
                return Conflict(errorPrefix, ex);
            }
        });
    }

    private static string ResolveKey(string? suppliedKey, string name) =>
        MasterDataKey.FromName(string.IsNullOrWhiteSpace(suppliedKey) ? name : suppliedKey);

    private static IResult? Validate(string name, string key)
    {
        var errors = new Dictionary<string, string[]>();
        if (name.Length == 0)
        {
            errors["name"] = ["name is required."];
        }
        else if (key.Length == 0)
        {
            errors["key"] = ["key must contain at least one letter or digit."];
        }

        return errors.Count == 0 ? null : Results.ValidationProblem(errors);
    }

    private static IResult Conflict(string errorPrefix, PostgresException ex) =>
        Results.Conflict(new
        {
            error = ex.ConstraintName?.EndsWith("_key_uk", StringComparison.Ordinal) == true
                ? $"{errorPrefix}-key-in-use"
                : $"{errorPrefix}-name-in-use",
        });

    private static async Task<(CloudTenantScope Scope, UserAccount Caller)?> AuthorizeCallerAsync(
        HttpContext httpContext, PostgresUserAccountStore userStore, bool requireManage, CancellationToken ct)
    {
        var scope = TenantScopeEndpointFilter.GetScope(httpContext);

        var callerIdClaim = httpContext.User.FindFirst(ClaimTypes.NameIdentifier)?.Value;
        if (callerIdClaim is null || !Guid.TryParse(callerIdClaim, out var callerId))
        {
            return null;
        }

        var caller = await userStore.LoadActorAsync(scope.IdentityScope, callerId, ct);
        if (caller is null || caller.IsRevoked)
        {
            return null;
        }

        var permissions = ActingPermissions.For(caller, scope);
        var allowed = requireManage ? permissions.HasFlag(Permission.ManageUsers) : permissions != Permission.None;
        return allowed ? (scope, caller) : null;
    }
}

/// <summary>
/// Body of POST/PUT on a master data catalog. `Key` is derived from `Name` on
/// create when omitted and kept on update when omitted; `SortOrder` defaults
/// to 0 on create and is kept on update when omitted; `IsActive` defaults to
/// true on create and is kept on update when omitted.
/// </summary>
public sealed record MasterDataRequest(string? Name, string? Key = null, int? SortOrder = null, bool? IsActive = null);
