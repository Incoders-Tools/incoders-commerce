using Commerce.Cloud.Api.Auditing;
using Commerce.Cloud.Api.Tenancy;
using Commerce.Domain.Pricing;
using Npgsql;

namespace Commerce.Cloud.Api.Persistence;

/// <summary>
/// Real Npgsql-backed rate component store (commerce-price-composition slice
/// 1), mirroring <see cref="PostgresPriceListStore"/>'s exact shape: raw
/// <see cref="NpgsqlDataSource"/>, every scoped method opens its own
/// <see cref="NpgsqlTransaction"/>, `set_config` is always the FIRST
/// statement, mutations write their audit row in the SAME transaction.
///
/// <see cref="PublishSetAsync"/> is the ONLY write path onto
/// `rate_component_sets`/`rate_components` and is a pure INSERT. There is no
/// update or delete method here and none can be added usefully:
/// migration `0013` grants `app_runtime` only `SELECT, INSERT`, so
/// append-only is enforced by the database rather than by this class. A
/// same-day double-publish surfaces as a <see cref="PostgresException"/>
/// with SqlState `23505` for the caller to translate into a 409.
///
/// Reads return the DOMAIN <see cref="RateComponentSet"/> rather than a
/// parallel persistence record. The row shape and the aggregate are the same
/// five facts, and rebuilding through the domain constructor means every
/// read re-validates the invariants (no duplicate code, no duplicate order,
/// a declared calculation base) instead of trusting the table.
///
/// As of slice 2, <see cref="GetEffectiveSetAsync"/> is on the resolution path:
/// `PostgresRateComponentSource` adapts it to
/// `IEffectiveRateComponentSource`, and `PricingResolutionService` composes its
/// result onto the entry's base price. This store stays the ONLY place the
/// effective-set selection rules (list first, organization default second,
/// all-or-nothing, `null` for none) are written.
/// </summary>
public sealed class PostgresRateComponentStore
{
    private readonly NpgsqlDataSource _dataSource;

    public PostgresRateComponentStore(NpgsqlDataSource dataSource) => _dataSource = dataSource;

    private static async Task SetTenantScopeAsync(
        NpgsqlConnection connection, NpgsqlTransaction tx, CloudTenantScope scope, CancellationToken ct)
    {
        await using var scopeCmd = new NpgsqlCommand("SELECT set_config('app.current_org_id', $1, true)", connection, tx);
        scopeCmd.Parameters.AddWithValue(scope.OrganizationId.ToString());
        await scopeCmd.ExecuteNonQueryAsync(ct);
    }

    private const string SetColumns = "id, organization_id, price_list_id, effective_from";

    private const string ComponentColumns = "code, label, percentage, calculation_base, component_order";

