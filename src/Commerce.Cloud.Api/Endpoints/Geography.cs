using System.Security.Claims;
using Commerce.Cloud.Api.Persistence;
using Commerce.Cloud.Api.Tenancy;
using Commerce.Domain.Identity;
using Npgsql;

namespace Commerce.Cloud.Api.Endpoints;

/// <summary>
/// Core geography (migration 0028): global countries, provinces and cities,
/// the same for every organization. Reading needs only a signed-in staff user
/// (the customer form's city picker and any filter use it). Writing a city
/// (add one Georef does not list, rename, deactivate) is reserved to the
/// platform system administrator, using the codebase's established gate:
/// the caller is loaded from the store (never trusted from a claim) and must be
/// a non-revoked <c>IsSystemAdmin</c>. Cities are never deleted: deactivating
/// one hides it from searches while customers keep their reference.
/// </summary>
public static class GeographyEndpoints
{
    internal const int DefaultLimit = 20;
    internal const int MaxLimit = 200;
    private const int MaxNameLength = 200;

    public static IEndpointRouteBuilder MapGeographyEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/geo")
            .RequireAuthorization()
            .AddEndpointFilter<TenantScopeEndpointFilter>();

        group.MapGet("/provinces", async (PostgresGeoStore store, CancellationToken ct) =>
            Results.Ok(await store.ListProvincesAsync(ct)));

        group.MapGet("/cities", async (
            string? search,
            string? provinceId,
            bool? includeInactive,
            int? limit,
            int? offset,
            PostgresGeoStore store,
            CancellationToken ct) =>
        {
            var folded = CustomerSearchTerm.FoldForLike(search);
            var take = limit is > 0 ? Math.Min(limit.Value, MaxLimit) : DefaultLimit;
            var skip = offset is > 0 ? offset.Value : 0;
            var province = string.IsNullOrWhiteSpace(provinceId) ? null : provinceId.Trim();
            return Results.Ok(await store.SearchCitiesAsync(new CitySearch(folded, province, includeInactive ?? false, take, skip), ct));
        });

        group.MapGet("/cities/{id:guid}", async (Guid id, PostgresGeoStore store, CancellationToken ct) =>
        {
            var city = await store.FindCityAsync(id, ct);
            return city is null ? Results.NotFound() : Results.Ok(city);
        });

        group.MapPost("/cities", async (
            CityRequest request,
            HttpContext httpContext,
            PostgresUserAccountStore userStore,
            PostgresGeoStore store,
            CancellationToken ct) =>
        {
            var caller = await AuthorizeSystemAdminAsync(httpContext, userStore, ct);
            if (caller is null)
            {
                return Results.StatusCode(StatusCodes.Status403Forbidden);
            }

            var name = request.Name?.Trim() ?? string.Empty;
            var provinceId = request.ProvinceId?.Trim() ?? string.Empty;
            if (Validate(name, provinceId, requireProvince: true) is { } invalid)
            {
                return invalid;
            }

            try
            {
                var created = await store.CreateCityAsync(
                    TenantScopeEndpointFilter.GetScope(httpContext),
                    new NewCity(Guid.NewGuid(), name, provinceId, BlankToNull(request.DepartmentName), request.IsActive ?? true),
                    caller.Id, ct);
                return Results.Created($"/geo/cities/{created.Id}", created);
            }
            catch (PostgresException ex) when (CityProblem(ex) is { } problem)
            {
                return problem;
            }
        });

        group.MapPut("/cities/{id:guid}", async (
            Guid id,
            CityRequest request,
            HttpContext httpContext,
            PostgresUserAccountStore userStore,
            PostgresGeoStore store,
            CancellationToken ct) =>
        {
            var caller = await AuthorizeSystemAdminAsync(httpContext, userStore, ct);
            if (caller is null)
            {
                return Results.StatusCode(StatusCodes.Status403Forbidden);
            }

            var name = request.Name?.Trim() ?? string.Empty;
            var provinceId = string.IsNullOrWhiteSpace(request.ProvinceId) ? null : request.ProvinceId.Trim();
            if (Validate(name, provinceId ?? "00", requireProvince: false) is { } invalid)
            {
                return invalid;
            }

            try
            {
                // An omitted department / province / isActive keeps the stored value;
                // a blank department name clears it.
                var updated = await store.UpdateCityAsync(
                    TenantScopeEndpointFilter.GetScope(httpContext), id,
                    new UpdateCity(name, provinceId, request.DepartmentName, request.IsActive), caller.Id, ct);
                return updated is null ? Results.NotFound() : Results.Ok(updated);
            }
            catch (PostgresException ex) when (CityProblem(ex) is { } problem)
            {
                return problem;
            }
        });

        return app;
    }

    private static string? BlankToNull(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private static IResult? Validate(string name, string provinceId, bool requireProvince)
    {
        var errors = new Dictionary<string, string[]>();
        if (name.Length == 0)
        {
            errors["name"] = ["name is required."];
        }
        else if (name.Length > MaxNameLength)
        {
            errors["name"] = [$"name must be {MaxNameLength} characters or fewer."];
        }

        if (requireProvince && provinceId.Length == 0)
        {
            errors["provinceId"] = ["provinceId is required."];
        }

        return errors.Count == 0 ? null : Results.ValidationProblem(errors);
    }

    private static IResult? CityProblem(PostgresException ex) => ex.SqlState switch
    {
        PostgresErrorCodes.UniqueViolation => Results.Conflict(new { error = "city-name-in-use" }),
        PostgresErrorCodes.ForeignKeyViolation => Results.ValidationProblem(new Dictionary<string, string[]>
        {
            ["provinceId"] = ["provinceId does not match a province."],
        }),
        _ => null,
    };

    /// <summary>Account.cs's manual gate: the caller is loaded from the store and must be a live system administrator.</summary>
    private static async Task<UserAccount?> AuthorizeSystemAdminAsync(
        HttpContext httpContext, PostgresUserAccountStore userStore, CancellationToken ct)
    {
        var scope = TenantScopeEndpointFilter.GetScope(httpContext);
        var claim = httpContext.User.FindFirst(ClaimTypes.NameIdentifier)?.Value;
        if (claim is null || !Guid.TryParse(claim, out var callerId))
        {
            return null;
        }

        var caller = await userStore.LoadActorAsync(scope.IdentityScope, callerId, ct);
        return caller is { IsSystemAdmin: true, IsRevoked: false } ? caller : null;
    }
}

/// <summary>
/// Body of POST/PUT /geo/cities. `ProvinceId` is the INDEC province code ("06"),
/// required on create. On update `ProvinceId`, `DepartmentName` and `IsActive` are
/// kept when omitted; a blank `DepartmentName` clears it. The INDEC id of a city is
/// never a request field.
/// </summary>
public sealed record CityRequest(string? Name, string? ProvinceId = null, string? DepartmentName = null, bool? IsActive = null);
