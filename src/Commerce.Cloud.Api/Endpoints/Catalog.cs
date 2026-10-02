using System.Security.Claims;
using Commerce.Application.Management;
using Commerce.Cloud.Api.Management;
using Commerce.Cloud.Api.Persistence;
using Commerce.Cloud.Api.Tenancy;
using Commerce.Domain.Catalog;
using Commerce.Domain.Identity;
using Npgsql;

namespace Commerce.Cloud.Api.Endpoints;

/// <summary>
/// Persisted catalog CRUD (commerce-pricing-engine design.md "Verified
/// deviation" / Work Unit 1). Before this rework, `Product`/`Presentation`
/// were constructed entirely from the request body on every call — there was
/// no `products`/`presentations` table anywhere. Every mutating route here
/// now reads/writes <see cref="PostgresCatalogStore"/>, mirroring
/// <c>CustomerEndpoints</c>'s <c>adminGroup</c> authorization shape verbatim:
/// <c>RequireAuthorization</c> (default cookie) + <see cref="TenantScopeEndpointFilter"/>
/// + a store-loaded caller + <see cref="Permission.ManageCatalog"/>.
/// `organization_id` is never a request field.
///
/// The rename route keeps forwarding to <see cref="CloudCatalogManagementAdapter"/>
/// (Component Reuse Policy: no authorization/business logic duplicated here),
/// but the product it authorizes over is now the REAL persisted row, looked
/// up by id — a caller can no longer rename a product that was never
/// created, and a client-supplied `currentName`/`categoryId`/`defaultUnitId`
/// has nowhere to go (the `AccessEnabled`-removal idiom): those fields were
/// removed from <see cref="RenameProductRequest"/> because the only source
/// of truth for them is now the persisted row, never the caller.
/// </summary>
public static class CatalogEndpoints
{
    public static RouteGroupBuilder MapCatalogEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/catalog")
            .RequireAuthorization()
            .AddEndpointFilter<TenantScopeEndpointFilter>();

        // Soft-deleted products (0036) are left out unless `?includeInactive=true`.
        group.MapGet("/products", async (
            bool? includeInactive,
            HttpContext httpContext,
            PostgresUserAccountStore userStore,
            PostgresCatalogStore catalogStore,
            CancellationToken ct) =>
        {
            var branchFailure = BranchSelectionRequirement.Enforce(httpContext);
            if (branchFailure is not null)
            {
                return branchFailure;
            }

            var auth = await AuthorizeCallerAsync(httpContext, userStore, ct);
            if (auth is null)
            {
                return Results.Forbid();
            }

            var products = await catalogStore.ListProductsAsync(auth.Value.Scope, ct, includeInactive == true);
            return Results.Ok(products);
        });

        group.MapGet("/products/{productId:guid}", async (
            Guid productId,
            HttpContext httpContext,
            PostgresUserAccountStore userStore,
            PostgresCatalogStore catalogStore,
            CancellationToken ct) =>
        {
            var branchFailure = BranchSelectionRequirement.Enforce(httpContext);
            if (branchFailure is not null)
            {
                return branchFailure;
            }

            var auth = await AuthorizeCallerAsync(httpContext, userStore, ct);
            if (auth is null)
            {
                return Results.Forbid();
            }

            var product = await catalogStore.FindProductAsync(auth.Value.Scope, productId, ct);
            return product is null ? Results.NotFound() : Results.Ok(product);
        });