    /// <summary>
    /// Publishes a new dated set: one INSERT for the set, one per component,
    /// one audit row, all in a single transaction — a half-written set would
    /// silently drop a tax from the composition.
    ///
    /// Publishing a set with the same `EffectiveFrom` as an existing set for
    /// the same owner throws (`rate_component_sets_list_day_uk` or
    /// `rate_component_sets_org_default_day_uk`, SqlState `23505`) rather
    /// than leaving two candidates for "the effective set on that date".
    ///
    /// The returned set is rebuilt from the CALLER'S components, not re-read
    /// from the rows just written, and that is safe only because
    /// <see cref="RateComponent"/> refuses any percentage the
    /// `numeric(9,4)` column would round (see
    /// <see cref="RateComponent.MaxDecimalPlaces"/>). Rejecting at the edge was
    /// chosen over re-reading: a re-read would have kept the silent rounding
    /// and merely reported it, so a publisher asking for 10.50005% would be
    /// handed 10.5001% with nothing marking the change. The alternative to
    /// widening that edge is widening the column, never loosening this.
    /// `RateComponentStoreTests.PublishSetAsync_ReturnedSet_ComposesExactlyWhatEveryLaterReadComposes`
    /// pins the two together against the live schema.
    /// </summary>
    public async Task<RateComponentSet> PublishSetAsync(
        CloudTenantScope scope, NewRateComponentSet set, string actorKind, Guid actorId, CancellationToken ct)
    {
        await using var connection = await _dataSource.OpenConnectionAsync(ct);
        await using var tx = await connection.BeginTransactionAsync(ct);

        await SetTenantScopeAsync(connection, tx, scope, ct);

        await using (var cmd = new NpgsqlCommand(
            """
            INSERT INTO rate_component_sets (id, organization_id, price_list_id, effective_from, created_by_user_id)
            VALUES ($1, $2, $3, $4, $5)
            """, connection, tx))
        {
            cmd.Parameters.AddWithValue(set.Id);
            cmd.Parameters.AddWithValue(scope.OrganizationId);
            cmd.Parameters.AddWithValue((object?)set.PriceListId ?? DBNull.Value);
            cmd.Parameters.AddWithValue(set.EffectiveFrom.ToDateTime(TimeOnly.MinValue));
            cmd.Parameters.AddWithValue(set.CreatedByUserId);
            await cmd.ExecuteNonQueryAsync(ct);
        }

        foreach (var component in set.Components)
        {
            await using var cmd = new NpgsqlCommand(
                """
                INSERT INTO rate_components
                    (id, organization_id, set_id, code, label, percentage, calculation_base, component_order)
                VALUES ($1, $2, $3, $4, $5, $6, $7, $8)
                """, connection, tx);
            cmd.Parameters.AddWithValue(Guid.NewGuid());
            cmd.Parameters.AddWithValue(scope.OrganizationId);
            cmd.Parameters.AddWithValue(set.Id);
            cmd.Parameters.AddWithValue(component.Code);
            cmd.Parameters.AddWithValue(component.Label);
            cmd.Parameters.AddWithValue(component.Percentage);
            // The enum NAME, not its numeric value: the column's CHECK lists
            // 'Base'/'Subtotal', so a renamed enum member fails loudly at the
            // database instead of silently persisting a different base.
            cmd.Parameters.AddWithValue(component.CalculationBase.ToString());
            cmd.Parameters.AddWithValue(component.Order);
            await cmd.ExecuteNonQueryAsync(ct);
        }

        await AuditLogWriter.InsertAsync(
            connection, tx,
            new UserManagementAuditEntry(
                actorKind, actorId, scope.OrganizationId, "rate_component_set", set.Id, "rate-component-set.published",
                OldValueJson: null,
                NewValueJson: $$"""{"effectiveFrom":"{{set.EffectiveFrom:yyyy-MM-dd}}","componentCount":{{set.Components.Count}}}"""),
            ct);

        await tx.CommitAsync(ct);

        return Rebuild(set.Id, scope.OrganizationId, set.PriceListId, set.EffectiveFrom, set.Components);
    }

    /// <summary>
    /// The resolution query (spec "Organization Default Rate Components"):
    /// the price list's own latest set effective on or before
    /// <paramref name="effectiveOn"/>, or — only when the list has NO set of
    /// its own at that date — the organization's default set, or `null` for
    /// exactly zero sets anywhere.
    ///
    /// Inheritance is all-or-nothing AT THE SET LEVEL and is deliberately not
    /// a per-component merge: a list that declares its own set uses it in
    /// full, so an empty declared set means an empty composition rather than
    /// a silent fallback to the organization's freight.
    ///
    /// `null` is "no components effective for this date", which the caller
    /// composes as the identity — it is never an error and never a zero.
    /// </summary>
    public async Task<RateComponentSet?> GetEffectiveSetAsync(
        CloudTenantScope scope, Guid priceListId, DateOnly effectiveOn, CancellationToken ct)
    {
        await using var connection = await _dataSource.OpenConnectionAsync(ct);
        await using var tx = await connection.BeginTransactionAsync(ct);

        await SetTenantScopeAsync(connection, tx, scope, ct);

        // `organization_id = $1` is redundant with RLS and kept deliberately:
        // the inheritance arm reads a row that is NOT keyed by the price list,
        // so it is the one query whose tenant scoping would otherwise rest on
        // the policy alone.
        //
        // Each arm binds its OWN parameters, in its own order. The two used to
        // share a fixed `(priceListId, effectiveOn, organizationId)` signature,
        // which forced the organization-default arm to bind a `priceListId` its
        // SQL never mentioned — a dead binding, and a positional coupling that
        // would silently mis-bind the day either query gained a placeholder.
        var header = await ReadSetHeaderAsync(
            connection, tx,
            $"""
            SELECT {SetColumns} FROM rate_component_sets
            WHERE organization_id = $1 AND price_list_id = $2 AND effective_from <= $3
            ORDER BY effective_from DESC
            LIMIT 1
            """,
            ct, scope.OrganizationId, priceListId, effectiveOn.ToDateTime(TimeOnly.MinValue));

        header ??= await ReadSetHeaderAsync(
            connection, tx,
            $"""
            SELECT {SetColumns} FROM rate_component_sets
            WHERE organization_id = $1 AND price_list_id IS NULL AND effective_from <= $2
            ORDER BY effective_from DESC
            LIMIT 1
            """,
            ct, scope.OrganizationId, effectiveOn.ToDateTime(TimeOnly.MinValue));

        RateComponentSet? result = null;
        if (header is not null)
        {
            var components = await ReadComponentsAsync(connection, tx, header.Value.Id, ct);
            result = Rebuild(header.Value.Id, header.Value.OrganizationId, header.Value.PriceListId, header.Value.EffectiveFrom, components);
        }

        await tx.CommitAsync(ct);
        return result;
    }

