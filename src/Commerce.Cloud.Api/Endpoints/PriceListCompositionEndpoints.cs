using System.Globalization;
using System.Text.RegularExpressions;
using Commerce.Cloud.Api.Persistence;
using Commerce.Cloud.Api.Pricing;
using Commerce.Cloud.Api.Tenancy;
using Commerce.Domain.Pricing;
using Npgsql;

namespace Commerce.Cloud.Api.Endpoints;

/// <summary>
/// customer-price-lists T3, on the same `/pricing` group and the same authorization as the rest of the pricing API
/// (a signed-in user holding <c>ManageCatalog</c> with a selected branch; a list of another organization or branch is
/// invisible under RLS, 404):
/// <list type="bullet">
/// <item>`GET  /price-lists/{id}/breakdown?on=` - per product: base, each component (name, percentage, amount it is
/// computed on, amount) and the final price.</item>
/// <item>`GET  /price-lists/{id}/composition?on=` - the rate set effective on a date and the list's own history.</item>
/// <item>`POST /price-lists/{id}/composition` - publishes a NEW effective-dated list-specific set (history is kept).</item>
/// <item>`POST /price-lists/{id}/copy` - a new independent list: copied base prices plus its own set.</item>
/// <item>`PUT  /price-lists/{id}/floor` - the list this one must never price below, or `null`.</item>
/// </list>
/// Every write that could put a product below its floor list answers 409 `price-below-floor` with the violations and
/// writes nothing. Every write is audited.
/// </summary>
internal static partial class PriceListCompositionEndpoints
{
    internal static void Map(RouteGroupBuilder group)
    {
        group.MapGet("/price-lists/{priceListId:guid}/breakdown", async (
            Guid priceListId,
            DateOnly? on,
            HttpContext httpContext,
            PostgresUserAccountStore userStore,
            PostgresPriceListStore priceListStore,
            PostgresRateComponentStore rateStore,
            CancellationToken ct) =>
        {
            var (failure, auth) = await AuthorizeAsync(httpContext, userStore, ct);
            if (failure is not null) return failure;
            var scope = auth!.Value.Scope;

            var priceList = await priceListStore.FindPriceListAsync(scope, priceListId, ct);
            if (priceList is null) return Results.NotFound();

            var date = on ?? Today();
            var set = await rateStore.GetEffectiveSetAsync(scope, priceListId, date, ct);
            var items = await priceListStore.ListItemsAsOfAsync(scope, priceListId, date, ct);

            return Results.Ok(new PriceBreakdownResponse(
                priceList.Id, priceList.Name, date, priceList.FloorPriceListId, ToComposition(set),
                [.. items.Select(i => ToItem(i, set))]));
        });

        group.MapGet("/price-lists/{priceListId:guid}/composition", async (
            Guid priceListId,
            DateOnly? on,
            HttpContext httpContext,
            PostgresUserAccountStore userStore,
            PostgresPriceListStore priceListStore,
            PostgresRateComponentStore rateStore,
            CancellationToken ct) =>
        {
            var (failure, auth) = await AuthorizeAsync(httpContext, userStore, ct);
            if (failure is not null) return failure;
            var scope = auth!.Value.Scope;

            if (await priceListStore.FindPriceListAsync(scope, priceListId, ct) is null) return Results.NotFound();

            var effective = ToComposition(await rateStore.GetEffectiveSetAsync(scope, priceListId, on ?? Today(), ct));
            var history = await rateStore.ListHistoryAsync(scope, priceListId, ct);
            return Results.Ok(new CompositionResponse(
                effective.Source, effective.EffectiveFrom, effective.Components,
                [.. history.Select(h => new CompositionSetResponse(h.Id, h.EffectiveFrom, [.. h.Components.Select(ToComponent)]))]));
        });

        group.MapPost("/price-lists/{priceListId:guid}/composition", async (
            Guid priceListId,
            PublishCompositionRequest request,
            HttpContext httpContext,
            PostgresUserAccountStore userStore,
            PostgresPriceListStore priceListStore,
            PostgresRateComponentStore rateStore,
            PriceFloorValidator floorValidator,
            CancellationToken ct) =>
        {
            var (failure, auth) = await AuthorizeAsync(httpContext, userStore, ct);
            if (failure is not null) return failure;
            var (scope, caller) = auth!.Value;

            if (await priceListStore.FindPriceListAsync(scope, priceListId, ct) is null) return Results.NotFound();

            var resolved = await ResolveComponentsAsync(
                scope, priceListId, request.EffectiveFrom, request.Components, request.RemarcacionPercentage, rateStore, ct);
            if (resolved.Problem is not null) return resolved.Problem;
            var components = resolved.Components!;

            var set = RateComponentSet.ForPriceList(Guid.NewGuid(), scope.OrganizationId, priceListId, request.EffectiveFrom, components);
            var violations = await floorValidator.CheckChangeAsync(scope, priceListId, request.EffectiveFrom, null, set, ct);
            if (violations.Count > 0) return BelowFloor(violations);

            try
            {
                await rateStore.PublishSetAsync(
                    scope, new NewRateComponentSet(set.Id, priceListId, request.EffectiveFrom, components, caller.Id),
                    "org-user", caller.Id, ct);
            }
            catch (PostgresException ex) when (ex.SqlState == PostgresErrorCodes.UniqueViolation)
            {
                // rate_component_sets_list_day_uk: one set per list per day (append-only history).
                return Results.Conflict(new { error = "composition-already-exists-for-date" });
            }

            return Results.Created(
                $"/pricing/price-lists/{priceListId}/composition",
                new CompositionSetResponse(set.Id, request.EffectiveFrom, [.. components.Select(ToComponent)]));
        });

        group.MapPost("/price-lists/{priceListId:guid}/copy", async (
            Guid priceListId,
            CopyPriceListRequest request,
            HttpContext httpContext,
            PostgresUserAccountStore userStore,
            PostgresPriceListStore priceListStore,
            PostgresRateComponentStore rateStore,
            PriceFloorValidator floorValidator,
            CancellationToken ct) =>
        {
            var (failure, auth) = await AuthorizeAsync(httpContext, userStore, ct);
            if (failure is not null) return failure;
            var (scope, caller) = auth!.Value;

            if (string.IsNullOrWhiteSpace(request.Name))
            {
                return Results.ValidationProblem(new Dictionary<string, string[]> { ["name"] = ["name is required."] });
            }

            var source = await priceListStore.FindPriceListAsync(scope, priceListId, ct);
            if (source is null) return Results.NotFound();

            var name = request.Name.Trim();
            var existing = await priceListStore.ListPriceListsAsync(scope, ct);
            if (existing.Any(l => string.Equals(l.Name.Trim(), name, StringComparison.CurrentCultureIgnoreCase)))
            {
                return Results.Conflict(new { error = "price-list-name-taken" });
            }

            var effectiveFrom = request.EffectiveFrom ?? Today();
            var sourceSet = await rateStore.GetEffectiveSetAsync(scope, priceListId, effectiveFrom, ct);

            IReadOnlyList<RateComponent>? components;
            if (request.Components is { Length: > 0 } || request.RemarcacionPercentage is not null)
            {
                var resolved = await ResolveComponentsAsync(
                    scope, priceListId, effectiveFrom, request.Components, request.RemarcacionPercentage, rateStore, ct);
                if (resolved.Problem is not null) return resolved.Problem;
                components = resolved.Components;
            }
            else
            {
                components = sourceSet?.Components; // as the source composes today; none when it has no set at all
            }

            Guid? floorId = request.ClearFloor ? null : request.FloorPriceListId ?? source.FloorPriceListId;
            if (floorId is { } requestedFloor && await priceListStore.FindPriceListAsync(scope, requestedFloor, ct) is null)
            {
                return Results.ValidationProblem(new Dictionary<string, string[]> { ["floorPriceListId"] = ["price-list-not-found"] });
            }

            var entries = (await priceListStore.ListItemsAsOfAsync(scope, priceListId, effectiveFrom, ct))
                .Select(i => (i.PresentationId, i.UnitPrice))
                .ToList();

            var newId = Guid.NewGuid();
            var newSet = components is null ? null : RateComponentSet.ForPriceList(Guid.NewGuid(), scope.OrganizationId, newId, effectiveFrom, components);
            if (floorId is { } floor)
            {
                var violations = await floorValidator.CheckNewListAsync(
                    scope, name, floor, effectiveFrom, entries.ToDictionary(e => e.PresentationId, e => e.UnitPrice), newSet, ct);
                if (violations.Count > 0) return BelowFloor(violations);
            }

            PriceListRecord created;
            try
            {
                created = await priceListStore.CopyPriceListAsync(
                    scope,
                    new NewPriceListCopy(
                        newId, name, priceListId, floorId, effectiveFrom, entries,
                        components is null ? null : new NewRateComponentSet(newSet!.Id, newId, effectiveFrom, components, caller.Id),
                        caller.Id),
                    "org-user", caller.Id, ct);
            }
            catch (PostgresException ex) when (ex.SqlState == PostgresErrorCodes.ForeignKeyViolation)
            {
                return Results.ValidationProblem(new Dictionary<string, string[]> { ["floorPriceListId"] = ["price-list-not-found"] });
            }

            return Results.Created(
                $"/pricing/price-lists/{created.Id}",
                new CopyPriceListResponse(
                    created, entries.Count,
                    newSet is null ? null : new CompositionSetResponse(newSet.Id, effectiveFrom, [.. newSet.Components.Select(ToComponent)])));
        });

        group.MapPut("/price-lists/{priceListId:guid}/floor", async (
            Guid priceListId,
            SetFloorRequest request,
            HttpContext httpContext,
            PostgresUserAccountStore userStore,
            PostgresPriceListStore priceListStore,
            PriceFloorValidator floorValidator,
            CancellationToken ct) =>
        {
            var (failure, auth) = await AuthorizeAsync(httpContext, userStore, ct);
            if (failure is not null) return failure;
            var (scope, caller) = auth!.Value;

            if (await priceListStore.FindPriceListAsync(scope, priceListId, ct) is null) return Results.NotFound();

            if (request.FloorPriceListId is { } floorId)
            {
                if (floorId == priceListId)
                {
                    return Results.ValidationProblem(new Dictionary<string, string[]> { ["floorPriceListId"] = ["a price list cannot be its own floor."] });
                }

                if (await priceListStore.FindPriceListAsync(scope, floorId, ct) is null)
                {
                    return Results.ValidationProblem(new Dictionary<string, string[]> { ["floorPriceListId"] = ["price-list-not-found"] });
                }

                if (await floorValidator.WouldCycleAsync(scope, priceListId, floorId, ct))
                {
                    return Results.Conflict(new { error = "floor-cycle" });
                }

                var violations = await floorValidator.CheckFloorAsync(scope, priceListId, floorId, Today(), ct);
                if (violations.Count > 0) return BelowFloor(violations);
            }

            try
            {
                var updated = await priceListStore.SetFloorAsync(scope, priceListId, request.FloorPriceListId, "org-user", caller.Id, ct);
                return updated is null ? Results.NotFound() : Results.Ok(updated);
            }
            catch (PostgresException ex) when (ex.SqlState is PostgresErrorCodes.ForeignKeyViolation or PostgresErrorCodes.CheckViolation)
            {
                return Results.ValidationProblem(new Dictionary<string, string[]> { ["floorPriceListId"] = ["price-list-not-found"] });
            }
        });
    }