        group.MapPost("/products", async (
            CreateProductRequest request,
            HttpContext httpContext,
            PostgresUserAccountStore userStore,
            PostgresCatalogStore catalogStore,
            PostgresCategoryStore categoryStore,
            CancellationToken ct) =>
        {
            var branchFailure = BranchSelectionRequirement.Enforce(httpContext);
            if (branchFailure is not null)
            {
                return branchFailure;
            }

            var auth = await AuthorizeCallerAsync(httpContext, userStore, ct);
            if (auth is null)
            {
                return Results.Forbid();
            }
            var (scope, caller) = auth.Value;

            if (string.IsNullOrWhiteSpace(request.Name))
            {
                return Results.ValidationProblem(new Dictionary<string, string[]>
                {
                    ["name"] = ["name is required."],
                });
            }

            // catalog-categories spec: a product references exactly one
            // category of ITS organization. No category id means the
            // organization's default "Sin categoría" (created on demand); a
            // foreign or unknown id is a validation failure, never a 500 from
            // the composite foreign key.
            Guid categoryId;
            if (request.CategoryId is null || request.CategoryId == Guid.Empty)
            {
                categoryId = await categoryStore.EnsureDefaultAsync(scope, ct);
            }
            else if (await categoryStore.FindAsync(scope, request.CategoryId.Value, ct) is not null)
            {
                categoryId = request.CategoryId.Value;
            }
            else
            {
                return CategoryNotFound();
            }

            var product = await catalogStore.CreateProductAsync(
                scope,
                new NewProduct(Guid.NewGuid(), request.Name.Trim(), categoryId, request.DefaultUnitId, caller.Id),
                "org-user", caller.Id, ct);

            return Results.Created($"/catalog/products/{product.Id}", product);
        });

        group.MapPut("/products/{productId:guid}/category", async (
            Guid productId,
            ChangeProductCategoryRequest request,
            HttpContext httpContext,
            PostgresUserAccountStore userStore,
            PostgresCatalogStore catalogStore,
            PostgresCategoryStore categoryStore,
            CancellationToken ct) =>
        {
            var branchFailure = BranchSelectionRequirement.Enforce(httpContext);
            if (branchFailure is not null)
            {
                return branchFailure;
            }

            var auth = await AuthorizeCallerAsync(httpContext, userStore, ct);
            if (auth is null)
            {
                return Results.Forbid();
            }
            var (scope, caller) = auth.Value;

            var existing = await catalogStore.FindProductAsync(scope, productId, ct);
            if (existing is null)
            {
                return Results.NotFound();
            }

            if (await categoryStore.FindAsync(scope, request.CategoryId, ct) is null)
            {
                return CategoryNotFound();
            }

            var updated = await catalogStore.UpdateProductAsync(
                scope, productId,
                new UpdateProduct(existing.Name, request.CategoryId, existing.DefaultUnitId),
                "org-user", caller.Id, ct);

            return updated is null ? Results.NotFound() : Results.Ok(updated);
        });

        group.MapPost("/products/{productId:guid}/deactivate", (
            Guid productId, HttpContext httpContext, PostgresUserAccountStore userStore, PostgresCatalogStore catalogStore,
            CancellationToken ct) => SetActiveAsync(productId, false, httpContext, userStore, catalogStore, ct));

        group.MapPost("/products/{productId:guid}/reactivate", (
            Guid productId, HttpContext httpContext, PostgresUserAccountStore userStore, PostgresCatalogStore catalogStore,
            CancellationToken ct) => SetActiveAsync(productId, true, httpContext, userStore, catalogStore, ct));

        group.MapPost("/products/{productId:guid}/rename", async (
            Guid productId,
            RenameProductRequest request,
            HttpContext httpContext,
            CloudCatalogManagementAdapter adapter,
            PostgresUserAccountStore userStore,
            PostgresCatalogStore catalogStore,
            CancellationToken ct) =>
        {
            var branchFailure = BranchSelectionRequirement.Enforce(httpContext);
            if (branchFailure is not null)
            {
                return branchFailure;
            }

            var auth = await AuthorizeCallerAsync(httpContext, userStore, ct);
            if (auth is null)
            {
                return Results.Forbid();
            }
            var (scope, actor) = auth.Value;

            var existing = await catalogStore.FindProductAsync(scope, productId, ct);
            if (existing is null)
            {
                return Results.NotFound();
            }

            // The branch this rename authorizes and applies against is
            // ALWAYS the request's own resolved scope (validated by
            // BranchSelectionRequirement/TenantScopeEndpointFilter above),
            // never a caller-submitted field — the old `TargetBranchId`
            // body field is gone; a cross-branch write is already
            // impossible because `existing`/the later UPDATE are both
            // scoped by this same branch under RLS, but the authorization
            // decision itself must agree, not merely the storage layer.
            var outcome = await adapter.RenameProductAsync(
                scope,
                actor,
                existing.ToDomain(),
                scope.BranchId!.Value,
                request.NewName,
                request.IsOffline,
                request.CorrelationId,
                ct);

            if (outcome.Status != ManagementOutcomeStatus.Allowed || outcome.UpdatedProduct is null)
            {
                return Results.Json(outcome, statusCode: StatusCodes.Status403Forbidden);
            }

            await catalogStore.UpdateProductAsync(
                scope, productId,
                new UpdateProduct(outcome.UpdatedProduct.Name, outcome.UpdatedProduct.CategoryId, outcome.UpdatedProduct.DefaultUnitId),
                "org-user", actor.Id, ct);

            return Results.Ok(outcome);
        });

