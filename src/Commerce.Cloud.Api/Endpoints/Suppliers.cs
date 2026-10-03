using Commerce.Cloud.Api.Persistence;
using Commerce.Cloud.Api.Tenancy;
using Commerce.Domain.Customers;
using Commerce.Domain.Suppliers;
using Npgsql;

namespace Commerce.Cloud.Api.Endpoints;

/// <summary>
/// Supplier registry admin endpoints (`/suppliers`), modelled on <see cref="CustomerEndpoints"/>: default cookie
/// auth, <see cref="TenantScopeEndpointFilter"/>, a store-loaded caller holding
/// <see cref="Commerce.Domain.Identity.Permission.ManageUsers"/> (the permission that guards the customer registry).
/// `organization_id` is never a request field and a cross-organization target is invisible under RLS (404).
/// There is no DELETE: a supplier is disabled (`isEnabled = false`) so its current account stays readable.
/// The supplier categories ("rubros", `/suppliers/categories`) are mapped by <see cref="MasterDataEndpoints"/>.
/// </summary>
public static class SupplierEndpoints
{
    private const string TaxIdTypeMessage = "taxIdType must be one of: None, Cuit, Cuil, Dni.";
    private const string TaxConditionMessage =
        "taxCondition must be one of: ConsumidorFinal, ResponsableInscripto, Monotributo, Exento, NoAplica.";

    public static RouteGroupBuilder MapSupplierEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/suppliers")
            .RequireAuthorization()
            .AddEndpointFilter<TenantScopeEndpointFilter>();

        group.MapGet("", async (
            string? search, Guid? categoryId, Guid? cityId, bool? enabled,
            HttpContext httpContext, PostgresUserAccountStore userStore, PostgresSupplierStore store, CancellationToken ct) =>
        {
            var auth = await CustomerEndpoints.AuthorizeCallerAsync(httpContext, userStore, ct);
            if (auth is null)
            {
                return Results.Forbid();
            }

            return Results.Ok(await store.ListAsync(
                auth.Value.Scope, new SupplierListFilter(search, cityId, categoryId, enabled), ct));
        });

        group.MapGet("/{id:guid}", async (
            Guid id, HttpContext httpContext, PostgresUserAccountStore userStore, PostgresSupplierStore store, CancellationToken ct) =>
        {
            var auth = await CustomerEndpoints.AuthorizeCallerAsync(httpContext, userStore, ct);
            if (auth is null)
            {
                return Results.Forbid();
            }

            var supplier = await store.FindAsync(auth.Value.Scope, id, ct);
            return supplier is null ? Results.NotFound() : Results.Ok(supplier);
        });

        group.MapPost("", async (
            CreateSupplierRequest request, HttpContext httpContext, PostgresUserAccountStore userStore,
            PostgresSupplierStore store, CancellationToken ct) =>
        {
            var auth = await CustomerEndpoints.AuthorizeCallerAsync(httpContext, userStore, ct);
            if (auth is null)
            {
                return Results.Forbid();
            }
            var (scope, caller) = auth.Value;

            if (!TryValidate(request, out var common, out var problem))
            {
                return problem!;
            }

            var supplierId = Guid.NewGuid();
            var newSupplier = new NewSupplier(
                supplierId, common!.DisplayName, request.LegalName, common.TaxIdType, common.TaxId, common.TaxCondition,
                request.Phone, common.Email, request.AddressStreet, request.AddressNumber, request.Neighborhood,
                request.PostalCode, CustomerEndpoints.NullIfEmpty(request.CityId), CustomerEndpoints.NullIfEmpty(request.CategoryId),
                request.PaymentTermsDays, common.BankCbu, common.BankAlias, request.Notes, caller.Id, common.Contacts);

            try
            {
                var created = await store.CreateAsync(scope, newSupplier, "org-user", caller.Id, ct);
                return Results.Created($"/suppliers/{created.Id}", new CreateSupplierResponse(created.Id));
            }
            catch (Exception ex) when (Translate(ex) is { } translated)
            {
                return translated;
            }
        });

