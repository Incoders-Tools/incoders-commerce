using Commerce.Cloud.Api.Auditing;
using Commerce.Cloud.Api.Tenancy;
using Commerce.Domain.Catalog;
using Npgsql;

namespace Commerce.Cloud.Api.Persistence;

/// <summary>
/// B7 U5b (catalog-item-identification spec "Copying Catalog Between
/// Branches"): copies the whole catalog, or a chosen list of products, from
/// one branch of an organization into ANOTHER branch of the SAME
/// organization, plus the source branch's latest uploaded price list for the
/// copied presentations. New, independent rows only — never an update/merge
/// of anything already in the target branch, so a later edit in either
/// branch never affects the other.
///
/// Deliberately its OWN store (not a method added to
/// <see cref="PostgresCatalogStore"/> or <see cref="PostgresPriceListStore"/>):
/// the whole operation — reading the source branch's products/presentations/
/// price list, then writing new rows into the target branch across BOTH the
/// catalog and pricing tables — MUST be one atomic transaction (the whole
/// copy succeeds or nothing is written), and every other store in this
/// codebase opens its own connection/transaction per call, so a cross-store
/// atomic write has nowhere to live except a store of its own.
///
/// RLS is fail-closed per branch (0016/0017: `organization_id AND branch_id`
/// must match the transaction's GUCs). A single connection/transaction can
/// only ever have ONE branch selected at a time, so this store reads under
/// the SOURCE branch's GUC, then re-applies <see cref="TenantScopeSql"/>
/// under the TARGET branch's GUC (`set_config(..., true)` is `SET LOCAL`,
/// scoped to the transaction, so switching it mid-transaction is safe and
/// affects only statements issued after the switch) before writing anything
/// — it never disables RLS and never runs as the owner role.
/// </summary>
public sealed class CatalogCopyStore
{
    private readonly NpgsqlDataSource _dataSource;

    public CatalogCopyStore(NpgsqlDataSource dataSource) => _dataSource = dataSource;