    /// <summary>The 409 every floor-violating write answers with: nothing was written.</summary>
    internal static IResult BelowFloor(IReadOnlyList<FloorViolation> violations) =>
        Results.Conflict(new { error = "price-below-floor", violations });

    private static DateOnly Today() => DateOnly.FromDateTime(DateTime.UtcNow);

    private static async Task<(IResult? Failure, (CloudTenantScope Scope, Commerce.Domain.Identity.UserAccount Caller)? Auth)> AuthorizeAsync(
        HttpContext httpContext, PostgresUserAccountStore userStore, CancellationToken ct)
    {
        var branchFailure = BranchSelectionRequirement.Enforce(httpContext);
        if (branchFailure is not null)
        {
            return (branchFailure, null);
        }

        var auth = await PricingEndpoints.AuthorizeCallerAsync(httpContext, userStore, ct);
        return auth is null ? (Results.Forbid(), null) : (null, auth);
    }

    // ----------------------------------------------------------- components

    private static async Task<(IResult? Problem, IReadOnlyList<RateComponent>? Components)> ResolveComponentsAsync(
        CloudTenantScope scope, Guid priceListId, DateOnly effectiveFrom, RateComponentRequest[]? requested,
        decimal? remarcacionPercentage, PostgresRateComponentStore rateStore, CancellationToken ct)
    {
        try
        {
            if (requested is { Length: > 0 } && remarcacionPercentage is not null)
            {
                return (Problem("components", "send either components or remarcacionPercentage, not both."), null);
            }

            IReadOnlyList<RateComponent> components;
            if (requested is not null && remarcacionPercentage is null)
            {
                // An explicit (possibly empty) set: an empty one is a legitimate "no components" composition.
                components = [.. requested.Select(ToDomain)];
            }
            else if (remarcacionPercentage is { } percentage)
            {
                var current = await rateStore.GetEffectiveSetAsync(scope, priceListId, effectiveFrom, ct);
                components = WithRemarcacion(current?.Components ?? [], percentage);
            }
            else
            {
                return (Problem("components", "components or remarcacionPercentage is required."), null);
            }

            // Repeated codes or orders are refused by the set itself: surface that as a 400 here, not a 500 later.
            _ = RateComponentSet.ForPriceList(Guid.Empty, scope.OrganizationId, priceListId, effectiveFrom, components);
            return (null, components);

        }
        catch (ArgumentException ex)
        {
            // RateComponent / RateComponentSet invariants: blank code or label, negative or too fine a percentage,
            // undeclared calculation base, a repeated code or order.
            return (Problem("components", ex.Message), null);
        }
    }

