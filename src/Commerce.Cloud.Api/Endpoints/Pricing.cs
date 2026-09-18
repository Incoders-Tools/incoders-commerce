using System.Security.Claims;
using Commerce.Application.Pricing.Import;
using Commerce.Cloud.Api.Persistence;
using Commerce.Cloud.Api.Tenancy;
using Commerce.Domain.Identity;
using Npgsql;

namespace Commerce.Cloud.Api.Endpoints;

/// <summary>
/// Admin pricing API (commerce-pricing-engine design.md Work Unit 5): price
/// list CRUD, appending an effective-dated entry (the ONLY write path onto
/// `price_list_entries`, via <see cref="PostgresPriceListStore.AppendEntryAsync"/>),
/// and per-presentation history. Reuses <c>CustomerEndpoints</c>/<c>Account.cs</c>'s
/// exact <c>adminGroup</c> authorization shape verbatim: <c>RequireAuthorization</c>
/// (default cookie) + <see cref="TenantScopeEndpointFilter"/> + a store-loaded
/// caller + <see cref="Permission.ManageCatalog"/> — corrected after review from
/// design.md's original literal "ManageUsers" gate, which conflated user
/// management with catalog/commercial-pricing management (a caller who can
/// manage the catalog but not create staff users should still be able to
/// publish a price). The web `PriceListsScreen` still lives inside the
/// existing `RequireAdmin` (ManageUsers-gated) navigation area for now — no
/// role in `RoleCatalog` currently holds `ManageCatalog` without also holding
/// `ManageUsers`, so this is a real API-level correction with no current
/// user-visible navigation gap; revisit the web guard only if a
/// catalog-only role is introduced later. `organization_id` is never a
/// request field — it always comes from the tenant scope, so cross-org
/// creation is unrepresentable and a cross-org target is invisible under RLS
/// (404, identical to a nonexistent id) — the same non-disclosure pattern as
/// <c>CustomerEndpoints</c> and <c>Account.cs</c>'s admin routes.
///
/// Work Unit 9 (Excel supplier-price import) adds supplier-mapping CRUD and
/// the import upload/review/commit/reject routes below, on the SAME
/// <c>adminGroup</c> authorization shape. The upload endpoint runs every
/// <see cref="ImportGuards"/> check before a single cell is read; a file
/// that fails ANY guard creates NO `price_import_batches` row (design.md
/// "Import state machine": no `Uploaded` state, so an un-reviewable batch
/// cannot exist).
/// </summary>
public static class PricingEndpoints
{
    public static RouteGroupBuilder MapPricingEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/pricing")
            .RequireAuthorization()
            .AddEndpointFilter<TenantScopeEndpointFilter>();

        group.MapGet("/price-lists", async (
            HttpContext httpContext,
            PostgresUserAccountStore userStore,
            PostgresPriceListStore priceListStore,
            CancellationToken ct) =>
        {
            var auth = await AuthorizeCallerAsync(httpContext, userStore, ct);
            if (auth is null)
            {
                return Results.Forbid();
            }

            var priceLists = await priceListStore.ListPriceListsAsync(auth.Value.Scope, ct);
            return Results.Ok(priceLists);
        });

        group.MapGet("/price-lists/{priceListId:guid}", async (
            Guid priceListId,
            HttpContext httpContext,
            PostgresUserAccountStore userStore,
            PostgresPriceListStore priceListStore,
            CancellationToken ct) =>
        {
            var auth = await AuthorizeCallerAsync(httpContext, userStore, ct);
            if (auth is null)
            {
                return Results.Forbid();
            }

            // Scoped to the CALLER's org: price_lists_tenant_isolation RLS
            // returns zero rows for a cross-org target, so this is null
            // identically to "no such price list" — same pattern as
            // CustomerEndpoints/Account.cs admin routes.
            var priceList = await priceListStore.FindPriceListAsync(auth.Value.Scope, priceListId, ct);
            return priceList is null ? Results.NotFound() : Results.Ok(priceList);
        });

        group.MapPost("/price-lists", async (
            CreatePriceListRequest request,
            HttpContext httpContext,
            PostgresUserAccountStore userStore,
            PostgresPriceListStore priceListStore,
            CancellationToken ct) =>
        {
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

            PriceListRecord created;
            try
            {
                created = await priceListStore.CreatePriceListAsync(
                    scope, new NewPriceList(Guid.NewGuid(), request.Name.Trim(), request.IsDefault, caller.Id),
                    "org-user", caller.Id, ct);
            }
            catch (PostgresException ex) when (ex.SqlState == PostgresErrorCodes.UniqueViolation)
            {
                // price_lists_one_default: a second is_default=true list for
                // this organization (design.md "Which price list resolves").
                return Results.Conflict(new { error = "default-price-list-already-exists" });
            }

            return Results.Created($"/pricing/price-lists/{created.Id}", created);
        });