    /// <summary>
    /// Every set ever published for one owner, newest first — the admin
    /// history view, and the proof that a rate change added a set rather than
    /// rewriting one. Pass `null` for <paramref name="priceListId"/> to read
    /// the organization's default sets; a list's history deliberately does
    /// NOT include the organization defaults it may inherit, because those
    /// are a different owner's history.
    /// </summary>
    public async Task<IReadOnlyList<RateComponentSet>> ListHistoryAsync(
        CloudTenantScope scope, Guid? priceListId, CancellationToken ct)
    {
        await using var connection = await _dataSource.OpenConnectionAsync(ct);
        await using var tx = await connection.BeginTransactionAsync(ct);

        await SetTenantScopeAsync(connection, tx, scope, ct);

        var headers = new List<(Guid Id, Guid OrganizationId, Guid? PriceListId, DateOnly EffectiveFrom)>();
        var ownerFilter = priceListId is null ? "price_list_id IS NULL" : "price_list_id = $2";
        await using (var cmd = new NpgsqlCommand(
            $"""
            SELECT {SetColumns} FROM rate_component_sets
            WHERE organization_id = $1 AND {ownerFilter}
            ORDER BY effective_from DESC
            """, connection, tx))
        {
            cmd.Parameters.AddWithValue(scope.OrganizationId);
            if (priceListId is not null)
            {
                cmd.Parameters.AddWithValue(priceListId.Value);
            }

            await using var reader = await cmd.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct))
            {
                headers.Add(ReadHeader(reader));
            }
        }

        var results = new List<RateComponentSet>(headers.Count);
        foreach (var header in headers)
        {
            var components = await ReadComponentsAsync(connection, tx, header.Id, ct);
            results.Add(Rebuild(header.Id, header.OrganizationId, header.PriceListId, header.EffectiveFrom, components));
        }

        await tx.CommitAsync(ct);
        return results;
    }

    /// <summary>
    /// Runs one header query and binds exactly the parameters it was given,
    /// positionally. The caller owns the placeholder order, so neither arm of
    /// <see cref="GetEffectiveSetAsync"/> has to carry a value the other one
    /// needs.
    /// </summary>
    private static async Task<(Guid Id, Guid OrganizationId, Guid? PriceListId, DateOnly EffectiveFrom)?> ReadSetHeaderAsync(
        NpgsqlConnection connection, NpgsqlTransaction tx, string sql,
        CancellationToken ct, params object[] parameters)
    {
        await using var cmd = new NpgsqlCommand(sql, connection, tx);
        foreach (var parameter in parameters)
        {
            cmd.Parameters.AddWithValue(parameter);
        }

        await using var reader = await cmd.ExecuteReaderAsync(ct);
        return await reader.ReadAsync(ct) ? ReadHeader(reader) : null;
    }

    private static (Guid Id, Guid OrganizationId, Guid? PriceListId, DateOnly EffectiveFrom) ReadHeader(NpgsqlDataReader reader) => (
        reader.GetGuid(0),
        reader.GetGuid(1),
        reader.IsDBNull(2) ? null : reader.GetGuid(2),
        DateOnly.FromDateTime(reader.GetDateTime(3)));

    private static async Task<IReadOnlyList<RateComponent>> ReadComponentsAsync(
        NpgsqlConnection connection, NpgsqlTransaction tx, Guid setId, CancellationToken ct)
    {
        var components = new List<RateComponent>();
        await using var cmd = new NpgsqlCommand(
            $"SELECT {ComponentColumns} FROM rate_components WHERE set_id = $1 ORDER BY component_order", connection, tx);
        cmd.Parameters.AddWithValue(setId);

        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            components.Add(new RateComponent(
                reader.GetString(0),
                reader.GetString(1),
                reader.GetDecimal(2),
                Enum.Parse<RateCalculationBase>(reader.GetString(3)),
                reader.GetInt32(4)));
        }

        return components;
    }

    /// <summary>
    /// Rebuilds the aggregate through its real factories, so a read re-runs
    /// every construction invariant instead of trusting the table.
    /// </summary>
    private static RateComponentSet Rebuild(
        Guid id, Guid organizationId, Guid? priceListId, DateOnly effectiveFrom, IReadOnlyList<RateComponent> components) =>
        priceListId is null
            ? RateComponentSet.ForOrganizationDefault(id, organizationId, effectiveFrom, components)
            : RateComponentSet.ForPriceList(id, organizationId, priceListId.Value, effectiveFrom, components);
}
