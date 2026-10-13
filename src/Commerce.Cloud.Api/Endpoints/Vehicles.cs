using Commerce.Cloud.Api.Persistence;
using Commerce.Cloud.Api.Tenancy;
using Commerce.Domain.Identity;
using Commerce.Domain.Vehicles;

namespace Commerce.Cloud.Api.Endpoints;

/// <summary>
/// vehicles V1: the business's vehicles (0057), a lookup table of the organization listed by plate. Anyone on the staff
/// reads it (a delivery run picks from it); the administration (<see cref="Permission.ManageUsers"/>, as the other lookup
/// tables) creates and edits. 409 <c>vehicle-plate-in-use</c>: another vehicle of the organization has that plate.
/// </summary>
public static class VehicleEndpoints
{
    public static IEndpointRouteBuilder MapVehicleEndpoints(this IEndpointRouteBuilder app)
    {
        var vehicles = app.MapGroup("/vehicles").RequireAuthorization().AddEndpointFilter<TenantScopeEndpointFilter>();

        vehicles.MapGet("", async (
            bool? includeInactive, HttpContext httpContext, PostgresUserAccountStore userStore, PostgresVehicleStore store, CancellationToken ct) =>
        {
            // Any staff permission reads it, as the other lookup tables.
            var (denied, caller) = await StaffAuthorization.AuthorizeAsync(httpContext, userStore, ct,
                Permission.ViewSales, Permission.ManageCatalog, Permission.ManageUsers, Permission.ManageBranchSettings, Permission.OperatePos,
                Permission.TakeOrders);
            if (denied is not null) return denied;
            return Results.Ok(await store.ListAsync(caller!.Scope, includeInactive ?? false, ct));
        });

        vehicles.MapPost("", async (
            VehicleRequest request, HttpContext httpContext, PostgresUserAccountStore userStore, PostgresVehicleStore store, CancellationToken ct) =>
        {
            var (denied, caller) = await StaffAuthorization.AuthorizeAsync(httpContext, userStore, ct, Permission.ManageUsers);
            if (denied is not null) return denied;
            if (Validate(request, httpContext.Today(), out var input) is { } invalid) return invalid;
            var (outcome, vehicle) = await store.CreateAsync(caller!.Scope, caller.Id, input!, ct);
            return outcome == VehicleWriteOutcome.Done ? Results.Created($"/vehicles/{vehicle!.Id}", vehicle) : Refusal(outcome);
        });

        vehicles.MapPut("/{id:guid}", async (
            Guid id, VehicleRequest request, HttpContext httpContext, PostgresUserAccountStore userStore, PostgresVehicleStore store,
            CancellationToken ct) =>
        {
            var (denied, caller) = await StaffAuthorization.AuthorizeAsync(httpContext, userStore, ct, Permission.ManageUsers);
            if (denied is not null) return denied;
            if (Validate(request, httpContext.Today(), out var input) is { } invalid) return invalid;
            var (outcome, vehicle) = await store.UpdateAsync(caller!.Scope, caller.Id, id, input!, ct);
            return outcome == VehicleWriteOutcome.Done ? Results.Ok(vehicle) : Refusal(outcome);
        });

        return app;
    }

    private static IResult? Validate(VehicleRequest request, DateOnly today, out VehicleInput? input)
    {
        input = null;
        var errors = new Dictionary<string, string[]>();
        var plate = VehicleRules.NormalizePlate(request.Plate);
        if (!VehicleRules.IsValidPlate(plate)) errors["plate"] = ["5 to 10 letters or digits (spaces and dashes are left out)."];
        if (request.Brand?.Trim() is { Length: > VehicleRules.MaxBrandLength }) errors["brand"] = [$"At most {VehicleRules.MaxBrandLength} characters."];
        if (request.Model?.Trim() is { Length: > VehicleRules.MaxModelLength }) errors["model"] = [$"At most {VehicleRules.MaxModelLength} characters."];
        if (request.Year is { } year && !VehicleRules.IsValidYear(year, today)) errors["year"] = [$"From {VehicleRules.FirstYear} to next year."];
        if (request.Notes?.Trim() is { Length: > VehicleRules.MaxNotesLength }) errors["notes"] = [$"At most {VehicleRules.MaxNotesLength} characters."];
        if (errors.Count > 0) return Results.ValidationProblem(errors);

        input = new VehicleInput(plate, request.Brand, request.Model, request.Year, request.Notes, request.IsActive ?? true);
        return null;
    }

    private static IResult Refusal(VehicleWriteOutcome outcome) => outcome switch
    {
        VehicleWriteOutcome.NotFound => Results.NotFound(),
        _ => Results.Conflict(new { error = "vehicle-plate-in-use" }),
    };
}

/// <summary>A vehicle as the web form sends it; IsActive omitted: active.</summary>
public sealed record VehicleRequest(string? Plate, string? Brand, string? Model, int? Year, string? Notes, bool? IsActive);