        group.MapGet("/price-lists/{priceListId:guid}/presentations/{presentationId:guid}/history", async (
            Guid priceListId,
            Guid presentationId,
            HttpContext httpContext,
            PostgresUserAccountStore userStore,
            PostgresPriceListStore priceListStore,
            CancellationToken ct) =>
        {
            var auth = await AuthorizeCallerAsync(httpContext, userStore, ct);
            if (auth is null)
            {
                return Results.Forbid();
            }

            // A cross-org priceListId resolves to zero rows under RLS just
            // like a nonexistent one — the history endpoint does not need a
            // separate existence check, an empty history is the correct
            // response either way.
            var history = await priceListStore.ListHistoryAsync(auth.Value.Scope, priceListId, presentationId, ct);
            return Results.Ok(history);
        });

        group.MapPost("/price-lists/{priceListId:guid}/entries", async (
            Guid priceListId,
            AppendPriceEntryRequest request,
            HttpContext httpContext,
            PostgresUserAccountStore userStore,
            PostgresPriceListStore priceListStore,
            CancellationToken ct) =>
        {
            var auth = await AuthorizeCallerAsync(httpContext, userStore, ct);
            if (auth is null)
            {
                return Results.Forbid();
            }
            var (scope, caller) = auth.Value;

            if (request.UnitPrice <= 0)
            {
                return Results.ValidationProblem(new Dictionary<string, string[]>
                {
                    ["unitPrice"] = ["unitPrice must be greater than zero."],
                });
            }

            // Cross-org (or nonexistent) price list is invisible under RLS —
            // identical 404 to the GET-by-id route above.
            var priceList = await priceListStore.FindPriceListAsync(scope, priceListId, ct);
            if (priceList is null)
            {
                return Results.NotFound();
            }

            PriceListEntryRecord created;
            try
            {
                created = await priceListStore.AppendEntryAsync(
                    scope,
                    new NewPriceListEntry(
                        Guid.NewGuid(), priceListId, request.PresentationId, request.UnitPrice,
                        request.EffectiveFrom, "Manual", ImportBatchId: null, caller.Id),
                    "org-user", caller.Id, ct);
            }
            catch (PostgresException ex) when (ex.SqlState == PostgresErrorCodes.UniqueViolation)
            {
                // price_list_entries_one_per_day: a same-day double-publish
                // for this (price list, presentation) — 409, not a coin flip
                // (design.md "Effective-dating shape").
                return Results.Conflict(new { error = "entry-already-exists-for-date" });
            }
            catch (PostgresException ex) when (ex.SqlState == PostgresErrorCodes.ForeignKeyViolation)
            {
                return Results.NotFound();
            }

            return Results.Created($"/pricing/price-lists/{priceListId}/presentations/{request.PresentationId}/history", created);
        });

        // --- Supplier mappings (Work Unit 9: "Per-Supplier Saved Column Mapping") ---

        group.MapPost("/supplier-mappings", async (
            CreateSupplierMappingRequest request,
            HttpContext httpContext,
            PostgresUserAccountStore userStore,
            PostgresPriceListStore priceListStore,
            CancellationToken ct) =>
        {
            var auth = await AuthorizeCallerAsync(httpContext, userStore, ct);
            if (auth is null)
            {
                return Results.Forbid();
            }
            var (scope, caller) = auth.Value;

            if (string.IsNullOrWhiteSpace(request.SupplierName) || string.IsNullOrWhiteSpace(request.SheetName)
                || string.IsNullOrWhiteSpace(request.CodeColumn) || string.IsNullOrWhiteSpace(request.PriceColumn)
                || request.HeaderRow <= 0)
            {
                return Results.ValidationProblem(new Dictionary<string, string[]>
                {
                    ["mapping"] = ["supplierName, sheetName, codeColumn, priceColumn are required and headerRow must be positive."],
                });
            }

            SupplierPriceMappingRecord created;
            try
            {
                created = await priceListStore.CreateSupplierMappingAsync(
                    scope,
                    new NewSupplierPriceMapping(
                        Guid.NewGuid(), request.SupplierName.Trim(), request.SheetName.Trim(),
                        request.HeaderRow, request.CodeColumn.Trim(), request.PriceColumn.Trim(), caller.Id),
                    "org-user", caller.Id, ct);
            }
            catch (PostgresException ex) when (ex.SqlState == PostgresErrorCodes.UniqueViolation)
            {
                // supplier_price_mappings_org_name_uk: this supplier already has a saved mapping.
                return Results.Conflict(new { error = "supplier-mapping-already-exists" });
            }

            return Results.Created($"/pricing/supplier-mappings/{created.Id}", created);
        });