    private static IResult Problem(string field, string message) =>
        Results.ValidationProblem(new Dictionary<string, string[]> { [field] = [message] });

    private static RateComponent ToDomain(RateComponentRequest c)
    {
        if (!Enum.TryParse<RateCalculationBase>(c.CalculationBase, ignoreCase: true, out var calculationBase)
            || calculationBase is RateCalculationBase.Unspecified)
        {
            throw new ArgumentException($"calculationBase of '{c.Code}' must be Base or Subtotal.");
        }

        return new RateComponent(c.Code ?? string.Empty, c.Label ?? string.Empty, c.Percentage, calculationBase, c.Order);
    }

    /// <summary>
    /// The components of a set with the markup (`REMARCACION`) replaced by <paramref name="percentage"/>, or appended on
    /// the base when the set has none. Every other component is kept exactly.
    /// </summary>
    internal static IReadOnlyList<RateComponent> WithRemarcacion(IReadOnlyList<RateComponent> components, decimal percentage)
    {
        const string Code = "REMARCACION";
        var text = percentage.ToString("0.####", CultureInfo.InvariantCulture).Replace('.', ',');
        var updated = new List<RateComponent>(components.Count + 1);
        var replaced = false;

        foreach (var component in components)
        {
            if (!replaced && string.Equals(component.Code, Code, StringComparison.OrdinalIgnoreCase))
            {
                var label = LabelPercentage().IsMatch(component.Label)
                    ? LabelPercentage().Replace(component.Label, $"({text}%)")
                    : $"{component.Label} ({text}%)";
                updated.Add(new RateComponent(component.Code, label, percentage, component.CalculationBase, component.Order));
                replaced = true;
            }
            else
            {
                updated.Add(component);
            }
        }

        if (!replaced)
        {
            var order = components.Count == 0 ? 1 : components.Max(c => c.Order) + 1;
            updated.Add(new RateComponent(Code, $"Remarcación ({text}%)", percentage, RateCalculationBase.Base, order));
        }

        return updated;
    }

