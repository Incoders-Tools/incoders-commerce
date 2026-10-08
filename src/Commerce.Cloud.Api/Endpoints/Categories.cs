using System.Security.Claims;
using Commerce.Cloud.Api.Persistence;
using Commerce.Cloud.Api.Tenancy;
using Commerce.Domain.Catalog;
using Commerce.Domain.Identity;
using Npgsql;

namespace Commerce.Cloud.Api.Endpoints;

/// <summary>
/// Organization-scoped product categories (catalog-categories spec). The
/// authorization shape is <see cref="CatalogEndpoints"/>' verbatim — default
/// cookie auth, <see cref="TenantScopeEndpointFilter"/>, a store-loaded
/// caller, `ActingPermissions` (so a system administrator acting on a
/// selected organization is elevated) — except that categories are shared by
/// every branch, so no branch selection is required, and READING only needs
/// the caller to hold any permission at all. Writing needs
/// <see cref="Permission.ManageCatalog"/>. `organization_id` is never a
/// request field, and a cross-organization id is invisible under RLS.
/// </summary>
public static class CategoryEndpoints
{
    public static RouteGroupBuilder MapCategoryEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/catalog/categories")
            .RequireAuthorization()
            .AddEndpointFilter<TenantScopeEndpointFilter>();

        group.MapGet("", async (
            HttpContext httpContext,
            PostgresUserAccountStore userStore,
            PostgresCategoryStore categoryStore,
            CancellationToken ct) =>
        {
            var auth = await AuthorizeCallerAsync(httpContext, userStore, requireManage: false, ct);
            if (auth is null)
            {
                return Results.Forbid();
            }

            return Results.Ok(await categoryStore.ListAsync(auth.Value.Scope, ct));
        });

        group.MapPost("", async (
            CategoryRequest request,
            HttpContext httpContext,
            PostgresUserAccountStore userStore,
            PostgresCategoryStore categoryStore,
            CancellationToken ct) =>
        {
            var auth = await AuthorizeCallerAsync(httpContext, userStore, requireManage: true, ct);
            if (auth is null)
            {
                return Results.Forbid();
            }
            var (scope, caller) = auth.Value;

            var invalid = Validate(request);
            if (invalid is not null)
            {
                return invalid;
            }

            try
            {
                var created = await categoryStore.CreateAsync(
                    scope, new NewCategory(Guid.NewGuid(), request.Name.Trim(), request.IconKey, request.ShowInPos ?? true, request.PosSortOrder ?? 0),
                    "org-user", caller.Id, ct);
                return Results.Created($"/catalog/categories/{created.Id}", created);
            }
            catch (PostgresException ex) when (ex.SqlState == PostgresErrorCodes.UniqueViolation)
            {
                return Results.Conflict(new { error = "category-name-in-use" });
            }
        });

        group.MapPut("/{categoryId:guid}", async (
            Guid categoryId,
            CategoryRequest request,
            HttpContext httpContext,
            PostgresUserAccountStore userStore,
            PostgresCategoryStore categoryStore,
            CancellationToken ct) =>
        {
            var auth = await AuthorizeCallerAsync(httpContext, userStore, requireManage: true, ct);
            if (auth is null)
            {
                return Results.Forbid();
            }
            var (scope, caller) = auth.Value;

            var invalid = Validate(request);
            if (invalid is not null)
            {
                return invalid;
            }

            try
            {
                var updated = await categoryStore.UpdateAsync(
                    scope, categoryId, request.Name.Trim(), request.IconKey, "org-user", caller.Id, ct, request.ShowInPos, request.PosSortOrder);
                return updated is null ? Results.NotFound() : Results.Ok(updated);
            }
            catch (PostgresException ex) when (ex.SqlState == PostgresErrorCodes.UniqueViolation)
            {
                return Results.Conflict(new { error = "category-name-in-use" });
            }
        });

        group.MapDelete("/{categoryId:guid}", async (
            Guid categoryId,
            HttpContext httpContext,
            PostgresUserAccountStore userStore,
            PostgresCategoryStore categoryStore,
            CancellationToken ct) =>
        {
            var auth = await AuthorizeCallerAsync(httpContext, userStore, requireManage: true, ct);
            if (auth is null)
            {
                return Results.Forbid();
            }
            var (scope, caller) = auth.Value;

            try
            {
                var deleted = await categoryStore.DeleteAsync(scope, categoryId, "org-user", caller.Id, ct);
                return deleted ? Results.NoContent() : Results.NotFound();
            }
            catch (PostgresException ex) when (ex.SqlState == PostgresErrorCodes.ForeignKeyViolation)
            {
                // products_category_org_fk (0018): some product, in any
                // branch, still references this category.
                return Results.Conflict(new { error = "category-in-use" });
            }
        });

        return group;
    }

    private static IResult? Validate(CategoryRequest request)
    {
        var errors = new Dictionary<string, string[]>();
        if (string.IsNullOrWhiteSpace(request.Name))
        {
            errors["name"] = ["name is required."];
        }

        if (!CategoryIcons.IsValid(request.IconKey))
        {
            errors["iconKey"] = [$"iconKey must be one of: {string.Join(", ", CategoryIcons.Keys)}."];
        }

        if (request.PosSortOrder is < 0 or > 9999)
        {
            errors["posSortOrder"] = ["posSortOrder is between 0 and 9999."];
        }

        return errors.Count == 0 ? null : Results.ValidationProblem(errors);
    }

    /// <summary>
    /// Caller loaded through the store (never trusted from a claim alone),
    /// not revoked, and holding at least one permission for a read, or
    /// <see cref="Permission.ManageCatalog"/> for a write. Null on any
    /// failure so every call site maps uniformly to 403.
    /// </summary>
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
        var allowed = requireManage ? permissions.HasFlag(Permission.ManageCatalog) : permissions != Permission.None;
        return allowed ? (scope, caller) : null;
    }
}

/// <summary>
/// A category. ShowInPos: whether the POS category rail offers it as a filter; PosSortOrder: its place there (then by
/// name). Both optional: a new category is shown, at 0; an update without them keeps the stored values.
/// </summary>
public sealed record CategoryRequest(string Name, string IconKey, bool? ShowInPos = null, int? PosSortOrder = null);
