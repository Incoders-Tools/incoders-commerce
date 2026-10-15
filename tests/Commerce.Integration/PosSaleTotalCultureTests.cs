using System.Globalization;
using Commerce.Application.Access;
using Commerce.Application.Audit;
using Commerce.BranchNode;
using Commerce.Domain.Sales;
using Commerce.Domain.Sync;
using Microsoft.Data.Sqlite;

namespace Commerce.Integration;

/// <summary>
/// owner feedback 2026-10-15, B1: a sale's total is kept in the branch database as invariant text, whatever the Windows
/// culture. It was written with the current culture ("26361,00" under es-AR) and read as invariant, where the comma is
/// a group separator: the day's sales showed $ 2.636.100,00 for a $ 26.361,00 sale, and the cash count and a void read
/// the same wrong total. Totals already stored that way are repaired when the database opens.
/// </summary>
public sealed class PosSaleTotalCultureTests : IDisposable
{
    private readonly string _dbPath = Path.Combine(Path.GetTempPath(), $"branch-sale-culture-{Guid.NewGuid():N}.db");
    private readonly Guid _organizationId = Guid.NewGuid();
    private readonly Guid _branchId = Guid.NewGuid();
    private readonly Guid _operatorId = Guid.NewGuid();
    private readonly CultureInfo _culture = CultureInfo.CurrentCulture;
    private readonly DateTimeOffset _now = new(2026, 10, 15, 11, 19, 0, TimeSpan.Zero);

    public PosSaleTotalCultureTests() => CultureInfo.CurrentCulture = new CultureInfo("es-AR");

    public void Dispose()
    {
        CultureInfo.CurrentCulture = _culture;
        SqliteConnection.ClearAllPools();
        foreach (var path in new[] { _dbPath, _dbPath + "-wal", _dbPath + "-shm" })
        {
            if (File.Exists(path))
            {
                try { File.Delete(path); } catch (IOException) { }
            }
        }
    }

    private BranchNodeService NewService(BranchSyncStore store)
    {
        var sink = new InMemoryAuditSink();
        return new BranchNodeService(store, new TenantAuthorizationService(sink), sink, () => _now);
    }

    private Guid Sell(BranchNodeService service, decimal total, SaleTender tender)
    {
        var id = Guid.NewGuid();
        Assert.True(service.CompleteScannedSale(
            _organizationId, _branchId, _operatorId, id,
            [new SaleLine(id, 1, Guid.NewGuid(), "c1", "Vacío", "Por kg", 1m, total, total)],
            total, Guid.NewGuid(), Guid.NewGuid(), tender: tender).WasNewlyCommitted);
        return id;
    }

    private static (DateTimeOffset, DateTimeOffset) Day => (new(2026, 10, 15, 0, 0, 0, TimeSpan.Zero), new(2026, 10, 16, 0, 0, 0, TimeSpan.Zero));

    [Fact]
    public void ASaleTotal_ReadsTheSame_InTheDaysSales_AndTheCashCount_UnderASpanishWindows()
    {
        using var store = new BranchSyncStore($"Data Source={_dbPath}");
        var service = NewService(store);
        var session = service.OpenCashSession(_organizationId, _branchId, _operatorId, 1000m, Guid.NewGuid()).Session!.SessionId;
        Assert.True(SaleTenderRules.TryCash(26_361m, 30_000m, out var cash));
        var sale = Sell(service, 26_361.00m, cash);

        var (from, to) = Day;
        Assert.Equal(26_361.00m, service.ListSales(from, to).Single(s => s.SaleId == sale).TotalAmount);
        Assert.Equal(27_361m, service.GetCashSessionSummary(session)!.ExpectedCash);
    }

    [Fact]
    public void ATotalStoredWithAComma_IsRepairedWhenTheDatabaseOpens()
    {
        Guid sale;
        using (var store = new BranchSyncStore($"Data Source={_dbPath}"))
        {
            var service = NewService(store);
            service.OpenCashSession(_organizationId, _branchId, _operatorId, 1000m, Guid.NewGuid());
            sale = Sell(service, 26_361.00m, SaleTenderRules.Card());
        }

        // What the earlier code wrote under es-AR.
        SqliteConnection.ClearAllPools();
        using (var raw = new SqliteConnection($"Data Source={_dbPath}"))
        {
            raw.Open();
            using var command = raw.CreateCommand();
            command.CommandText = "UPDATE sale_effects SET total_amount = '26361,00' WHERE sale_id = $id;";
            command.Parameters.AddWithValue("$id", sale.ToString());
            Assert.Equal(1, command.ExecuteNonQuery());
        }

        SqliteConnection.ClearAllPools();
        using var reopened = new BranchSyncStore($"Data Source={_dbPath}");
        var (from, to) = Day;
        Assert.Equal(26_361.00m, NewService(reopened).ListSales(from, to).Single(s => s.SaleId == sale).TotalAmount);
    }
}