        group.MapPut("/{id:guid}", async (
            Guid id, UpdateSupplierRequest request, HttpContext httpContext, PostgresUserAccountStore userStore,
            PostgresSupplierStore store, CancellationToken ct) =>
        {
            var auth = await CustomerEndpoints.AuthorizeCallerAsync(httpContext, userStore, ct);
            if (auth is null)
            {
                return Results.Forbid();
            }
            var (scope, caller) = auth.Value;

            if (!TryValidate(request, out var common, out var problem))
            {
                return problem!;
            }

            // An omitted cityId/categoryId/contacts/isEnabled keeps the stored value; Guid.Empty clears a reference.
            var update = new UpdateSupplier(
                common!.DisplayName, request.LegalName, common.TaxIdType, common.TaxId, common.TaxCondition,
                request.Phone, common.Email, request.AddressStreet, request.AddressNumber, request.Neighborhood,
                request.PostalCode, request.PaymentTermsDays, common.BankCbu, common.BankAlias, request.Notes,
                request.IsEnabled,
                request.CityId is { } city ? new ColumnChange<Guid?>(CustomerEndpoints.NullIfEmpty(city)) : null,
                request.CategoryId is { } category ? new ColumnChange<Guid?>(CustomerEndpoints.NullIfEmpty(category)) : null,
                common.Contacts, request.ExpectedUpdatedAtUtc);

            try
            {
                var updated = await store.UpdateAsync(scope, id, update, "org-user", caller.Id, ct);
                return updated is null ? Results.NotFound() : Results.Ok(updated);
            }
            catch (SupplierModifiedException)
            {
                return Results.Conflict(new { error = "supplier-modified" });
            }
            catch (Exception ex) when (Translate(ex) is { } translated)
            {
                return translated;
            }
        });

        return group;
    }

    private sealed record Validated(
        string DisplayName, TaxIdType TaxIdType, string? TaxId, TaxCondition TaxCondition,
        string? BankCbu, string? BankAlias, IReadOnlyList<SupplierContactInput>? Contacts, string? Email);

    private static bool TryValidate(ISupplierBody body, out Validated? validated, out IResult? problem)
    {
        validated = null;
        problem = null;

        if (string.IsNullOrWhiteSpace(body.DisplayName))
        {
            problem = Problem("displayName", "displayName is required.");
            return false;
        }

        var taxIdType = TaxIdType.None;
        if (!string.IsNullOrWhiteSpace(body.TaxIdType) && !Enum.TryParse(body.TaxIdType, out taxIdType))
        {
            problem = Problem("taxIdType", TaxIdTypeMessage);
            return false;
        }

        if (!TaxIdRules.TryNormalize(taxIdType, body.TaxId, out var taxId, out var taxIdError))
        {
            problem = Problem("taxId", taxIdError!);
            return false;
        }

        var taxCondition = TaxCondition.NoAplica;
        if (!string.IsNullOrWhiteSpace(body.TaxCondition) && !Enum.TryParse(body.TaxCondition, out taxCondition))
        {
            problem = Problem("taxCondition", TaxConditionMessage);
            return false;
        }

        if (!SupplierRules.TryValidatePaymentTermsDays(body.PaymentTermsDays, out var termsError))
        {
            problem = Problem("paymentTermsDays", termsError!);
            return false;
        }

        if (!SupplierRules.TryNormalizeBankCbu(body.BankCbu, out var cbu, out var cbuError))
        {
            problem = Problem("bankCbu", cbuError!);
            return false;
        }

        if (!SupplierRules.TryNormalizeBankAlias(body.BankAlias, out var alias, out var aliasError))
        {
            problem = Problem("bankAlias", aliasError!);
            return false;
        }

        if (!CustomerEndpoints.TryNormalizeEmail(body.Email, out var email, out var emailProblem))
        {
            problem = emailProblem;
            return false;
        }

        if (!CustomerEndpoints.TryBuildContacts(body.Contacts, out var customerContacts, out var contactsProblem))
        {
            problem = contactsProblem;
            return false;
        }

        var contacts = customerContacts?
            .Select(c => new SupplierContactInput(c.Id, c.FirstName, c.LastName, c.Phone, c.Email, c.Role, c.IsPrimary, c.SortOrder))
            .ToList();

        validated = new Validated(body.DisplayName.Trim(), taxIdType, taxId, taxCondition, cbu, alias, contacts, email);
        return true;
    }

    private static IResult Problem(string field, string message) =>
        Results.ValidationProblem(new Dictionary<string, string[]> { [field] = [message] });

    /// <summary>Maps the store's refusals (bad contact id, unknown city, category of another organization) to a 400.</summary>
    private static IResult? Translate(Exception ex) => ex switch
    {
        SupplierContactRejectedException contact => CustomerEndpoints.ContactsProblem(contact.Message),
        PostgresException { SqlState: PostgresErrorCodes.CheckViolation } =>
            Problem("taxId", "taxId is required exactly when taxIdType is not None."),
        PostgresException { SqlState: PostgresErrorCodes.ForeignKeyViolation, ConstraintName: "suppliers_city_fk" } =>
            Problem("cityId", "cityId does not match a city."),
        PostgresException { SqlState: PostgresErrorCodes.ForeignKeyViolation, ConstraintName: "suppliers_category_org_fk" } =>
            Problem("categoryId", "categoryId does not match a supplier category of this organization."),
        _ => null,
    };
}

