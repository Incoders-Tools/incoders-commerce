using Commerce.Cloud.Api.Tenancy;
using Npgsql;

namespace Commerce.Cloud.Api.Persistence;

/// <summary>A customer as the staff order screen lists it (staff-order-taking T2).</summary>
public sealed record StaffOrderCustomer(
    Guid Id, string DisplayName, string? TaxId, string? Phone, string? CityName, Guid? PriceListId, string? PriceListName,
    decimal? DiscountPercentage, bool IsEnabled);

/// <summary>An orderable presentation as the staff order screen lists it (staff-order-taking T2).</summary>
public sealed record StaffOrderPresentation(
    Guid PresentationId, Guid ProductId, string ProductName, string PresentationName, string? IdentificationCode,
    string QuantityBehavior);

/// <summary>
/// The two read-only lookups of the staff order screen (staff-order-taking T2). The customer registry and the catalog
/// reads require <c>ManageUsers</c> / <c>ManageCatalog</c>, which a seller does not hold, so this store exposes only the
/// fields needed to pick a customer and a product, behind <c>TakeOrders</c>. Same shape as the other stores: one
/// transaction per call, tenant scope applied first, so RLS limits both reads to the organization (and the
/// presentations to the selected branch's catalog). Both are bounded to <see cref="MaxResults"/> rows.
/// </summary>
public sealed class PostgresStaffOrderLookupStore
{
    public const int MaxResults = 50;

    private readonly NpgsqlDataSource _dataSource;

    public PostgresStaffOrderLookupStore(NpgsqlDataSource dataSource) => _dataSource = dataSource;

    /// <summary>
    /// Ordered by display name. <paramref name="search"/> matches the name, legal name, tax id and phone ignoring case,
    /// accents and (for a number) punctuation, through the same <see cref="CustomerSearchTerm"/> the registry uses.
    /// Disabled customers are listed with <c>IsEnabled = false</c> so the screen can say why they cannot order.
    /// </summary>
    public async Task<IReadOnlyList<StaffOrderCustomer>> SearchCustomersAsync(CloudTenantScope scope, string? search, CancellationToken ct)
    {
        var term = CustomerSearchTerm.Normalize(search);
        await using var connection = await _dataSource.OpenConnectionAsync(ct);
        await using var tx = await connection.BeginTransactionAsync(ct);
        await TenantScopeSql.ApplyAsync(connection, tx, scope, ct);

        var results = new List<StaffOrderCustomer>();
        await using (var cmd = new NpgsqlCommand(
            """
            SELECT c.id, c.display_name, c.tax_id, c.phone, ci.name, c.price_list_id, pl.name, c.discount_percentage, c.is_enabled
            FROM customers c
                LEFT JOIN cities ci ON ci.id = c.city_id
                LEFT JOIN price_lists pl ON pl.id = c.price_list_id
            WHERE $1::text IS NULL
               OR translate(lower(c.display_name || ' ' || coalesce(c.legal_name, '') || ' ' || coalesce(c.tax_id, '')),
                      'áéíóúüñàèìòùâêîôûäëïöç', 'aeiouunaeiouaeiouaeioc') LIKE '%' || $1 || '%' ESCAPE '\'
               OR regexp_replace(coalesce(c.phone, ''), '[^0-9]', '', 'g') LIKE '%' || $1 || '%' ESCAPE '\'
            ORDER BY c.display_name, c.id
            LIMIT $2
            """, connection, tx))
        {
            cmd.Parameters.AddWithValue((object?)term ?? DBNull.Value);
            cmd.Parameters.AddWithValue(MaxResults);
            await using var reader = await cmd.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct))
            {
                results.Add(new StaffOrderCustomer(
                    reader.GetGuid(0), reader.GetString(1),
                    reader.IsDBNull(2) ? null : reader.GetString(2),
                    reader.IsDBNull(3) ? null : reader.GetString(3),
                    reader.IsDBNull(4) ? null : reader.GetString(4),
                    reader.IsDBNull(5) ? null : reader.GetGuid(5),
                    reader.IsDBNull(6) ? null : reader.GetString(6),
                    reader.IsDBNull(7) ? null : reader.GetDecimal(7),
                    reader.GetBoolean(8)));
            }
        }

        await tx.CommitAsync(ct);
        return results;
    }

    /// <summary>
    /// Presentations of active products (soft-deleted products, 0036, are left out), ordered by product then
    /// presentation name. <paramref name="search"/> matches the product name, the presentation name and the
    /// identification code ignoring case and accents.
    /// </summary>
    public async Task<IReadOnlyList<StaffOrderPresentation>> SearchPresentationsAsync(CloudTenantScope scope, string? search, CancellationToken ct)
    {
        var term = CustomerSearchTerm.FoldForLike(search);
        await using var connection = await _dataSource.OpenConnectionAsync(ct);
        await using var tx = await connection.BeginTransactionAsync(ct);
        await TenantScopeSql.ApplyAsync(connection, tx, scope, ct);

        var results = new List<StaffOrderPresentation>();
        await using (var cmd = new NpgsqlCommand(
            """
            SELECT p.id, pr.id, pr.name, p.name, p.identification_code, p.quantity_behavior
            FROM presentations p
                JOIN products pr ON pr.id = p.product_id
            WHERE pr.is_active
              AND ($1::text IS NULL
                   OR translate(lower(pr.name || ' ' || p.name || ' ' || coalesce(p.identification_code, '')),
                          'áéíóúüñàèìòùâêîôûäëïöç', 'aeiouunaeiouaeiouaeioc') LIKE '%' || $1 || '%' ESCAPE '\')
            ORDER BY pr.name, p.name, p.id
            LIMIT $2
            """, connection, tx))
        {
            cmd.Parameters.AddWithValue((object?)term ?? DBNull.Value);
            cmd.Parameters.AddWithValue(MaxResults);
            await using var reader = await cmd.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct))
            {
                results.Add(new StaffOrderPresentation(
                    reader.GetGuid(0), reader.GetGuid(1), reader.GetString(2), reader.GetString(3),
                    reader.IsDBNull(4) ? null : reader.GetString(4), reader.GetString(5)));
            }
        }

        await tx.CommitAsync(ct);
        return results;
    }
}