        group.MapGet("/supplier-mappings", async (
            HttpContext httpContext,
            PostgresUserAccountStore userStore,
            PostgresPriceListStore priceListStore,
            CancellationToken ct) =>
        {
            var auth = await AuthorizeCallerAsync(httpContext, userStore, ct);
            if (auth is null)
            {
                return Results.Forbid();
            }

            var mappings = await priceListStore.ListSupplierMappingsAsync(auth.Value.Scope, ct);
            return Results.Ok(mappings);
        });

        // --- Excel import lifecycle (Work Unit 9: "Staged -> Committed | Rejected", no "Uploaded" state) ---

        group.MapPost("/imports", async (
            HttpContext httpContext,
            PostgresUserAccountStore userStore,
            PostgresPriceListStore priceListStore,
            PostgresCatalogStore catalogStore,
            CancellationToken ct) =>
        {
            var auth = await AuthorizeCallerAsync(httpContext, userStore, ct);
            if (auth is null)
            {
                return Results.Forbid();
            }
            var (scope, caller) = auth.Value;

            if (!httpContext.Request.HasFormContentType)
            {
                return Results.BadRequest(new { error = "expected-multipart-form" });
            }

            var form = await httpContext.Request.ReadFormAsync(ct);
            var file = form.Files.GetFile("file");
            if (file is null || file.Length == 0 || !Guid.TryParse(form["supplierMappingId"], out var supplierMappingId))
            {
                return Results.BadRequest(new { error = "file-and-supplierMappingId-required" });
            }

            // Fail fast on the size guard before even buffering the file —
            // ImportGuards.ValidateFile re-checks this once bytes are read,
            // but there is no reason to buffer an obviously oversized upload.
            if (file.Length > ImportGuards.MaxFileSizeBytes)
            {
                return Results.BadRequest(new { error = "file-too-large" });
            }

            var mapping = await priceListStore.FindSupplierMappingAsync(scope, supplierMappingId, ct);
            if (mapping is null)
            {
                return Results.NotFound();
            }

            byte[] content;
            using (var buffer = new MemoryStream())
            {
                await file.CopyToAsync(buffer, ct);
                content = buffer.ToArray();
            }

            var mappingSpec = new SupplierPriceMappingSpec(mapping.SheetName, mapping.HeaderRow, mapping.CodeColumn, mapping.PriceColumn);

            // Every guard runs BEFORE any cell is read (design.md
            // "Executable-file classification"): magic bytes, size, valid
            // OpenXML package, the mapping's named sheet, used-range row
            // count. A rejection here creates NO batch row.
            var guardResult = ImportGuards.ValidateFile(content, mappingSpec);
            if (guardResult is ImportGuardResult.Rejected rejected)
            {
                return Results.BadRequest(new { error = rejected.Reason });
            }

            IReadOnlyList<ParsedImportRow> parsedRows;
            try
            {
                parsedRows = SupplierPriceImportParser.Parse(content, mappingSpec);
            }
            catch (Exception)
            {
                return Results.BadRequest(new { error = "unparsable-file" });
            }

            var defaultPriceList = await priceListStore.FindDefaultPriceListAsync(scope, ct);

            var codeLookup = new Dictionary<string, (Guid PresentationId, decimal? CurrentPrice)>(StringComparer.Ordinal);
            foreach (var code in parsedRows.Select(r => r.RawCode).Where(c => !string.IsNullOrWhiteSpace(c)).Distinct(StringComparer.Ordinal))
            {
                var presentation = await catalogStore.FindByIdentificationCodeAsync(scope, code!, ct);
                if (presentation is null)
                {
                    continue;
                }

                decimal? currentPrice = null;
                if (defaultPriceList is not null)
                {
                    var effective = await priceListStore.GetEffectiveAsync(
                        scope, defaultPriceList.Id, presentation.Id, DateOnly.FromDateTime(DateTime.UtcNow), ct);
                    currentPrice = effective?.UnitPrice;
                }

                codeLookup[code!] = (presentation.Id, currentPrice);
            }

            var matched = ImportRowMatcher.Match(parsedRows, code => codeLookup.TryGetValue(code, out var value) ? value : null);

            var newRows = matched
                .Select(m => new NewImportBatchRow(m.RowNumber, m.RawCode, m.RawPrice, m.PresentationId, m.CurrentPrice, m.ProposedPrice, m.MatchStatus.ToString()))
                .ToList();

            var batch = await priceListStore.CreateImportBatchAsync(scope, supplierMappingId, file.FileName, newRows, "org-user", caller.Id, ct);

            return Results.Created($"/pricing/imports/{batch.Id}", batch);
        });