/// <summary>The body members both create and update validate.</summary>
internal interface ISupplierBody
{
    string DisplayName { get; }
    string? TaxIdType { get; }
    string? TaxId { get; }
    string? TaxCondition { get; }
    string? Email { get; }
    int? PaymentTermsDays { get; }
    string? BankCbu { get; }
    string? BankAlias { get; }
    ContactRequest[]? Contacts { get; }
}

/// <summary>
/// Create body. `TaxIdType` defaults to None and `TaxCondition` to NoAplica when omitted. `BankCbu` accepts
/// separators (stored as 22 digits). `Email` follows the shared email rule (stored trimmed). `Contacts`: same shape
/// and rules as customers.
/// </summary>
public sealed record CreateSupplierRequest(
    string DisplayName, string? LegalName = null,
    string? TaxIdType = null, string? TaxId = null, string? TaxCondition = null,
    string? Phone = null, string? Email = null,
    string? AddressStreet = null, string? AddressNumber = null, string? Neighborhood = null, string? PostalCode = null,
    Guid? CityId = null, Guid? CategoryId = null,
    int? PaymentTermsDays = null, string? BankCbu = null, string? BankAlias = null, string? Notes = null,
    ContactRequest[]? Contacts = null) : ISupplierBody;

/// <summary>
/// Update body: plain fields REPLACE the stored value (omitted/null clears it); `IsEnabled`, `CityId`, `CategoryId`
/// (Guid.Empty clears) and `Contacts` (whole set replaced when present) keep the stored value when omitted.
/// `ExpectedUpdatedAtUtc` is the optimistic-concurrency token: a mismatch answers 409 `supplier-modified`.
/// </summary>
public sealed record UpdateSupplierRequest(
    string DisplayName, string? LegalName = null,
    string? TaxIdType = null, string? TaxId = null, string? TaxCondition = null,
    string? Phone = null, string? Email = null,
    string? AddressStreet = null, string? AddressNumber = null, string? Neighborhood = null, string? PostalCode = null,
    Guid? CityId = null, Guid? CategoryId = null,
    int? PaymentTermsDays = null, string? BankCbu = null, string? BankAlias = null, string? Notes = null,
    bool? IsEnabled = null, ContactRequest[]? Contacts = null,
    DateTimeOffset? ExpectedUpdatedAtUtc = null) : ISupplierBody;

public sealed record CreateSupplierResponse(Guid SupplierId);
