using System.Security.Claims;
using Commerce.Cloud.Api.Persistence;
using Commerce.Cloud.Api.Tenancy;
using Commerce.Domain.Customers;
using Commerce.Domain.Identity;
using Microsoft.AspNetCore.Mvc;
using Npgsql;

namespace Commerce.Cloud.Api.Endpoints;

/// <summary>
/// Customer registry admin endpoints (commerce-customer-identity design.md
/// "Two admin UIs against one endpoint set"). Reuses <c>Account.cs</c>'s
/// <c>adminGroup</c> authorization shape verbatim: <c>RequireAuthorization</c>
/// (default cookie) + <see cref="TenantScopeEndpointFilter"/> + a store-loaded
/// caller + <see cref="Permission.ManageUsers"/>. `organization_id` is never a
/// request field — it always comes from the tenant scope, so cross-org
/// creation is unrepresentable and a cross-org target is invisible under RLS
/// (404, identical to a nonexistent id).
/// </summary>
public static class CustomerEndpoints
{
    public static RouteGroupBuilder MapCustomerEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/customers")
            .RequireAuthorization()
            .AddEndpointFilter<TenantScopeEndpointFilter>();

        group.MapGet("", async (
            string? search,
            Guid? cityId,
            Guid? businessTypeId,
            HttpContext httpContext,
            PostgresUserAccountStore userStore,
            PostgresCustomerStore customerStore,
            CancellationToken ct) =>
        {
            var auth = await AuthorizeCallerAsync(httpContext, userStore, ct);
            if (auth is null)
            {
                return Results.Forbid();
            }

            var customers = await customerStore.ListAsync(
                auth.Value.Scope, new CustomerListFilter(search, cityId, businessTypeId), ct);
            return Results.Ok(customers);
        });

        group.MapGet("/{id:guid}", async (
            Guid id,
            HttpContext httpContext,
            PostgresUserAccountStore userStore,
            PostgresCustomerStore customerStore,
            CancellationToken ct) =>
        {
            var auth = await AuthorizeCallerAsync(httpContext, userStore, ct);
            if (auth is null)
            {
                return Results.Forbid();
            }

            var customer = await customerStore.FindAsync(auth.Value.Scope, id, ct);
            return customer is null ? Results.NotFound() : Results.Ok(customer);
        });

        group.MapPost("", async (
            CreateCustomerRequest request,
            HttpContext httpContext,
            PostgresUserAccountStore userStore,
            PostgresCustomerStore customerStore,
            CancellationToken ct) =>
        {
            var auth = await AuthorizeCallerAsync(httpContext, userStore, ct);
            if (auth is null)
            {
                return Results.Forbid();
            }
            var (scope, caller) = auth.Value;

            if (string.IsNullOrWhiteSpace(request.DisplayName))
            {
                return Results.ValidationProblem(new Dictionary<string, string[]>
                {
                    ["displayName"] = ["displayName is required."],
                });
            }

            if (!Enum.TryParse<CustomerKind>(request.CustomerKind, out var customerKind))
            {
                return Results.ValidationProblem(new Dictionary<string, string[]>
                {
                    ["customerKind"] = ["customerKind must be one of: Retail, Wholesale."],
                });
            }

            if (!Enum.TryParse<TaxIdType>(request.TaxIdType, out var taxIdType))
            {
                return Results.ValidationProblem(new Dictionary<string, string[]>
                {
                    ["taxIdType"] = [TaxIdTypeMessage],
                });
            }

            if (!TaxIdRules.TryNormalize(taxIdType, request.TaxId, out var normalizedTaxId, out var taxIdError))
            {
                return Results.ValidationProblem(new Dictionary<string, string[]> { ["taxId"] = [taxIdError!] });
            }

            if (!Enum.TryParse<TaxCondition>(request.TaxCondition, out var taxCondition))
            {
                return Results.ValidationProblem(new Dictionary<string, string[]>
                {
                    ["taxCondition"] = ["taxCondition must be one of: ConsumidorFinal, ResponsableInscripto, Monotributo, Exento, NoAplica."],
                });
            }

            var customerId = Guid.NewGuid();
            var newCustomer = new NewCustomer(
                customerId, customerKind, request.DisplayName.Trim(), request.LegalName, taxIdType, normalizedTaxId,
                taxCondition, request.Phone, request.Email, request.AddressStreet, request.AddressNumber,
                request.Neighborhood, request.Locality, request.Province, request.PostalCode, request.DeliveryNotes,
                request.DiscountPercentage, request.PaymentTerms, request.Notes, caller.Id,
                NullIfEmpty(request.CityId), NullIfEmpty(request.BusinessTypeId), BlankToNull(request.ContactName));

            CustomerRecord created;
            try
            {
                created = await customerStore.CreateAsync(scope, newCustomer, "org-user", caller.Id, ct);
            }
            catch (PostgresException ex) when (ex.SqlState == PostgresErrorCodes.CheckViolation)
            {
                // `customers_tax_id_requires_type` (0008): the INSERT and its
                // audit row are in ONE transaction, so this failure rolls
                // both back atomically — no audit row is ever written for
                // this attempted customerId.
                return Results.ValidationProblem(new Dictionary<string, string[]>
                {
                    ["taxId"] = ["taxId is required exactly when taxIdType is not None."],
                });
            }
            catch (PostgresException ex) when (MasterDataReferenceProblem(ex) is { } problem)
            {
                return problem;
            }

            return Results.Created($"/customers/{created.Id}", new CreateCustomerResponse(created.Id));
        });