    /// <summary>
    /// Copies <paramref name="productIds"/> (or, when null/empty, every
    /// product) from <paramref name="sourceBranchId"/> into
    /// <paramref name="targetBranchId"/> of the SAME organization
    /// (<paramref name="scope"/>.OrganizationId — the endpoint has already
    /// confirmed both branches belong to it and are within the caller's
    /// scope). Throws <see cref="CatalogCopyProductNotFoundException"/>
    /// (whole transaction rolled back, nothing written) when a requested id
    /// does not resolve to a product actually visible in the source branch.
    /// </summary>
    public async Task<CatalogCopyOutcome> CopyCatalogAsync(
        CloudTenantScope scope,
        Guid sourceBranchId,
        Guid targetBranchId,
        IReadOnlyList<Guid>? productIds,
        string actorKind,
        Guid actorId,
        CancellationToken ct)
    {
        var sourceScope = scope with { BranchId = sourceBranchId };
        var targetScope = scope with { BranchId = targetBranchId };

        await using var connection = await _dataSource.OpenConnectionAsync(ct);
        await using var tx = await connection.BeginTransactionAsync(ct);

        // --- Read phase: SOURCE branch GUC ---------------------------------
        await TenantScopeSql.ApplyAsync(connection, tx, sourceScope, ct);

        var sourceProducts = await LoadProductsAsync(connection, tx, productIds, ct);
        var sourceProductsById = sourceProducts.ToDictionary(p => p.Id);

        var presentationsByProduct = await LoadPresentationsByProductAsync(
            connection, tx, sourceProducts.Select(p => p.Id).ToList(), ct);

        var latestPriceList = await LoadLatestPriceListAsync(connection, tx, ct);
        IReadOnlyList<SourcePriceEntryRow> latestEntries = latestPriceList is { } sourcePriceList
            ? await LoadEntriesForPriceListAsync(connection, tx, sourcePriceList.Id, ct)
            : [];

        // --- Write phase: TARGET branch GUC ---------------------------------
        await TenantScopeSql.ApplyAsync(connection, tx, targetScope, ct);

        var existingTargetCodes = await LoadExistingCodesAsync(connection, tx, ct);
        var targetHasDefaultPriceList = await HasDefaultPriceListAsync(connection, tx, ct);

        var skipped = new List<SkippedPresentation>();
        var presentationIdMap = new Dictionary<Guid, Guid>();
        var productsCopied = 0;
        var presentationsCopied = 0;

        // A caller-submitted id that does not resolve to a product actually
        // visible in the source branch (deleted since the caller listed it,
        // wrong branch, wrong organization, or never existed) fails the
        // WHOLE copy — checked incrementally here, per requested id, in the
        // caller's own order, rather than as a separate pre-pass: a product
        // requested earlier in the list may already have new rows staged
        // (uncommitted) in THIS transaction by the time a later id is found
        // missing. The transaction is never committed, so those staged rows
        // never become visible to anyone (atomicity holds either way; this
        // ordering is what B7 U5b's integration test exercises directly).
        var orderedProductIds = productIds is { Count: > 0 } ? productIds : sourceProducts.Select(p => p.Id).ToList();

        foreach (var productId in orderedProductIds)
        {
            if (!sourceProductsById.TryGetValue(productId, out var product))
            {
                throw new CatalogCopyProductNotFoundException(productId);
            }

            var sourcePresentations = presentationsByProduct.TryGetValue(product.Id, out var list) ? list : [];
            var toCopy = new List<SourcePresentationRow>();

            foreach (var presentation in sourcePresentations)
            {
                if (presentation.IdentificationCode is { } code && existingTargetCodes.Contains(code))
                {
                    skipped.Add(new SkippedPresentation(presentation.Id, presentation.IdentificationCode, "identification-code-in-target"));
                    continue;
                }

                toCopy.Add(presentation);
            }

            // "A product whose presentations were all skipped is not
            // created" — but a product with NO presentations at all (an
            // empty shell) is still copied, since nothing about it was
            // skipped.
            if (sourcePresentations.Count > 0 && toCopy.Count == 0)
            {
                continue;
            }

            var newProductId = Guid.NewGuid();
            await InsertProductAsync(connection, tx, targetScope, newProductId, product, actorId, ct);
            productsCopied++;

            foreach (var presentation in toCopy)
            {
                var newPresentationId = Guid.NewGuid();
                await InsertPresentationAsync(connection, tx, targetScope, newPresentationId, newProductId, presentation, actorId, ct);
                presentationIdMap[presentation.Id] = newPresentationId;
                if (presentation.IdentificationCode is not null)
                {
                    existingTargetCodes.Add(presentation.IdentificationCode);
                }

                presentationsCopied++;
            }
        }

        Guid? newPriceListId = null;
        var priceEntriesCopied = 0;

        if (latestPriceList is { } sourceList && presentationIdMap.Count > 0)
        {
            var relevantEntries = latestEntries.Where(e => presentationIdMap.ContainsKey(e.PresentationId)).ToList();
            if (relevantEntries.Count > 0)
            {
                newPriceListId = Guid.NewGuid();
                await InsertPriceListAsync(
                    connection, tx, targetScope, newPriceListId.Value, sourceList.Name,
                    isDefault: !targetHasDefaultPriceList, actorId, ct);

                foreach (var entry in relevantEntries)
                {
                    await InsertPriceEntryAsync(
                        connection, tx, targetScope, Guid.NewGuid(), newPriceListId.Value,
                        presentationIdMap[entry.PresentationId], entry.UnitPrice, entry.EffectiveFrom, actorId, ct);
                    priceEntriesCopied++;
                }
            }
        }

        var skippedJson = string.Join(",", skipped.Select(s =>
            $$"""{"presentationId":"{{s.PresentationId}}","reason":"{{s.Reason}}"}"""));

        await AuditLogWriter.InsertAsync(
            connection, tx,
            new UserManagementAuditEntry(
                actorKind, actorId, scope.OrganizationId, "catalog", targetBranchId, "catalog.copied",
                OldValueJson: null,
                NewValueJson:
                    $$"""
                    {"sourceBranchId":"{{sourceBranchId}}","targetBranchId":"{{targetBranchId}}","productsCopied":{{productsCopied}},"presentationsCopied":{{presentationsCopied}},"skippedCount":{{skipped.Count}},"skipped":[{{skippedJson}}]}
                    """),
            ct);

        await tx.CommitAsync(ct);

        return new CatalogCopyOutcome(productsCopied, presentationsCopied, skipped, newPriceListId, priceEntriesCopied);
    }

