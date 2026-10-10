using System.Security.Claims;
using System.Text.Json;
using Commerce.Application.Time;
using Commerce.Cloud.Api.Auditing;
using Commerce.Cloud.Api.Persistence;
using Commerce.Cloud.Api.Tenancy;
using Commerce.Domain.Tenancy;

namespace Commerce.Cloud.Api.Endpoints;

/// <summary>
/// The system administrator's account-standing actions on one organization (odd/tasks/organization-account-standing.md
/// T3): read the standing, set the billing due date and grace days (also how a payment is recorded: move the due date to
/// the next period), suspend now, and reactivate with a new due date. Gated exactly like the branding routes: the caller
/// must be a system administrator, checked against the store; the route id is trusted only after that. Every write is
/// audited in its own transaction. The standing is derived on the business day (<see cref="IBusinessClock"/>).
/// </summary>
public static class OrganizationAccountStandingEndpoints
{
    public static RouteGroupBuilder MapOrganizationAccountStandingEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/account/organizations/{id:guid}/standing").RequireAuthorization();

        group.MapGet("", async (Guid id, HttpContext httpContext, PostgresUserAccountStore userStore, PostgresOrganizationStore organizationStore, IBusinessClock clock, CancellationToken ct) =>
        {
            if (await SystemAdminIdAsync(httpContext, userStore, ct) is null) return Results.StatusCode(StatusCodes.Status403Forbidden);
            var inputs = await organizationStore.GetAccountStandingInputsAsync(id, ct);
            return inputs is null ? Results.NotFound() : Results.Ok(ToResponse(inputs, clock.Today));
        });

        group.MapPut("", async (Guid id, UpdateOrganizationAccountStandingRequest request, HttpContext httpContext, PostgresUserAccountStore userStore, PostgresOrganizationStore organizationStore, CancellationToken ct) =>
        {
            if (await SystemAdminIdAsync(httpContext, userStore, ct) is not { } actorId) return Results.StatusCode(StatusCodes.Status403Forbidden);
            if (!AccountStandingRules.IsValidGraceDays(request.GraceDays)) return GraceDaysProblem();

            var audit = Audit(actorId, id, "organization.standing_updated", new { dueOn = request.DueOn, graceDays = request.GraceDays });
            return await organizationStore.UpdateBillingAsync(id, request.DueOn, request.GraceDays, audit, ct)
                ? Results.NoContent()
                : Results.NotFound();
        });

        group.MapPost("/suspend", async (Guid id, HttpContext httpContext, PostgresUserAccountStore userStore, PostgresOrganizationStore organizationStore, CancellationToken ct) =>
        {
            if (await SystemAdminIdAsync(httpContext, userStore, ct) is not { } actorId) return Results.StatusCode(StatusCodes.Status403Forbidden);

            return await organizationStore.SuspendAsync(id, Audit(actorId, id, "organization.suspended", new { }), ct)
                ? Results.NoContent()
                : Results.NotFound();
        });

        group.MapPost("/reactivate", async (Guid id, ReactivateOrganizationRequest request, HttpContext httpContext, PostgresUserAccountStore userStore, PostgresOrganizationStore organizationStore, IBusinessClock clock, CancellationToken ct) =>
        {
            if (await SystemAdminIdAsync(httpContext, userStore, ct) is not { } actorId) return Results.StatusCode(StatusCodes.Status403Forbidden);
            if (request.GraceDays is { } requested && !AccountStandingRules.IsValidGraceDays(requested)) return GraceDaysProblem();
            // A due date already behind the business day would reactivate straight into Overdue or Suspended.
            if (request.DueOn < clock.Today)
            {
                return Results.ValidationProblem(new Dictionary<string, string[]> { ["dueOn"] = ["dueOn must be today or later."] });
            }

            var current = await organizationStore.GetAccountStandingInputsAsync(id, ct);
            if (current is null) return Results.NotFound();

            var graceDays = request.GraceDays ?? current.GraceDays;
            var audit = Audit(actorId, id, "organization.reactivated", new { dueOn = request.DueOn, graceDays });
            return await organizationStore.ReactivateAsync(id, request.DueOn, graceDays, audit, ct)
                ? Results.NoContent()
                : Results.NotFound();
        });

        return group;
    }

    /// <summary>The standing derived from the stored inputs on <paramref name="today"/>, plus the inputs themselves.</summary>
    public static OrganizationAccountStandingResponse ToResponse(OrganizationAccountStandingInputs inputs, DateOnly today)
    {
        var standing = AccountStandingRules.Evaluate(inputs.DueOn, inputs.GraceDays, inputs.SuspendedAt is not null, today);
        return new OrganizationAccountStandingResponse(
            inputs.DueOn, inputs.GraceDays, inputs.SuspendedAt, standing.Status.ToString(), standing.SuspendsOn, standing.DaysLeft);
    }

    /// <summary>The caller's user id when it is a system administrator; otherwise <c>null</c>.</summary>
    private static async Task<Guid?> SystemAdminIdAsync(HttpContext httpContext, PostgresUserAccountStore userStore, CancellationToken ct)
    {
        if (!TenantScopeResolver.TryResolve(httpContext.User, out var scope, out _)) return null;
        var claim = httpContext.User.FindFirst(ClaimTypes.NameIdentifier)?.Value;
        if (claim is null || !Guid.TryParse(claim, out var actorId)) return null;
        var actor = await userStore.LoadActorAsync(scope!, actorId, ct);
        return actor is { IsSystemAdmin: true } ? actorId : null;
    }

    private static UserManagementAuditEntry Audit(Guid actorId, Guid organizationId, string action, object after) =>
        new("org-user", actorId, organizationId, "organization", organizationId, action, null, JsonSerializer.Serialize(after));

    private static IResult GraceDaysProblem() =>
        Results.ValidationProblem(new Dictionary<string, string[]>
        {
            ["graceDays"] = [$"graceDays must be between 0 and {AccountStandingRules.MaxGraceDays}."],
        });
}

/// <summary>
/// An organization's account standing: the stored inputs (<see cref="DueOn"/>, <see cref="GraceDays"/>,
/// <see cref="SuspendedAt"/>) and what they mean today (<see cref="Status"/>: Active | Overdue | Suspended,
/// <see cref="SuspendsOn"/>, <see cref="DaysLeft"/>).
/// </summary>
public sealed record OrganizationAccountStandingResponse(
    DateOnly? DueOn, int GraceDays, DateTimeOffset? SuspendedAt, string Status, DateOnly? SuspendsOn, int? DaysLeft);

/// <summary><see cref="DueOn"/> null stops tracking billing for the organization.</summary>
public sealed record UpdateOrganizationAccountStandingRequest(DateOnly? DueOn, int GraceDays);

/// <summary><see cref="GraceDays"/> null keeps the organization's current grace days.</summary>
public sealed record ReactivateOrganizationRequest(DateOnly DueOn, int? GraceDays);