        group.MapPut("/{id:guid}", async (
            Guid id,
            UpdateCustomerRequest request,
            HttpContext httpContext,
            PostgresUserAccountStore userStore,
            PostgresCustomerStore customerStore,
            CancellationToken ct) =>
        {
            var auth = await AuthorizeCallerAsync(httpContext, userStore, ct);
            if (auth is null)
            {
                return Results.Forbid();
            }
            var (scope, caller) = auth.Value;

            if (string.IsNullOrWhiteSpace(request.DisplayName))
            {
                return Results.ValidationProblem(new Dictionary<string, string[]>
                {
                    ["displayName"] = ["displayName is required."],
                });
            }

            if (!Enum.TryParse<TaxIdType>(request.TaxIdType, out var taxIdType))
            {
                return Results.ValidationProblem(new Dictionary<string, string[]>
                {
                    ["taxIdType"] = [TaxIdTypeMessage],
                });
            }

            if (!TaxIdRules.TryNormalize(taxIdType, request.TaxId, out var normalizedTaxId, out var taxIdError))
            {
                return Results.ValidationProblem(new Dictionary<string, string[]> { ["taxId"] = [taxIdError!] });
            }

            if (!Enum.TryParse<TaxCondition>(request.TaxCondition, out var taxCondition))
            {
                return Results.ValidationProblem(new Dictionary<string, string[]>
                {
                    ["taxCondition"] = ["taxCondition must be one of: ConsumidorFinal, ResponsableInscripto, Monotributo, Exento, NoAplica."],
                });
            }

            // Master data on update: an omitted property keeps the stored value
            // (so the POS, which predates these fields, never wipes them);
            // Guid.Empty / "" clears it.
            var update = new UpdateCustomer(
                request.DisplayName.Trim(), request.LegalName, taxIdType, normalizedTaxId, taxCondition,
                request.Phone, request.Email, request.AddressStreet, request.AddressNumber, request.Neighborhood,
                request.Locality, request.Province, request.PostalCode, request.DeliveryNotes,
                request.DiscountPercentage, request.PaymentTerms, request.Notes, request.IsEnabled,
                request.CityId is { } city ? new ColumnChange<Guid?>(NullIfEmpty(city)) : null,
                request.BusinessTypeId is { } type ? new ColumnChange<Guid?>(NullIfEmpty(type)) : null,
                request.ContactName is { } contact ? new ColumnChange<string?>(BlankToNull(contact)) : null,
                request.ExpectedUpdatedAtUtc);

            CustomerRecord? updated;
            try
            {
                // Scoped to the CALLER's org: customers_tenant_isolation RLS
                // makes a cross-org target invisible, so UpdateAsync returns
                // null identically to "no such customer" (same pattern as
                // Account.cs's /reset-password and /roles admin routes).
                updated = await customerStore.UpdateAsync(scope, id, update, "org-user", caller.Id, ct);
            }
            catch (PostgresException ex) when (ex.SqlState == PostgresErrorCodes.CheckViolation)
            {
                return Results.ValidationProblem(new Dictionary<string, string[]>
                {
                    ["taxId"] = ["taxId is required exactly when taxIdType is not None."],
                });
            }
            catch (CustomerModifiedException)
            {
                return Results.Conflict(new { error = "customer-modified" });
            }
            catch (PostgresException ex) when (MasterDataReferenceProblem(ex) is { } problem)
            {
                return problem;
            }

            return updated is null ? Results.NotFound() : Results.Ok(updated);
        });

        group.MapPost("/{id:guid}/ordering-access", async (
            Guid id,
            HttpContext httpContext,
            PostgresUserAccountStore userStore,
            PostgresCustomerStore customerStore,
            PostgresCustomerOrderingAccessStore accessStore,
            CancellationToken ct) =>
        {
            var auth = await AuthorizeCallerAsync(httpContext, userStore, ct);
            if (auth is null)
            {
                return Results.Forbid();
            }
            var (scope, caller) = auth.Value;

            // Cross-org (or nonexistent) target is invisible under RLS —
            // identical 404 to the PUT path above.
            var customer = await customerStore.FindAsync(scope, id, ct);
            if (customer is null)
            {
                return Results.NotFound();
            }

            var credential = await accessStore.IssueAsync(scope, id, caller.Id, ct);

            // The plaintext credential is returned exactly once (the
            // `/device/pair` precedent) — it is never stored, only its hash.
            return Results.Ok(new IssueOrderingAccessResponse(credential));
        });