        group.MapGet("/imports/{batchId:guid}", async (
            Guid batchId,
            HttpContext httpContext,
            PostgresUserAccountStore userStore,
            PostgresPriceListStore priceListStore,
            CancellationToken ct) =>
        {
            var auth = await AuthorizeCallerAsync(httpContext, userStore, ct);
            if (auth is null)
            {
                return Results.Forbid();
            }

            var batch = await priceListStore.FindImportBatchAsync(auth.Value.Scope, batchId, ct);
            if (batch is null)
            {
                return Results.NotFound();
            }

            var rows = await priceListStore.ListImportRowsAsync(auth.Value.Scope, batchId, ct);
            return Results.Ok(new { batch, rows });
        });

        group.MapPost("/imports/{batchId:guid}/commit", async (
            Guid batchId,
            HttpContext httpContext,
            PostgresUserAccountStore userStore,
            PostgresPriceListStore priceListStore,
            CancellationToken ct) =>
        {
            var auth = await AuthorizeCallerAsync(httpContext, userStore, ct);
            if (auth is null)
            {
                return Results.Forbid();
            }
            var (scope, caller) = auth.Value;

            var batch = await priceListStore.FindImportBatchAsync(scope, batchId, ct);
            if (batch is null)
            {
                return Results.NotFound();
            }

            var defaultPriceList = await priceListStore.FindDefaultPriceListAsync(scope, ct);
            if (defaultPriceList is null)
            {
                return Results.Conflict(new { error = "no-default-price-list" });
            }

            try
            {
                var committed = await priceListStore.CommitImportBatchAsync(
                    scope, batchId, defaultPriceList.Id, DateOnly.FromDateTime(DateTime.UtcNow), "org-user", caller.Id, ct);
                return Results.Ok(committed);
            }
            catch (ImportBatchNotStagedException)
            {
                // design.md: "committing twice => 409" — the batch is no longer Staged.
                return Results.Conflict(new { error = "batch-already-resolved" });
            }
            catch (PostgresException ex) when (ex.SqlState is PostgresErrorCodes.UniqueViolation or PostgresErrorCodes.ForeignKeyViolation)
            {
                return Results.Conflict(new { error = "commit-failed" });
            }
        });

        group.MapPost("/imports/{batchId:guid}/reject", async (
            Guid batchId,
            HttpContext httpContext,
            PostgresUserAccountStore userStore,
            PostgresPriceListStore priceListStore,
            CancellationToken ct) =>
        {
            var auth = await AuthorizeCallerAsync(httpContext, userStore, ct);
            if (auth is null)
            {
                return Results.Forbid();
            }
            var (scope, caller) = auth.Value;

            var batch = await priceListStore.FindImportBatchAsync(scope, batchId, ct);
            if (batch is null)
            {
                return Results.NotFound();
            }

            try
            {
                var rejected = await priceListStore.RejectImportBatchAsync(scope, batchId, "org-user", caller.Id, ct);
                return Results.Ok(rejected);
            }
            catch (ImportBatchNotStagedException)
            {
                return Results.Conflict(new { error = "batch-already-resolved" });
            }
        });

        return group;
    }

    /// <summary>
    /// The <c>CustomerEndpoints.AuthorizeCallerAsync</c> shape, reused
    /// verbatim: caller id from the NameIdentifier claim, loaded through the
    /// store (never trusted from a claim alone), not revoked, and holding
    /// <see cref="Permission.ManageCatalog"/> — corrected from the
    /// `Permission.ManageUsers` gate design.md Work Unit 5 originally
    /// specified, since catalog/commercial pricing is a ManageCatalog
    /// concern (the web `RequireAdmin`/`CustomersScreen` guard still nests
    /// the pricing screen under ManageUsers for now, since no current role
    /// holds ManageCatalog without it). Returns
    /// <see langword="null"/> on ANY failure so every call site maps
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

        var caller = await userStore.LoadActorAsync(scope, callerId, ct);
        if (caller is null || caller.IsRevoked || !caller.EffectivePermissions.HasFlag(Permission.ManageCatalog))
        {
            return null;
        }

        return (scope, caller);
    }
}

public sealed record CreatePriceListRequest(string Name, bool IsDefault);

public sealed record AppendPriceEntryRequest(Guid PresentationId, decimal UnitPrice, DateOnly EffectiveFrom);

public sealed record CreateSupplierMappingRequest(string SupplierName, string SheetName, int HeaderRow, string CodeColumn, string PriceColumn);
