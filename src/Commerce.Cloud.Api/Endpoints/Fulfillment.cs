using System.Security.Claims;
using System.Text.RegularExpressions;
using Commerce.Application.Time;
using Commerce.Cloud.Api.Persistence;
using Commerce.Cloud.Api.Tenancy;
using Commerce.Domain.Identity;
using Commerce.Domain.Ordering;

namespace Commerce.Cloud.Api.Endpoints;

/// <summary>
/// Order fulfillment of the selected branch (0045), for the staff who take and deliver orders
/// (<see cref="Permission.TakeOrders"/>: seller, business admin, a system administrator acting on the organization):
/// <list type="bullet">
/// <item><c>/orders/tracking</c>: the branch's orders with their status, one order's detail, a manual status step, and
/// the remitos (delivery notes) of one or many orders;</item>
/// <item><c>/deliveries/runs</c>: delivery runs (repartos): create and edit while planned, dispatch, and settle on
/// return (stock and current accounts move then).</item>
/// </list>
/// The document data printed on remitos is edited under <c>/account/organization/document-profile</c> and
/// <c>/account/branches/{id}/document-profile</c> by whoever manages the branch settings
/// (<see cref="Permission.ManageBranchSettings"/>); everyone who prints a remito reads it there.
/// Every route needs a selected branch, except the document data of the organization. Denials are literal 403s.
/// </summary>
public static partial class FulfillmentEndpoints
{
    public const int MaxRemitosPerRequest = 200;