        group.MapDelete("/{id:guid}/ordering-access", async (
            Guid id,
            [FromBody] RevokeOrderingAccessRequest request,
            HttpContext httpContext,
            PostgresUserAccountStore userStore,
            PostgresCustomerStore customerStore,
            PostgresCustomerOrderingAccessStore accessStore,
            CancellationToken ct) =>
        {
            var auth = await AuthorizeCallerAsync(httpContext, userStore, ct);
            if (auth is null)
            {
                return Results.Forbid();
            }
            var scope = auth.Value.Scope;

            var customer = await customerStore.FindAsync(scope, id, ct);
            if (customer is null)
            {
                return Results.NotFound();
            }

            var revoked = await accessStore.RevokeAsync(request.Credential, ct);
            return revoked ? Results.NoContent() : Results.NotFound();
        });

        return group;
    }

    private const string TaxIdTypeMessage = "taxIdType must be one of: None, Cuit, Cuil, Dni.";

    private static Guid? NullIfEmpty(Guid? id) => id is { } value && value != Guid.Empty ? value : null;

    private static string? BlankToNull(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    /// <summary>
    /// The foreign keys refuse a city that does not exist (global, 0028) or a
    /// business type that is not in the caller's organization (another
    /// organization's id is indistinguishable from an unknown one): a 400 on the
    /// offending field.
    /// </summary>
    private static IResult? MasterDataReferenceProblem(PostgresException ex) =>
        ex.SqlState == PostgresErrorCodes.ForeignKeyViolation
            ? ex.ConstraintName switch
            {
                "customers_city_fk" => Results.ValidationProblem(new Dictionary<string, string[]>
                {
                    ["cityId"] = ["cityId does not match a city."],
                }),
                "customers_business_type_org_fk" => Results.ValidationProblem(new Dictionary<string, string[]>
                {
                    ["businessTypeId"] = ["businessTypeId does not match a business type of this organization."],
                }),
                _ => null,
            }
            : null;

    /// <summary>
    /// Account.cs's adminGroup check, factored once for this file's six
    /// routes rather than repeated verbatim six times: caller id from the
    /// NameIdentifier claim, loaded through the store (never trusted from a
    /// claim alone), not revoked, and holding <see cref="Permission.ManageUsers"/>.
    /// Returns <see langword="null"/> on ANY failure so every call site maps
    /// to the same <c>Results.Forbid()</c>.
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
        if (caller is null || caller.IsRevoked || !ActingPermissions.For(caller, scope).HasFlag(Permission.ManageUsers))
        {
            return null;
        }

        return (scope, caller);
    }
}

public sealed record CreateCustomerRequest(
    string CustomerKind, string DisplayName, string? LegalName,
    string TaxIdType, string? TaxId, string TaxCondition,
    string? Phone, string? Email,
    string? AddressStreet, string? AddressNumber, string? Neighborhood,
    string? Locality, string? Province, string? PostalCode,
    string? DeliveryNotes, decimal? DiscountPercentage, string? PaymentTerms, string? Notes,
    Guid? CityId = null, Guid? BusinessTypeId = null, string? ContactName = null);

/// <summary>
/// <see cref="CreateCustomerRequest"/> minus <c>CustomerKind</c> (read-only at
/// edit — design.md "Web form shape (create vs. edit)"), plus
/// <c>IsEnabled</c>.
/// </summary>
public sealed record UpdateCustomerRequest(
    string DisplayName, string? LegalName,
    string TaxIdType, string? TaxId, string TaxCondition,
    string? Phone, string? Email,
    string? AddressStreet, string? AddressNumber, string? Neighborhood,
    string? Locality, string? Province, string? PostalCode,
    string? DeliveryNotes, decimal? DiscountPercentage, string? PaymentTerms, string? Notes,
    bool IsEnabled,
    Guid? CityId = null, Guid? BusinessTypeId = null, string? ContactName = null,
    DateTimeOffset? ExpectedUpdatedAtUtc = null);

public sealed record CreateCustomerResponse(Guid CustomerId);

/// <summary>Shown exactly once — the server never stores the plaintext.</summary>
public sealed record IssueOrderingAccessResponse(Guid Credential);

/// <summary>
/// The credential to revoke. Revocation is by credential hash (design.md
/// "customer_ordering_access credential storage") — the server cannot
/// reverse a hash into "the credential currently issued to this customer",
/// so the caller supplies the plaintext value it was shown at issuance.
/// </summary>
public sealed record RevokeOrderingAccessRequest(Guid Credential);