    // --- Reads (source branch GUC already applied by the caller) ----------

    private static async Task<IReadOnlyList<SourceProductRow>> LoadProductsAsync(
        NpgsqlConnection connection, NpgsqlTransaction tx, IReadOnlyList<Guid>? productIds, CancellationToken ct)
    {
        var results = new List<SourceProductRow>();
        var sql = productIds is { Count: > 0 }
            ? "SELECT id, name, category_id, default_unit_id FROM products WHERE id = ANY($1)"
            : "SELECT id, name, category_id, default_unit_id FROM products";

        await using var cmd = new NpgsqlCommand(sql, connection, tx);
        if (productIds is { Count: > 0 })
        {
            cmd.Parameters.AddWithValue(productIds.ToArray());
        }

        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            results.Add(new SourceProductRow(reader.GetGuid(0), reader.GetString(1), reader.GetGuid(2), reader.GetGuid(3)));
        }

        return results;
    }

    private static async Task<Dictionary<Guid, List<SourcePresentationRow>>> LoadPresentationsByProductAsync(
        NpgsqlConnection connection, NpgsqlTransaction tx, IReadOnlyList<Guid> productIds, CancellationToken ct)
    {
        var byProduct = new Dictionary<Guid, List<SourcePresentationRow>>();
        if (productIds.Count == 0)
        {
            return byProduct;
        }

        await using var cmd = new NpgsqlCommand(
            """
            SELECT id, product_id, name, quantity_behavior, unit_id, identification_code
            FROM presentations WHERE product_id = ANY($1)
            """, connection, tx);
        cmd.Parameters.AddWithValue(productIds.ToArray());

        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            var row = new SourcePresentationRow(
                reader.GetGuid(0), reader.GetGuid(1), reader.GetString(2),
                Enum.Parse<QuantityBehavior>(reader.GetString(3)), reader.GetGuid(4),
                reader.IsDBNull(5) ? null : reader.GetString(5));

            if (!byProduct.TryGetValue(row.ProductId, out var list))
            {
                list = [];
                byProduct[row.ProductId] = list;
            }

            list.Add(row);
        }

        return byProduct;
    }

    /// <summary>
    /// "The source branch's most recently uploaded price list (the latest
    /// one created or imported)" — an import appends entries into an
    /// EXISTING list, so a list's upload time is the later of its own
    /// `created_at_utc` and the newest `source = 'Import'` entry it received.
    /// Ties (same instant) fall back to `id` purely for determinism.
    /// </summary>
    private static async Task<(Guid Id, string Name)?> LoadLatestPriceListAsync(
        NpgsqlConnection connection, NpgsqlTransaction tx, CancellationToken ct)
    {
        await using var cmd = new NpgsqlCommand(
            """
            SELECT pl.id, pl.name
            FROM price_lists pl
            ORDER BY GREATEST(
                         pl.created_at_utc,
                         COALESCE((SELECT max(e.created_at_utc) FROM price_list_entries e
                                   WHERE e.price_list_id = pl.id AND e.source = 'Import'), pl.created_at_utc)) DESC,
                     pl.id DESC
            LIMIT 1
            """, connection, tx);
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        return await reader.ReadAsync(ct) ? (reader.GetGuid(0), reader.GetString(1)) : null;
    }

    /// <summary>Every entry ever published for this list — full history, not only the currently-effective one.</summary>
    private static async Task<IReadOnlyList<SourcePriceEntryRow>> LoadEntriesForPriceListAsync(
        NpgsqlConnection connection, NpgsqlTransaction tx, Guid priceListId, CancellationToken ct)
    {
        var results = new List<SourcePriceEntryRow>();
        await using var cmd = new NpgsqlCommand(
            "SELECT presentation_id, unit_price, effective_from FROM price_list_entries WHERE price_list_id = $1",
            connection, tx);
        cmd.Parameters.AddWithValue(priceListId);

        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            results.Add(new SourcePriceEntryRow(reader.GetGuid(0), reader.GetDecimal(1), DateOnly.FromDateTime(reader.GetDateTime(2))));
        }

        return results;
    }

    // --- Reads/writes (target branch GUC already applied by the caller) ---

    private static async Task<HashSet<string>> LoadExistingCodesAsync(NpgsqlConnection connection, NpgsqlTransaction tx, CancellationToken ct)
    {
        var codes = new HashSet<string>();
        await using var cmd = new NpgsqlCommand(
            "SELECT identification_code FROM presentations WHERE identification_code IS NOT NULL", connection, tx);
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            codes.Add(reader.GetString(0));
        }

        return codes;
    }

    private static async Task<bool> HasDefaultPriceListAsync(NpgsqlConnection connection, NpgsqlTransaction tx, CancellationToken ct)
    {
        await using var cmd = new NpgsqlCommand("SELECT EXISTS(SELECT 1 FROM price_lists WHERE is_default)", connection, tx);
        return (bool)(await cmd.ExecuteScalarAsync(ct))!;
    }

    private static async Task InsertProductAsync(
        NpgsqlConnection connection, NpgsqlTransaction tx, CloudTenantScope targetScope,
        Guid newProductId, SourceProductRow source, Guid actorId, CancellationToken ct)
    {
        await using var cmd = new NpgsqlCommand(
            """
            INSERT INTO products (id, organization_id, branch_id, name, category_id, default_unit_id, created_by_user_id)
            VALUES ($1, $2, $3, $4, $5, $6, $7)
            """, connection, tx);
        cmd.Parameters.AddWithValue(newProductId);
        cmd.Parameters.AddWithValue(targetScope.OrganizationId);
        cmd.Parameters.AddWithValue(targetScope.BranchId!.Value);
        cmd.Parameters.AddWithValue(source.Name);
        cmd.Parameters.AddWithValue(source.CategoryId);
        cmd.Parameters.AddWithValue(source.DefaultUnitId);
        cmd.Parameters.AddWithValue(actorId);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    private static async Task InsertPresentationAsync(
        NpgsqlConnection connection, NpgsqlTransaction tx, CloudTenantScope targetScope,
        Guid newPresentationId, Guid newProductId, SourcePresentationRow source, Guid actorId, CancellationToken ct)
    {
        await using var cmd = new NpgsqlCommand(
            """
            INSERT INTO presentations
                (id, organization_id, branch_id, product_id, name, quantity_behavior, unit_id, identification_code, created_by_user_id)
            VALUES ($1, $2, $3, $4, $5, $6, $7, $8, $9)
            """, connection, tx);
        cmd.Parameters.AddWithValue(newPresentationId);
        cmd.Parameters.AddWithValue(targetScope.OrganizationId);
        cmd.Parameters.AddWithValue(targetScope.BranchId!.Value);
        cmd.Parameters.AddWithValue(newProductId);
        cmd.Parameters.AddWithValue(source.Name);
        cmd.Parameters.AddWithValue(source.QuantityBehavior.ToString());
        cmd.Parameters.AddWithValue(source.UnitId);
        cmd.Parameters.AddWithValue((object?)source.IdentificationCode ?? DBNull.Value);
        cmd.Parameters.AddWithValue(actorId);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    private static async Task InsertPriceListAsync(
        NpgsqlConnection connection, NpgsqlTransaction tx, CloudTenantScope targetScope,
        Guid newPriceListId, string name, bool isDefault, Guid actorId, CancellationToken ct)
    {
        await using var cmd = new NpgsqlCommand(
            """
            INSERT INTO price_lists (id, organization_id, branch_id, name, is_default, created_by_user_id)
            VALUES ($1, $2, $3, $4, $5, $6)
            """, connection, tx);
        cmd.Parameters.AddWithValue(newPriceListId);
        cmd.Parameters.AddWithValue(targetScope.OrganizationId);
        cmd.Parameters.AddWithValue(targetScope.BranchId!.Value);
        cmd.Parameters.AddWithValue(name);
        cmd.Parameters.AddWithValue(isDefault);
        cmd.Parameters.AddWithValue(actorId);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    /// <summary>
    /// `source = 'Manual'` — the `price_list_entries.source` CHECK
    /// constraint (0009) only allows `'Manual' | 'Import'`; a copied entry
    /// went through neither the manual publish form nor the supplier import
    /// pipeline, but of the two it is closer to "Manual" (an administrative
    /// action, not a supplier file), so no new CHECK value was added for it.
    /// `import_batch_id` stays null.
    /// </summary>
    private static async Task InsertPriceEntryAsync(
        NpgsqlConnection connection, NpgsqlTransaction tx, CloudTenantScope targetScope,
        Guid newEntryId, Guid priceListId, Guid presentationId, decimal unitPrice, DateOnly effectiveFrom,
        Guid actorId, CancellationToken ct)
    {
        await using var cmd = new NpgsqlCommand(
            """
            INSERT INTO price_list_entries
                (id, organization_id, branch_id, price_list_id, presentation_id, unit_price, effective_from, source, import_batch_id, created_by_user_id)
            VALUES ($1, $2, $3, $4, $5, $6, $7, 'Manual', NULL, $8)
            """, connection, tx);
        cmd.Parameters.AddWithValue(newEntryId);
        cmd.Parameters.AddWithValue(targetScope.OrganizationId);
        cmd.Parameters.AddWithValue(targetScope.BranchId!.Value);
        cmd.Parameters.AddWithValue(priceListId);
        cmd.Parameters.AddWithValue(presentationId);
        cmd.Parameters.AddWithValue(unitPrice);
        cmd.Parameters.AddWithValue(effectiveFrom.ToDateTime(TimeOnly.MinValue));
        cmd.Parameters.AddWithValue(actorId);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    private readonly record struct SourceProductRow(Guid Id, string Name, Guid CategoryId, Guid DefaultUnitId);

    private readonly record struct SourcePresentationRow(
        Guid Id, Guid ProductId, string Name, QuantityBehavior QuantityBehavior, Guid UnitId, string? IdentificationCode);

    private readonly record struct SourcePriceEntryRow(Guid PresentationId, decimal UnitPrice, DateOnly EffectiveFrom);
}

/// <summary>One presentation NOT copied, and why — reported to the caller, never silently dropped.</summary>
public sealed record SkippedPresentation(Guid PresentationId, string? IdentificationCode, string Reason);

/// <summary>Full result of one <see cref="CatalogCopyStore.CopyCatalogAsync"/> call.</summary>
public sealed record CatalogCopyOutcome(
    int ProductsCopied,
    int PresentationsCopied,
    IReadOnlyList<SkippedPresentation> Skipped,
    Guid? PriceListId,
    int PriceEntriesCopied);

/// <summary>
/// Thrown when a caller-submitted product id does not resolve to a product
/// visible in the source branch (wrong branch, wrong organization, or never
/// existed) — the endpoint translates this into a 404. Thrown from inside
/// the copy transaction, before any row is written for that request, so an
/// unhandled instance still leaves the whole copy atomic: the transaction is
/// never committed and rolls back on disposal.
/// </summary>
public sealed class CatalogCopyProductNotFoundException(Guid productId)
    : Exception($"Product {productId} was not found in the source branch.")
{
    public Guid ProductId { get; } = productId;
}