    public static void MapFulfillmentEndpoints(this IEndpointRouteBuilder app)
    {
        var orders = app.MapGroup("/orders/tracking").RequireAuthorization().AddEndpointFilter<TenantScopeEndpointFilter>();

        orders.MapGet("", async (
            string? status, DateOnly? from, DateOnly? to, string? search,
            HttpContext httpContext, PostgresUserAccountStore userStore, PostgresFulfillmentStore store, IConfiguration configuration,
            CancellationToken ct) =>
        {
            var auth = await AuthorizeAsync(httpContext, userStore, Permission.TakeOrders, requireBranch: true, ct);
            if (auth.Result is not null) return auth.Result;
            if (status is not null and not "Active" && !Enum.TryParse<OrderFulfillmentStatus>(status, out _))
            {
                return Results.ValidationProblem(new Dictionary<string, string[]> { ["status"] = ["Unknown status."] });
            }

            var zone = BusinessTimeZone.Resolve(configuration["Business:TimeZone"]);
            return Results.Ok(await store.ListOrdersAsync(
                auth.Scope!, string.IsNullOrWhiteSpace(status) ? null : status, StartOfDay(from, zone), StartOfDay(to?.AddDays(1), zone), search, ct));
        });

        orders.MapGet("/{orderId:guid}", async (
            Guid orderId, HttpContext httpContext, PostgresUserAccountStore userStore, PostgresFulfillmentStore store, CancellationToken ct) =>
        {
            var auth = await AuthorizeAsync(httpContext, userStore, Permission.TakeOrders, requireBranch: true, ct);
            if (auth.Result is not null) return auth.Result;
            return await store.GetOrderAsync(auth.Scope!, orderId, ct) is { } detail ? Results.Ok(detail) : Results.NotFound();
        });

        orders.MapPost("/{orderId:guid}/status", async (
            Guid orderId, ChangeOrderStatusRequest request, HttpContext httpContext, PostgresUserAccountStore userStore,
            PostgresFulfillmentStore store, CancellationToken ct) =>
        {
            var auth = await AuthorizeAsync(httpContext, userStore, Permission.TakeOrders, requireBranch: true, ct);
            if (auth.Result is not null) return auth.Result;
            if (!Enum.TryParse<OrderFulfillmentStatus>(request.Status, out var target))
            {
                return Results.ValidationProblem(new Dictionary<string, string[]> { ["status"] = ["Unknown status."] });
            }

            return ToResult(await store.ChangeStatusAsync(auth.Scope!, orderId, target, request.Reason, auth.CallerId, ct));
        });

        orders.MapPost("/remitos", async (
            RemitosRequest request, HttpContext httpContext, PostgresUserAccountStore userStore, PostgresFulfillmentStore store,
            CancellationToken ct) =>
        {
            var auth = await AuthorizeAsync(httpContext, userStore, Permission.TakeOrders, requireBranch: true, ct);
            if (auth.Result is not null) return auth.Result;
            if (request.OrderIds is not { Count: > 0 } ids || ids.Count > MaxRemitosPerRequest)
            {
                return Results.ValidationProblem(new Dictionary<string, string[]>
                {
                    ["orderIds"] = [$"Between 1 and {MaxRemitosPerRequest} orders."],
                });
            }

            return Results.Ok(await store.GetRemitosAsync(auth.Scope!, ids, httpContext.Today(), ct));
        });

        var runs = app.MapGroup("/deliveries/runs").RequireAuthorization().AddEndpointFilter<TenantScopeEndpointFilter>();

        runs.MapGet("", async (
            DateOnly? from, DateOnly? to, string? status, HttpContext httpContext, PostgresUserAccountStore userStore,
            PostgresFulfillmentStore store, CancellationToken ct) =>
        {
            var auth = await AuthorizeAsync(httpContext, userStore, Permission.TakeOrders, requireBranch: true, ct);
            if (auth.Result is not null) return auth.Result;
            return Results.Ok(await store.ListRunsAsync(auth.Scope!, from, to, status, ct));
        });

        runs.MapGet("/{runId:guid}", async (
            Guid runId, HttpContext httpContext, PostgresUserAccountStore userStore, PostgresFulfillmentStore store, CancellationToken ct) =>
        {
            var auth = await AuthorizeAsync(httpContext, userStore, Permission.TakeOrders, requireBranch: true, ct);
            if (auth.Result is not null) return auth.Result;
            return await store.GetRunAsync(auth.Scope!, runId, ct) is { } run ? Results.Ok(run) : Results.NotFound();
        });

        runs.MapPost("", async (
            DeliveryRunRequest request, HttpContext httpContext, PostgresUserAccountStore userStore, PostgresFulfillmentStore store,
            CancellationToken ct) =>
        {
            var auth = await AuthorizeAsync(httpContext, userStore, Permission.TakeOrders, requireBranch: true, ct);
            if (auth.Result is not null) return auth.Result;
            if (Validate(request) is { } invalid) return invalid;

            var result = await store.CreateRunAsync(
                auth.Scope!, request.RunDate, request.DriverName, request.Vehicle, request.Notes, request.OrderIds ?? [], auth.CallerId, ct);
            return result.Outcome == FulfillmentOutcome.Done
                ? Results.Created($"/deliveries/runs/{result.Id}", new { runId = result.Id })
                : ToResult(result);
        });

        runs.MapPut("/{runId:guid}", async (
            Guid runId, DeliveryRunRequest request, HttpContext httpContext, PostgresUserAccountStore userStore,
            PostgresFulfillmentStore store, CancellationToken ct) =>
        {
            var auth = await AuthorizeAsync(httpContext, userStore, Permission.TakeOrders, requireBranch: true, ct);
            if (auth.Result is not null) return auth.Result;
            if (Validate(request) is { } invalid) return invalid;

            return ToResult(await store.UpdateRunAsync(
                auth.Scope!, runId, request.RunDate, request.DriverName, request.Vehicle, request.Notes, request.OrderIds ?? [],
                auth.CallerId, ct));
        });

        // A run planned wrong is discarded while still Planned; its orders are free again.
        runs.MapDelete("/{runId:guid}", async (
            Guid runId, HttpContext httpContext, PostgresUserAccountStore userStore, PostgresFulfillmentStore store, CancellationToken ct) =>
        {
            var auth = await AuthorizeAsync(httpContext, userStore, Permission.TakeOrders, requireBranch: true, ct);
            if (auth.Result is not null) return auth.Result;
            return ToResult(await store.DeleteRunAsync(auth.Scope!, runId, auth.CallerId, ct));
        });

        runs.MapPost("/{runId:guid}/dispatch", async (
            Guid runId, HttpContext httpContext, PostgresUserAccountStore userStore, PostgresFulfillmentStore store, CancellationToken ct) =>
        {
            var auth = await AuthorizeAsync(httpContext, userStore, Permission.TakeOrders, requireBranch: true, ct);
            if (auth.Result is not null) return auth.Result;
            return ToResult(await store.DispatchRunAsync(auth.Scope!, runId, auth.CallerId, ct));
        });

        runs.MapPost("/{runId:guid}/settle", async (
            Guid runId, SettleRunRequest request, HttpContext httpContext, PostgresUserAccountStore userStore,
            PostgresFulfillmentStore store, CancellationToken ct) =>
        {
            var auth = await AuthorizeAsync(httpContext, userStore, Permission.TakeOrders, requireBranch: true, ct);
            if (auth.Result is not null) return auth.Result;
            if (request.Orders is not { Count: > 0 } returns)
            {
                return Results.ValidationProblem(new Dictionary<string, string[]> { ["orders"] = ["Every order of the run is required."] });
            }

            return ToResult(await store.SettleRunAsync(auth.Scope!, runId, returns, auth.CallerId, httpContext.Today(), ct));
        });

        var organization = app.MapGroup("/account/organization/document-profile").RequireAuthorization().AddEndpointFilter<TenantScopeEndpointFilter>();

        organization.MapGet("", async (
            HttpContext httpContext, PostgresUserAccountStore userStore, PostgresFulfillmentStore store, CancellationToken ct) =>
        {
            var auth = await AuthorizeAsync(httpContext, userStore, Permission.None, requireBranch: false, ct);
            if (auth.Result is not null) return auth.Result;
            return await store.GetOrganizationProfileAsync(auth.Scope!, ct) is { } profile ? Results.Ok(profile) : Results.NotFound();
        });

        organization.MapPut("", async (
            OrganizationDocumentProfileRequest request, HttpContext httpContext, PostgresUserAccountStore userStore,
            PostgresFulfillmentStore store, CancellationToken ct) =>
        {
            var auth = await AuthorizeAsync(httpContext, userStore, Permission.ManageBranchSettings, requireBranch: false, ct);
            if (auth.Result is not null) return auth.Result;

            var taxId = request.TaxId is null ? null : DigitsOnly().Replace(request.TaxId, string.Empty);
            var errors = new Dictionary<string, string[]>();
            if (!string.IsNullOrEmpty(taxId) && taxId.Length != 11) errors["taxId"] = ["The CUIT has 11 digits."];
            if (request.TaxCondition is { Length: > 0 } condition && !TaxConditions.Contains(condition)) errors["taxCondition"] = ["Unknown tax condition."];
            if (request.LegalName is { Length: > 200 }) errors["legalName"] = ["At most 200 characters."];
            if (request.GrossIncomeNumber is { Length: > 40 }) errors["grossIncomeNumber"] = ["At most 40 characters."];
            if (request.FiscalAddress is { Length: > 200 }) errors["fiscalAddress"] = ["At most 200 characters."];
            if (request.DocumentFooter is { Length: > 300 }) errors["documentFooter"] = ["At most 300 characters."];
            var logoUrl = string.IsNullOrWhiteSpace(request.LogoUrl) ? null : request.LogoUrl.Trim();
            if (logoUrl is not null
                && (logoUrl.Length > 2048 || !Uri.TryCreate(logoUrl, UriKind.Absolute, out var parsed)
                    || (parsed.Scheme != Uri.UriSchemeHttp && parsed.Scheme != Uri.UriSchemeHttps)))
            {
                errors["logoUrl"] = ["The logo must be an absolute http or https URL of at most 2048 characters."];
            }

            var color = string.IsNullOrWhiteSpace(request.PrimaryColor) ? null : request.PrimaryColor.Trim();
            if (color is not null && !HexColor().IsMatch(color)) errors["primaryColor"] = ["The color is #rrggbb."];
            if (errors.Count > 0) return Results.ValidationProblem(errors);

            var profile = new OrganizationDocumentProfile(
                string.Empty, request.LegalName, string.IsNullOrEmpty(taxId) ? null : taxId, request.TaxCondition,
                request.GrossIncomeNumber, request.ActivityStartDate, request.FiscalAddress, request.DocumentFooter, logoUrl, color);
            return await store.UpdateOrganizationProfileAsync(auth.Scope!, profile, auth.CallerId, ct) ? Results.NoContent() : Results.NotFound();
        });

        var branches = app.MapGroup("/account/branch-profiles").RequireAuthorization().AddEndpointFilter<TenantScopeEndpointFilter>();

        branches.MapGet("", async (
            HttpContext httpContext, PostgresUserAccountStore userStore, PostgresFulfillmentStore store, CancellationToken ct) =>
        {
            var auth = await AuthorizeAsync(httpContext, userStore, Permission.None, requireBranch: false, ct);
            if (auth.Result is not null) return auth.Result;
            return Results.Ok(await store.ListBranchProfilesAsync(auth.Scope!, ct));
        });

        branches.MapPut("/{branchId:guid}", async (
            Guid branchId, BranchDocumentProfileRequest request, HttpContext httpContext, PostgresUserAccountStore userStore,
            PostgresFulfillmentStore store, CancellationToken ct) =>
        {
            var auth = await AuthorizeAsync(httpContext, userStore, Permission.ManageBranchSettings, requireBranch: false, ct);
            if (auth.Result is not null) return auth.Result;

            var errors = new Dictionary<string, string[]>();
            if (request.Address is { Length: > 200 }) errors["address"] = ["At most 200 characters."];
            if (request.Locality is { Length: > 120 }) errors["locality"] = ["At most 120 characters."];
            if (request.Phone is { Length: > 60 }) errors["phone"] = ["At most 60 characters."];
            if (request.Email is { Length: > 200 }) errors["email"] = ["At most 200 characters."];
            if (request.WarehouseAddress is { Length: > 200 }) errors["warehouseAddress"] = ["At most 200 characters."];
            if (errors.Count > 0) return Results.ValidationProblem(errors);

            var profile = new BranchDocumentProfile(
                branchId, string.Empty, 0, request.Address, request.Locality, request.Phone, request.Email, request.WarehouseAddress);
            return await store.UpdateBranchProfileAsync(auth.Scope!, profile, auth.CallerId, ct) ? Results.NoContent() : Results.NotFound();
        });
    }