        group.MapGet("/presentations", async (
            bool? includeInactive,
            HttpContext httpContext,
            PostgresUserAccountStore userStore,
            PostgresCatalogStore catalogStore,
            CancellationToken ct) =>
        {
            var branchFailure = BranchSelectionRequirement.Enforce(httpContext);
            if (branchFailure is not null)
            {
                return branchFailure;
            }

            var auth = await AuthorizeCallerAsync(httpContext, userStore, ct);
            if (auth is null)
            {
                return Results.Forbid();
            }

            var presentations = await catalogStore.ListPresentationsAsync(auth.Value.Scope, ct, includeInactive == true);
            return Results.Ok(presentations);
        });

        group.MapPost("/presentations", async (
            CreatePresentationRequest request,
            HttpContext httpContext,
            PostgresUserAccountStore userStore,
            PostgresCatalogStore catalogStore,
            CancellationToken ct) =>
        {
            var branchFailure = BranchSelectionRequirement.Enforce(httpContext);
            if (branchFailure is not null)
            {
                return branchFailure;
            }

            var auth = await AuthorizeCallerAsync(httpContext, userStore, ct);
            if (auth is null)
            {
                return Results.Forbid();
            }
            var (scope, caller) = auth.Value;

            if (string.IsNullOrWhiteSpace(request.Name))
            {
                return Results.ValidationProblem(new Dictionary<string, string[]>
                {
                    ["name"] = ["name is required."],
                });
            }

            var product = await catalogStore.FindProductAsync(scope, request.ProductId, ct);
            if (product is null)
            {
                return Results.NotFound();
            }

            if (!product.IsActive)
            {
                // A soft-deleted product (0036) takes no new presentations until it is reactivated.
                return Results.Conflict(new { error = "product-inactive" });
            }

            PresentationRecord created;
            try
            {
                created = await catalogStore.CreatePresentationAsync(
                    scope,
                    new NewPresentation(
                        Guid.NewGuid(), request.ProductId, request.Name.Trim(),
                        request.QuantityBehavior, request.UnitId,
                        string.IsNullOrWhiteSpace(request.IdentificationCode) ? null : request.IdentificationCode.Trim(),
                        caller.Id),
                    "org-user", caller.Id, ct);
            }
            catch (PostgresException ex) when (ex.SqlState == PostgresErrorCodes.UniqueViolation)
            {
                // presentations_org_branch_code_uk (B7 U4 — was
                // presentations_org_code_uk): same identification_code
                // already used by another presentation in this BRANCH
                // (catalog-item-identification spec "Branch-Owned Catalog").
                return Results.Conflict(new { error = "identification-code-in-use" });
            }

            return Results.Created($"/catalog/presentations/{created.Id}", created);
        });

        group.MapPut("/presentations/{presentationId:guid}", async (
            Guid presentationId,
            UpdatePresentationRequest request,
            HttpContext httpContext,
            PostgresUserAccountStore userStore,
            PostgresCatalogStore catalogStore,
            CancellationToken ct) =>
        {
            var branchFailure = BranchSelectionRequirement.Enforce(httpContext);
            if (branchFailure is not null)
            {
                return branchFailure;
            }

            var auth = await AuthorizeCallerAsync(httpContext, userStore, ct);
            if (auth is null)
            {
                return Results.Forbid();
            }
            var (scope, caller) = auth.Value;

            PresentationRecord? updated;
            try
            {
                updated = await catalogStore.UpdatePresentationAsync(
                    scope, presentationId,
                    new UpdatePresentation(
                        request.Name, request.QuantityBehavior, request.UnitId,
                        string.IsNullOrWhiteSpace(request.IdentificationCode) ? null : request.IdentificationCode.Trim()),
                    "org-user", caller.Id, ct);
            }
            catch (PostgresException ex) when (ex.SqlState == PostgresErrorCodes.UniqueViolation)
            {
                return Results.Conflict(new { error = "identification-code-in-use" });
            }

            return updated is null ? Results.NotFound() : Results.Ok(updated);
        });