    [GeneratedRegex(@"\(\s*[\d.,]+\s*%\s*\)")]
    private static partial Regex LabelPercentage();

    // ------------------------------------------------------------ responses

    private static ComponentResponse ToComponent(RateComponent c) =>
        new(c.Code, c.Label, c.Percentage, c.CalculationBase.ToString(), c.Order);

    private static CompositionResponse ToComposition(RateComponentSet? set) => new(
        set is null ? "none" : set.IsOrganizationDefault ? "organization" : "list",
        set?.EffectiveFrom,
        [.. (set?.Components ?? []).Select(ToComponent)],
        []);

    private static PriceBreakdownItemResponse ToItem(PriceListItemRecord item, RateComponentSet? set)
    {
        var breakdown = (set ?? RateComponentSet.ForOrganizationDefault(Guid.Empty, Guid.Empty, DateOnly.MinValue, [])).Breakdown(item.UnitPrice);
        return new PriceBreakdownItemResponse(
            item.PresentationId, item.ProductId, item.ProductName, item.PresentationName, item.IdentificationCode,
            item.EntryEffectiveFrom, breakdown.BasePrice,
            [.. breakdown.Lines.Select(l => new BreakdownComponentResponse(
                l.Code, l.Label, l.Percentage, l.CalculationBase.ToString(), l.Order, l.CalculationAmount, l.Amount))],
            Money.Round2(breakdown.FinalPrice));
    }
}