    private static readonly HashSet<string> TaxConditions =
        ["ResponsableInscripto", "Monotributo", "Exento", "ConsumidorFinal", "NoAplica"];

    [GeneratedRegex(@"\D")]
    private static partial Regex DigitsOnly();

    [GeneratedRegex("^#[0-9a-fA-F]{6}$")]
    private static partial Regex HexColor();

    private static DateTimeOffset? StartOfDay(DateOnly? day, TimeZoneInfo zone)
    {
        if (day is not { } value)
        {
            return null;
        }

        var local = value.ToDateTime(TimeOnly.MinValue, DateTimeKind.Unspecified);
        return new DateTimeOffset(local, zone.GetUtcOffset(local)).ToUniversalTime();
    }

    private static IResult? Validate(DeliveryRunRequest request)
    {
        var errors = new Dictionary<string, string[]>();
        if (request.RunDate == default) errors["runDate"] = ["runDate is required."];
        if (request.DriverName is { Length: > 120 }) errors["driverName"] = ["At most 120 characters."];
        if (request.Vehicle is { Length: > 120 }) errors["vehicle"] = ["At most 120 characters."];
        if (request.Notes is { Length: > 500 }) errors["notes"] = ["At most 500 characters."];
        if (request.OrderIds is { Count: > MaxRemitosPerRequest }) errors["orderIds"] = [$"At most {MaxRemitosPerRequest} orders."];
        return errors.Count == 0 ? null : Results.ValidationProblem(errors);
    }