        group.MapPost("/copy", async (
            CopyCatalogRequest request,
            HttpContext httpContext,
            PostgresUserAccountStore userStore,
            PostgresOrganizationStore organizationStore,
            CatalogCopyStore copyStore,
            CancellationToken ct) =>
        {
            var branchFailure = BranchSelectionRequirement.Enforce(httpContext);
            if (branchFailure is not null)
            {
                return branchFailure;
            }

            var auth = await AuthorizeCallerAsync(httpContext, userStore, ct);
            if (auth is null)
            {
                return Results.Forbid();
            }
            var (scope, caller) = auth.Value;

            // B7 U5b design decision: the header-selected branch
            // (`TenantScopeEndpointFilter.BranchSelectorHeader`, already
            // validated against the caller's own scope above the same way
            // every other `/catalog/*` route validates it) MUST be the
            // SOURCE branch, never the target — this mirrors the Catalog
            // screen's own navigation model (an admin looking at ONE
            // branch's catalog copies it somewhere else), keeps the existing
            // header-authorization machinery as the single source-branch
            // gate, and leaves the target branch as the one value this
            // route validates for itself below (a business-admin's own
            // BranchScope for a plain caller, or ANY branch of the selected
            // organization for a sysadmin acting on it).
            if (request.SourceBranchId != scope.BranchId)
            {
                return Results.BadRequest(new { error = "source-branch-must-be-the-selected-branch" });
            }

            if (request.TargetBranchId == request.SourceBranchId)
            {
                return Results.BadRequest(new { error = "target-branch-must-differ-from-source" });
            }

            // Same identical-403 shape `TenantScopeEndpointFilter` uses for
            // `X-Branch-Id` (tenant-access-foundation spec: "without
            // revealing whether that branch exists") — an unknown,
            // cross-organization, or out-of-scope target branch all deny the
            // same way.
            var targetMatches = await organizationStore.ListBranchesAsync(scope, [request.TargetBranchId], ct);
            var targetExistsInOrganization = targetMatches.Count > 0;
            var callerMayActOnTarget = scope.IsActingOnSelectedOrganization && caller.IsSystemAdmin
                ? targetExistsInOrganization
                : caller.BranchScope.Contains(request.TargetBranchId);

            if (!targetExistsInOrganization || !callerMayActOnTarget)
            {
                return Results.StatusCode(StatusCodes.Status403Forbidden);
            }

            CatalogCopyOutcome outcome;
            try
            {
                outcome = await copyStore.CopyCatalogAsync(
                    scope, request.SourceBranchId, request.TargetBranchId, request.ProductIds,
                    "org-user", caller.Id, ct);
            }
            catch (CatalogCopyProductNotFoundException)
            {
                return Results.NotFound(new { error = "product-not-found-in-source-branch" });
            }

            return Results.Ok(new CopyCatalogResponse(
                outcome.ProductsCopied,
                outcome.PresentationsCopied,
                outcome.Skipped
                    .Select(s => new SkippedPresentationDto(s.PresentationId, s.IdentificationCode, s.Reason))
                    .ToList(),
                outcome.PriceListId,
                outcome.PriceEntriesCopied));
        });

