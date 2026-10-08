using System.Security.Claims;
using Commerce.Cloud.Api.Persistence;
using Commerce.Cloud.Api.Tenancy;
using Commerce.Domain.Customers;
using Commerce.Domain.Identity;
using Commerce.Domain.Validation;
using Microsoft.AspNetCore.Mvc;
using Npgsql;

using Commerce.Domain.CurrentAccounts;

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
///
/// List, read one, create and update also admit a paired terminal with a verified operator
/// (<see cref="DeviceOperatorAccess.AllowDeviceOperator"/>, admin-console-field-fixes T5); the operator is
/// then the caller every check below sees. Reading one lets the terminal's enable/disable row action re-read the
/// customer right before writing it, so it never sends a stale copy of the list row.
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
        }).AllowDeviceOperator();

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
        }).AllowDeviceOperator();

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

            if (!TryParsePartyType(request.PartyType, out var partyType, out var partyTypeProblem))
            {
                return partyTypeProblem!;
            }

            if (!TryNormalizeEmail(request.Email, out var email, out var emailProblem))
            {
                return emailProblem!;
            }

            if (!TryBuildContacts(request.Contacts, out var contacts, out var contactsProblem))
            {
                return contactsProblem!;
            }

            if (InvalidPaymentTermsDays(request.PaymentTermsDays) is { } termsProblem)
            {
                return termsProblem;
            }

            // The name is always `displayName` (a person's full name or a company's legal name, per `partyType`);
            // legal name, locality and province are no longer written.
            var customerId = Guid.NewGuid();
            var newCustomer = new NewCustomer(
                customerId, customerKind, request.DisplayName.Trim(), LegalName: null, taxIdType, normalizedTaxId,
                taxCondition, request.Phone, email, request.AddressStreet, request.AddressNumber,
                request.Neighborhood, Locality: null, Province: null, request.PostalCode, request.DeliveryNotes,
                request.DiscountPercentage, request.PaymentTerms, request.Notes, caller.Id,
                NullIfEmpty(request.CityId), NullIfEmpty(request.BusinessTypeId), contacts, NullIfEmpty(request.PriceListId),
                partyType ?? PartyTypeRules.DefaultFor(taxIdType), PaymentTermsDays: request.PaymentTermsDays == ClearPaymentTermsDays ? null : request.PaymentTermsDays);

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
            catch (CustomerContactRejectedException ex)
            {
                return ContactsProblem(ex.Message);
            }
            catch (PostgresException ex) when (MasterDataReferenceProblem(ex) is { } problem)
            {
                return problem;
            }

            return Results.Created($"/customers/{created.Id}", new CreateCustomerResponse(created.Id));
        }).AllowDeviceOperator();

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

            if (!TryParsePartyType(request.PartyType, out var partyType, out var partyTypeProblem))
            {
                return partyTypeProblem!;
            }

            if (!TryNormalizeEmail(request.Email, out var email, out var emailProblem))
            {
                return emailProblem!;
            }

            if (!TryBuildContacts(request.Contacts, out var contacts, out var contactsProblem))
            {
                return contactsProblem!;
            }

            if (InvalidPaymentTermsDays(request.PaymentTermsDays) is { } termsProblem)
            {
                return termsProblem;
            }

            // Master data on update: an omitted property keeps the stored value
            // (so the POS, which predates these fields, never wipes them);
            // Guid.Empty / "" clears it. An omitted partyType keeps the stored one.
            var update = new UpdateCustomer(
                request.DisplayName.Trim(), taxIdType, normalizedTaxId, taxCondition,
                request.Phone, email, request.AddressStreet, request.AddressNumber, request.Neighborhood,
                request.PostalCode, request.DeliveryNotes,
                request.DiscountPercentage, request.PaymentTerms, request.Notes, request.IsEnabled,
                request.CityId is { } city ? new ColumnChange<Guid?>(NullIfEmpty(city)) : null,
                request.BusinessTypeId is { } type ? new ColumnChange<Guid?>(NullIfEmpty(type)) : null,
                contacts,
                request.ExpectedUpdatedAtUtc,
                request.PriceListId is { } priceList ? new ColumnChange<Guid?>(NullIfEmpty(priceList)) : null,
                partyType,
                // Omitted keeps the stored terms; -1 clears them (the customer uses the organization's default).
                request.PaymentTermsDays is { } days ? new ColumnChange<int?>(days == ClearPaymentTermsDays ? null : days) : null);

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
            catch (CustomerContactRejectedException ex)
            {
                return ContactsProblem(ex.Message);
            }
            catch (PostgresException ex) when (MasterDataReferenceProblem(ex) is { } problem)
            {
                return problem;
            }

            return updated is null ? Results.NotFound() : Results.Ok(updated);
        }).AllowDeviceOperator();

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

    internal static Guid? NullIfEmpty(Guid? id) => id is { } value && value != Guid.Empty ? value : null;

    /// <summary>
    /// An optional email field under the shared rule (<see cref="EmailAddressRules"/>): blank is no email, otherwise
    /// the trimmed address; an invalid one is a 400 on <paramref name="field"/>.
    /// </summary>
    internal static bool TryNormalizeEmail(string? raw, out string? email, out IResult? problem, string field = "email")
    {
        problem = null;
        if (EmailAddressRules.TryNormalize(raw, out email))
        {
            return true;
        }

        problem = Results.ValidationProblem(new Dictionary<string, string[]> { [field] = [$"{field} must be a valid email address."] });
        return false;
    }

    /// <summary>An omitted party type is null (create: default for the tax id type; update: keep the stored one).</summary>
    private static bool TryParsePartyType(string? raw, out PartyType? partyType, out IResult? problem)
    {
        partyType = null;
        problem = null;
        if (raw is null)
        {
            return true;
        }

        if (!PartyTypeRules.TryParse(raw, out var parsed))
        {
            problem = Results.ValidationProblem(new Dictionary<string, string[]> { ["partyType"] = [PartyTypeRules.Message] });
            return false;
        }

        partyType = parsed;
        return true;
    }

    internal static string? BlankToNull(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    internal const int MaxContacts = 50;
    private const int MaxContactFieldLength = 200;

    internal static IResult ContactsProblem(string message) =>
        Results.ValidationProblem(new Dictionary<string, string[]> { ["contacts"] = [message] });

    /// <summary>
    /// Validates and normalizes the request's contact list. A null array (property omitted) yields a null
    /// list, which on update means "keep the stored contacts"; an empty array yields an empty list (clear).
    /// Rules: at most 50 contacts, a first name on each, an email (when present) under the shared email rule, at
    /// most one primary, no repeated id. A contact without `sortOrder` takes its position in the array.
    /// </summary>
    internal static bool TryBuildContacts(
        ContactRequest[]? requested, out IReadOnlyList<CustomerContactInput>? contacts, out IResult? problem)
    {
        contacts = null;
        problem = null;
        if (requested is null)
        {
            return true;
        }

        if (requested.Length > MaxContacts)
        {
            problem = ContactsProblem($"at most {MaxContacts} contacts are allowed.");
            return false;
        }

        var result = new List<CustomerContactInput>(requested.Length);
        var ids = new HashSet<Guid>();
        for (var i = 0; i < requested.Length; i++)
        {
            var contact = requested[i];
            var firstName = BlankToNull(contact?.FirstName);
            if (firstName is null)
            {
                problem = ContactsProblem($"contacts[{i}].firstName is required.");
                return false;
            }

            if (new[] { firstName, contact!.LastName, contact.Phone, contact.Email, contact.Role }
                .Any(v => v is { Length: > MaxContactFieldLength }))
            {
                problem = ContactsProblem($"contacts[{i}] fields must be {MaxContactFieldLength} characters or fewer.");
                return false;
            }

            if (!EmailAddressRules.TryNormalize(contact.Email, out var contactEmail))
            {
                problem = ContactsProblem($"contacts[{i}].email must be a valid email address.");
                return false;
            }

            var id = NullIfEmpty(contact.Id);
            if (id is { } contactId && !ids.Add(contactId))
            {
                problem = ContactsProblem($"contacts[{i}].id is repeated.");
                return false;
            }

            result.Add(new CustomerContactInput(
                id, firstName, BlankToNull(contact.LastName), BlankToNull(contact.Phone), contactEmail,
                BlankToNull(contact.Role), contact.IsPrimary ?? false, contact.SortOrder ?? i));
        }

        if (result.Count(c => c.IsPrimary) > 1)
        {
            problem = ContactsProblem("at most one contact can be primary.");
            return false;
        }

        contacts = result;
        return true;
    }

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
                "customers_price_list_org_fk" => Results.ValidationProblem(new Dictionary<string, string[]>
                {
                    ["priceListId"] = ["priceListId does not match a price list of this organization."],
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
    /// <summary>On an update, this value of <c>paymentTermsDays</c> clears the customer's own terms.</summary>
    internal const int ClearPaymentTermsDays = -1;

    /// <summary>
    /// The customer's payment terms in days: 0 to 365 (0 = due the same day); on an update also -1 (clear: the
    /// organization's default applies). Null (omitted) is always fine.
    /// </summary>
    private static IResult? InvalidPaymentTermsDays(int? days) =>
        days is null || days == ClearPaymentTermsDays || PaymentTerms.IsValidDays(days.Value)
            ? null
            : Results.ValidationProblem(new Dictionary<string, string[]>
            {
                ["paymentTermsDays"] = [$"paymentTermsDays is between {PaymentTerms.MinDays} and {PaymentTerms.MaxDays} days (or -1 to use the organization's default)."],
            });

    internal static async Task<(CloudTenantScope Scope, UserAccount Caller)?> AuthorizeCallerAsync(
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

/// <summary>
/// Create body. `DisplayName` is the one customer name: a person's full name or a company's legal name, per
/// `PartyType` ("Person" | "Company"; omitted = Company for a CUIT, otherwise Person). `Email` follows the shared email
/// rule. The old `legalName`, `locality` and `province` properties are no longer read (sending them is harmless).
/// </summary>
public sealed record CreateCustomerRequest(
    string CustomerKind, string DisplayName,
    string TaxIdType, string? TaxId, string TaxCondition,
    string? Phone, string? Email,
    string? AddressStreet, string? AddressNumber, string? Neighborhood, string? PostalCode,
    string? DeliveryNotes, decimal? DiscountPercentage, string? PaymentTerms, string? Notes,
    Guid? CityId = null, Guid? BusinessTypeId = null, ContactRequest[]? Contacts = null, Guid? PriceListId = null,
    string? PartyType = null, int? PaymentTermsDays = null);

/// <summary>
/// <see cref="CreateCustomerRequest"/> minus <c>CustomerKind</c> (read-only at
/// edit — design.md "Web form shape (create vs. edit)"), plus
/// <c>IsEnabled</c>. An omitted <c>PartyType</c> keeps the stored one.
/// </summary>
public sealed record UpdateCustomerRequest(
    string DisplayName,
    string TaxIdType, string? TaxId, string TaxCondition,
    string? Phone, string? Email,
    string? AddressStreet, string? AddressNumber, string? Neighborhood, string? PostalCode,
    string? DeliveryNotes, decimal? DiscountPercentage, string? PaymentTerms, string? Notes,
    bool IsEnabled,
    Guid? CityId = null, Guid? BusinessTypeId = null, ContactRequest[]? Contacts = null,
    DateTimeOffset? ExpectedUpdatedAtUtc = null, Guid? PriceListId = null, string? PartyType = null,
    int? PaymentTermsDays = null);

/// <summary>
/// One contact person in a customer create/update body. `Id` is optional: a sent id is kept (it updates the
/// customer's contact with that id, or creates one with it); `FirstName` is required; at most one contact
/// can be `IsPrimary`; `SortOrder` defaults to the position in the array. On update the whole array
/// REPLACES the customer's contacts (omitted ones are removed); omit the property to keep them.
/// </summary>
public sealed record ContactRequest(
    Guid? Id = null, string? FirstName = null, string? LastName = null, string? Phone = null, string? Email = null,
    string? Role = null, bool? IsPrimary = null, int? SortOrder = null);

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