    private static IResult ToResult(FulfillmentResult result) => result.Outcome switch
    {
        FulfillmentOutcome.Done => Results.NoContent(),
        FulfillmentOutcome.NotFound => Results.NotFound(),
        FulfillmentOutcome.InvalidTransition => Results.Conflict(new { error = "invalid-transition", detail = result.Error }),
        FulfillmentOutcome.Conflict => Results.Conflict(new { error = result.Error }),
        _ => Results.BadRequest(new { error = result.Error }),
    };

    /// <summary>
    /// The selected branch when <paramref name="requireBranch"/> (400 <c>branch-selection-required</c> otherwise), then the
    /// caller: loaded through the store, not revoked, holding <paramref name="permission"/> for this request
    /// (<see cref="Permission.None"/> = any staff role of the organization). 403 otherwise.
    /// </summary>
    private static async Task<(IResult? Result, CloudTenantScope? Scope, Guid CallerId)> AuthorizeAsync(
        HttpContext httpContext, PostgresUserAccountStore userStore, Permission permission, bool requireBranch, CancellationToken ct)
    {
        if (requireBranch && BranchSelectionRequirement.Enforce(httpContext) is { } branchFailure)
        {
            return (branchFailure, null, Guid.Empty);
        }

        var scope = TenantScopeEndpointFilter.GetScope(httpContext);
        var claim = httpContext.User.FindFirst(ClaimTypes.NameIdentifier)?.Value;
        if (claim is null || !Guid.TryParse(claim, out var callerId))
        {
            return (Results.StatusCode(StatusCodes.Status403Forbidden), null, Guid.Empty);
        }

        var caller = await userStore.LoadActorAsync(scope.IdentityScope, callerId, ct);
        var granted = caller is null ? Permission.None : ActingPermissions.For(caller, scope);
        var allowed = caller is { IsRevoked: false }
                      && (permission == Permission.None ? granted != Permission.None : granted.HasFlag(permission));
        return allowed ? (null, scope, callerId) : (Results.StatusCode(StatusCodes.Status403Forbidden), null, Guid.Empty);
    }
}

public sealed record ChangeOrderStatusRequest(string Status, string? Reason);

public sealed record RemitosRequest(IReadOnlyList<Guid>? OrderIds);

public sealed record DeliveryRunRequest(DateOnly RunDate, string? DriverName, string? Vehicle, string? Notes, IReadOnlyList<Guid>? OrderIds);

public sealed record SettleRunRequest(IReadOnlyList<OrderReturnInput>? Orders);

public sealed record OrganizationDocumentProfileRequest(
    string? LegalName,
    string? TaxId,
    string? TaxCondition,
    string? GrossIncomeNumber,
    DateOnly? ActivityStartDate,
    string? FiscalAddress,
    string? DocumentFooter,
    string? LogoUrl,
    string? PrimaryColor);

public sealed record BranchDocumentProfileRequest(
    string? Address, string? Locality, string? Phone, string? Email, string? WarehouseAddress);