public sealed record RateComponentRequest(string? Code, string? Label, decimal Percentage, string? CalculationBase, int Order);

public sealed record PublishCompositionRequest(DateOnly EffectiveFrom, RateComponentRequest[]? Components = null, decimal? RemarcacionPercentage = null);

public sealed record CopyPriceListRequest(
    string? Name, DateOnly? EffectiveFrom = null, decimal? RemarcacionPercentage = null, RateComponentRequest[]? Components = null,
    Guid? FloorPriceListId = null, bool ClearFloor = false);

public sealed record SetFloorRequest(Guid? FloorPriceListId);

public sealed record ComponentResponse(string Code, string Label, decimal Percentage, string CalculationBase, int Order);

public sealed record BreakdownComponentResponse(
    string Code, string Label, decimal Percentage, string CalculationBase, int Order, decimal CalculationAmount, decimal Amount);

public sealed record PriceBreakdownItemResponse(
    Guid PresentationId, Guid ProductId, string ProductName, string PresentationName, string? IdentificationCode,
    DateOnly EntryEffectiveFrom, decimal Base, IReadOnlyList<BreakdownComponentResponse> Components, decimal Final);

public sealed record CompositionSetResponse(Guid Id, DateOnly EffectiveFrom, IReadOnlyList<ComponentResponse> Components);

public sealed record CompositionResponse(
    string Source, DateOnly? EffectiveFrom, IReadOnlyList<ComponentResponse> Components, IReadOnlyList<CompositionSetResponse> History);

public sealed record PriceBreakdownResponse(
    Guid PriceListId, string PriceListName, DateOnly On, Guid? FloorPriceListId, CompositionResponse Composition,
    IReadOnlyList<PriceBreakdownItemResponse> Items);

public sealed record CopyPriceListResponse(PriceListRecord PriceList, int EntriesCopied, CompositionSetResponse? Composition);
