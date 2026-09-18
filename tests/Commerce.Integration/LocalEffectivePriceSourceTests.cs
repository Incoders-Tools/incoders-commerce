using Commerce.BranchNode;
using Commerce.Pos.Windows;

namespace Commerce.Integration;

/// <summary>
/// Covers commerce-pricing-engine Phase 7 task 7.2: the POS half of the
/// shared <c>IEffectivePriceSource</c> port, reading the replicated
/// `price_replica` table instead of Postgres. Same "no zero fallback"
/// contract as <see cref="Commerce.Cloud.Api.Pricing.PostgresEffectivePriceSource"/>:
/// no row, or a row not yet effective on the resolution date, is `null`.
/// </summary>
public sealed class LocalEffectivePriceSourceTests : IDisposable
{
    private readonly string _dbPath = Path.Combine(Path.GetTempPath(), $"branch-local-price-{Guid.NewGuid():N}.db");

    private string ConnectionString => $"Data Source={_dbPath}";

    public void Dispose()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();

        foreach (var path in new[] { _dbPath, _dbPath + "-wal", _dbPath + "-shm" })
        {
            if (File.Exists(path))
            {
                try
                {
                    File.Delete(path);
                }
                catch (IOException)
                {
                }
            }
        }
    }

    private static CatalogPriceReplicaItem SampleItem(Guid presentationId, Guid organizationId, decimal? unitPrice, DateOnly? effectiveFrom) => new(
        PresentationId: presentationId,
        OrganizationId: organizationId,
        ProductId: Guid.NewGuid(),
        ProductName: "Flour",
        PresentationName: "1kg Bag",
        IdentificationCode: "7791234567890",
        QuantityBehavior: "Integral",
        UnitId: Guid.NewGuid(),
        UnitPrice: unitPrice,
        EffectiveFrom: effectiveFrom,
        UpdatedAtUtc: DateTimeOffset.UtcNow);

    [Fact]
    public async Task GetUnitPriceAsync_ReplicatedPrice_ReturnsIt()
    {
        using var store = new BranchSyncStore(ConnectionString);
        var organizationId = Guid.NewGuid();
        var presentationId = Guid.NewGuid();
        var effectiveFrom = new DateOnly(2026, 1, 1);
        store.ApplyCatalogPriceSync([SampleItem(presentationId, organizationId, 249.99m, effectiveFrom)], [], DateTimeOffset.UtcNow);

        var source = new LocalEffectivePriceSource(store);
        var price = await source.GetUnitPriceAsync(presentationId, new DateOnly(2026, 3, 1), CancellationToken.None);

        Assert.Equal(249.99m, price);
    }

    [Fact]
    public async Task GetUnitPriceAsync_UnknownPresentation_ReturnsNull_NeverZero()
    {
        using var store = new BranchSyncStore(ConnectionString);
        var source = new LocalEffectivePriceSource(store);

        var price = await source.GetUnitPriceAsync(Guid.NewGuid(), DateOnly.FromDateTime(DateTime.UtcNow), CancellationToken.None);

        Assert.Null(price);
    }

    [Fact]
    public async Task GetUnitPriceAsync_PriceNotYetEffective_ReturnsNull_NeverZero()
    {
        using var store = new BranchSyncStore(ConnectionString);
        var organizationId = Guid.NewGuid();
        var presentationId = Guid.NewGuid();
        var futureDate = DateOnly.FromDateTime(DateTime.UtcNow).AddYears(1);
        store.ApplyCatalogPriceSync([SampleItem(presentationId, organizationId, 100m, futureDate)], [], DateTimeOffset.UtcNow);

        var source = new LocalEffectivePriceSource(store);
        var price = await source.GetUnitPriceAsync(presentationId, DateOnly.FromDateTime(DateTime.UtcNow), CancellationToken.None);

        Assert.Null(price);
    }

    /// <summary>
    /// Channel-parity proof against the SAME tuple this Phase's design
    /// requires (design.md "the exact same compiled method runs online and
    /// offline"): the shared <see cref="Commerce.Application.Pricing.PricingResolutionService"/>
    /// resolves identically over <see cref="LocalEffectivePriceSource"/> as
    /// it does over Postgres in <c>PricingChannelParityTests</c>.
    /// </summary>
    [Fact]
    public async Task ResolveAsync_OverLocalEffectivePriceSource_AppliesRoundingPolicy()
    {
        using var store = new BranchSyncStore(ConnectionString);
        var organizationId = Guid.NewGuid();
        var presentationId = Guid.NewGuid();
        store.ApplyCatalogPriceSync(
            [SampleItem(presentationId, organizationId, 249.99m, new DateOnly(2026, 1, 1))], [], DateTimeOffset.UtcNow);

        var service = new Commerce.Application.Pricing.PricingResolutionService(new LocalEffectivePriceSource(store));
        var outcome = await service.ResolveAsync(presentationId, 4m, 10m, new DateOnly(2026, 3, 1), CancellationToken.None);

        var resolved = Assert.IsType<Commerce.Application.Pricing.PriceResolutionOutcome.Resolved>(outcome);
        Assert.Equal(224.99m, resolved.UnitNetPrice);
        Assert.Equal(899.96m, resolved.LineTotal);
    }
}
