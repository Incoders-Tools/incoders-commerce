using Commerce.BranchNode;

namespace Commerce.Integration;

/// <summary>
/// Covers Unit 6 task 6.1 (commerce-customer-identity design.md "BranchNode
/// cloud->local customer replication"): `customers_replica` upsert is
/// idempotent, `RemoveCustomers` deletes, the sync cursor advances only on a
/// successful transaction, and a failed/interrupted apply leaves both the
/// replica and the cursor untouched. Mirrors `SyncTests`' exact pattern
/// (temp SQLite file, `SimulateInterruptedCommit`'s "abandon the transaction"
/// idiom) — deviation note: placed in `tests/Commerce.Integration`, the same
/// path Units 2/3 already used, because `tests/Commerce.BranchNode` does not
/// exist as a project in this solution.
/// </summary>
public sealed class CustomerReplicaTests : IDisposable
{
    private readonly string _dbPath = Path.Combine(Path.GetTempPath(), $"branch-customers-{Guid.NewGuid():N}.db");

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
                    // Best-effort cleanup; the OS temp directory is periodically reclaimed.
                }
            }
        }
    }

    private static CustomerReplica SampleCustomer(
        Guid customerId,
        Guid organizationId,
        string displayName = "Acme Butchery",
        DateTimeOffset? updatedAtUtc = null) => new(
            CustomerId: customerId,
            OrganizationId: organizationId,
            DisplayName: displayName,
            CustomerKind: "Retail",
            TaxId: null,
            Phone: "555-1234",
            Locality: "CABA",
            UpdatedAtUtc: updatedAtUtc ?? DateTimeOffset.UtcNow);

    [Fact]
    public void UpsertCustomers_IsIdempotent()
    {
        using var store = new BranchSyncStore(ConnectionString);
        var customerId = Guid.NewGuid();
        var organizationId = Guid.NewGuid();
        var customer = SampleCustomer(customerId, organizationId);

        store.UpsertCustomers([customer]);
        store.UpsertCustomers([customer]);

        var replica = store.ListCustomers();
        var entry = Assert.Single(replica);
        Assert.Equal(customerId, entry.CustomerId);
    }

    [Fact]
    public void UpsertCustomers_UpdatesExistingRowRatherThanDuplicating()
    {
        using var store = new BranchSyncStore(ConnectionString);
        var customerId = Guid.NewGuid();
        var organizationId = Guid.NewGuid();

        store.UpsertCustomers([SampleCustomer(customerId, organizationId, "Original Name")]);
        store.UpsertCustomers([SampleCustomer(customerId, organizationId, "Renamed")]);

        var entry = Assert.Single(store.ListCustomers());
        Assert.Equal("Renamed", entry.DisplayName);
    }

    [Fact]
    public void RemoveCustomers_DeletesReplicaRow()
    {
        using var store = new BranchSyncStore(ConnectionString);
        var customerId = Guid.NewGuid();
        store.UpsertCustomers([SampleCustomer(customerId, Guid.NewGuid())]);

        store.RemoveCustomers([customerId]);

        Assert.Empty(store.ListCustomers());
    }

    [Fact]
    public void CustomersCursor_IsNullUntilFirstSuccessfulApply()
    {
        using var store = new BranchSyncStore(ConnectionString);

        Assert.Null(store.GetCustomersCursor());
    }

    [Fact]
    public void ApplyCustomerSync_AdvancesCursorAndAppliesUpsertsAndRemovalsAtomically()
    {
        using var store = new BranchSyncStore(ConnectionString);
        var organizationId = Guid.NewGuid();
        var disabledId = Guid.NewGuid();
        store.UpsertCustomers([SampleCustomer(disabledId, organizationId)]);

        var keepId = Guid.NewGuid();
        var serverTime = DateTimeOffset.UtcNow;

        store.ApplyCustomerSync(
            customers: [SampleCustomer(keepId, organizationId)],
            disabledIds: [disabledId],
            serverTimeUtc: serverTime);

        var replica = store.ListCustomers();
        var entry = Assert.Single(replica);
        Assert.Equal(keepId, entry.CustomerId);
        Assert.Equal(serverTime, store.GetCustomersCursor());
    }

    [Fact]
    public void InterruptedCustomerSync_NeverPersistsPartialReplicaOrCursorAcrossRestart()
    {
        var organizationId = Guid.NewGuid();
        var customerId = Guid.NewGuid();
        var serverTime = DateTimeOffset.UtcNow;

        using (var store = new BranchSyncStore(ConnectionString))
        {
            store.SimulateInterruptedCustomerSync(
                customers: [SampleCustomer(customerId, organizationId)],
                disabledIds: [],
                serverTimeUtc: serverTime);
        }

        // Branch restarts: reopen the same SQLite file as a fresh store.
        using var restarted = new BranchSyncStore(ConnectionString);
        Assert.Empty(restarted.ListCustomers());
        Assert.Null(restarted.GetCustomersCursor());
    }
}