        return group;
    }

    /// <summary>
    /// Soft deletion (0036): same gate as every catalog write (selected branch, store-loaded caller, ManageCatalog); the store
    /// audits the change and bumps the replica cursor. History (receptions, stock, sales) is never touched.
    /// </summary>
    private static async Task<IResult> SetActiveAsync(
        Guid productId, bool active, HttpContext httpContext, PostgresUserAccountStore userStore,
        PostgresCatalogStore catalogStore, CancellationToken ct)
    {
        var branchFailure = BranchSelectionRequirement.Enforce(httpContext);
        if (branchFailure is not null)
        {
            return branchFailure;
        }

        var auth = await AuthorizeCallerAsync(httpContext, userStore, ct);
        if (auth is null)
        {
            return Results.Forbid();
        }
        var (scope, caller) = auth.Value;

        var product = await catalogStore.SetProductActiveAsync(scope, productId, active, "org-user", caller.Id, ct);
        return product is null ? Results.NotFound() : Results.Ok(product);
    }

    private static IResult CategoryNotFound() =>
        Results.ValidationProblem(new Dictionary<string, string[]>
        {
            ["categoryId"] = ["categoryId does not match a category of this organization."],
        });

    /// <summary>
    /// The <c>CustomerEndpoints.AuthorizeCallerAsync</c> shape, reused
    /// verbatim for this file's routes: caller id from the NameIdentifier
    /// claim, loaded through the store (never trusted from a claim alone),
    /// not revoked, and holding <see cref="Permission.ManageCatalog"/>.
    /// Returns <see langword="null"/> on ANY failure so every call site maps
    /// uniformly to <see cref="Results.Forbid()"/>.
    /// </summary>
    private static async Task<(CloudTenantScope Scope, UserAccount Caller)?> AuthorizeCallerAsync(
        HttpContext httpContext, PostgresUserAccountStore userStore, CancellationToken ct)
    {
        var scope = TenantScopeEndpointFilter.GetScope(httpContext);

        var callerIdClaim = httpContext.User.FindFirst(ClaimTypes.NameIdentifier)?.Value;
        if (callerIdClaim is null || !Guid.TryParse(callerIdClaim, out var callerId))
        {
            return null;
        }

        var caller = await userStore.LoadActorAsync(scope.IdentityScope, callerId, ct);
        if (caller is null || caller.IsRevoked || !ActingPermissions.For(caller, scope).HasFlag(Permission.ManageCatalog))
        {
            return null;
        }

        return (scope, caller);
    }
}

/// <summary>`CategoryId` is optional: omitted (or empty) means the organization's default "Sin categoría".</summary>
public sealed record CreateProductRequest(string Name, Guid? CategoryId, Guid DefaultUnitId);

public sealed record ChangeProductCategoryRequest(Guid CategoryId);

/// <summary>
/// `CurrentName`/`CategoryId`/`DefaultUnitId` were removed from this shape
/// (the `AccessEnabled`-removal idiom): the only source of truth for those
/// fields is now the persisted `products` row, never the caller — there is
/// nowhere left to put a value that would have been ignored anyway.
/// `TargetBranchId` was removed the same way (B7 U4): the only branch a
/// rename can ever apply to is the request's own resolved
/// <see cref="CloudTenantScope.BranchId"/>, never a caller-submitted field.
/// </summary>
public sealed record RenameProductRequest(
    string NewName,
    bool IsOffline,
    Guid CorrelationId);

public sealed record CreatePresentationRequest(
    Guid ProductId, string Name, QuantityBehavior QuantityBehavior, Guid UnitId, string? IdentificationCode);

public sealed record UpdatePresentationRequest(
    string Name, QuantityBehavior QuantityBehavior, Guid UnitId, string? IdentificationCode);

/// <summary>
/// `SourceBranchId` MUST equal the request's own selected branch (see
/// `CatalogEndpoints.MapCatalogEndpoints`'s `/copy` handler) — carried as an
/// explicit field anyway (rather than implied silently) so a client that
/// gets it wrong sees a named 400, not a mismatched no-op. `ProductIds`
/// null/empty means "the whole catalog".
/// </summary>
public sealed record CopyCatalogRequest(Guid SourceBranchId, Guid TargetBranchId, IReadOnlyList<Guid>? ProductIds);

public sealed record SkippedPresentationDto(Guid PresentationId, string? IdentificationCode, string Reason);

public sealed record CopyCatalogResponse(
    int ProductsCopied,
    int PresentationsCopied,
    IReadOnlyList<SkippedPresentationDto> Skipped,
    Guid? PriceListId,
    int PriceEntriesCopied);

public sealed record RoleDto(string Name, Permission Permissions);
