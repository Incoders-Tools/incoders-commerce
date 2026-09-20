using Commerce.Cloud.Api.Persistence;
using Commerce.Domain.Payments;
using Npgsql;

namespace Commerce.Integration;

/// <summary>
/// Covers Unit 4 task 4.8 (commerce-payments design.md "Interfaces /
/// Contracts"): append-only insert + ordered read scoped by `set_config`
/// tenant context first; a cross-organization read returns no rows.
/// </summary>
[Collection("Postgres")]
public sealed class PostgresPaymentStoreTests
{
    private readonly bool _postgresAvailable = PostgresTestFixture.TryPing(PostgresTestFixture.OwnerConnectionString);

    private static string ResolvePaymentsMigrationPath()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "Commerce.sln")))
        {
            dir = dir.Parent;
        }
        if (dir is null)
        {
            throw new InvalidOperationException("Could not locate repo root.");
        }
        return Path.Combine(dir.FullName, "deploy", "db", "migrations");
    }

    private void ApplyMigrationsAndReset(NpgsqlConnection owner)
    {
        var migrationsDir = ResolvePaymentsMigrationPath();
        foreach (var file in new[]
        {
            "0001_init_rls.sql", "0002_users.sql", "0003_organizations_branches.sql",
            "0004_device_credentials.sql", "0005_password_recovery.sql", "0006_role_taxonomy.sql",
            "0007_platform_administration.sql", "0008_customer_registry.sql", "0009_catalog_and_pricing.sql",
            "0010_guest_ordering.sql", "0011_payments.sql"
        })
        {
            var sql = File.ReadAllText(Path.Combine(migrationsDir, file));
            if (file == "0001_init_rls.sql")
            {
                sql = sql.Replace("__APP_RUNTIME_PASSWORD__", "dev-only-password");
            }
            using var cmd = new NpgsqlCommand(sql, owner);
            cmd.ExecuteNonQuery();
        }

        using var reset = new NpgsqlCommand("TRUNCATE TABLE payment_entries, organizations CASCADE", owner);
        reset.ExecuteNonQuery();
    }

    private static PaymentEntry NewEntry(Guid organizationId, PaymentSubject subject) => new(
        EntryId: Guid.NewGuid(),
        OrganizationId: organizationId,
        Subject: subject,
        Kind: PaymentEntryKind.Payment,
        Method: PaymentMethod.Cash,
        Amount: 25m,
        ApprovalState: PaymentApprovalState.Approved,
        ReversesEntryId: null,
        ProviderReference: null,
        ActorId: Guid.NewGuid(),
        RecordedAtUtc: DateTimeOffset.UtcNow);

    [Fact]
    public async Task AppendAsync_ThenGetEntriesAsync_ReturnsOrderedRow()
    {
        if (!_postgresAvailable)
        {
            Console.WriteLine("SKIPPED: no live Postgres. Start deploy/dev/compose.yaml.");
            return;
        }

        var orgId = Guid.NewGuid();
        using (var owner = new NpgsqlConnection(PostgresTestFixture.OwnerConnectionString))
        {
            owner.Open();
            ApplyMigrationsAndReset(owner);
            using var insertOrg = new NpgsqlCommand("INSERT INTO organizations (id, name) VALUES ($1, 'Org A')", owner);
            insertOrg.Parameters.AddWithValue(orgId);
            insertOrg.ExecuteNonQuery();
        }

        await using var dataSource = NpgsqlDataSource.Create(PostgresTestFixture.DirectConnectionString);
        var store = new PostgresPaymentStore(dataSource);

        var subject = new PaymentSubject(PaymentSubjectKind.Order, Guid.NewGuid());
        var entry = NewEntry(orgId, subject);

        await store.AppendAsync(entry, CancellationToken.None);
        var entries = await store.GetEntriesAsync(subject, orgId, CancellationToken.None);

        Assert.Single(entries);
        Assert.Equal(entry.EntryId, entries[0].EntryId);
    }

    [Fact]
    public async Task GetEntriesAsync_CrossOrganization_ReturnsNoRows()
    {
        if (!_postgresAvailable)
        {
            Console.WriteLine("SKIPPED: no live Postgres. Start deploy/dev/compose.yaml.");
            return;
        }

        var orgAId = Guid.NewGuid();
        var orgBId = Guid.NewGuid();
        using (var owner = new NpgsqlConnection(PostgresTestFixture.OwnerConnectionString))
        {
            owner.Open();
            ApplyMigrationsAndReset(owner);
            using var insertOrgA = new NpgsqlCommand("INSERT INTO organizations (id, name) VALUES ($1, 'Org A')", owner);
            insertOrgA.Parameters.AddWithValue(orgAId);
            insertOrgA.ExecuteNonQuery();
            using var insertOrgB = new NpgsqlCommand("INSERT INTO organizations (id, name) VALUES ($1, 'Org B')", owner);
            insertOrgB.Parameters.AddWithValue(orgBId);
            insertOrgB.ExecuteNonQuery();
        }

        await using var dataSource = NpgsqlDataSource.Create(PostgresTestFixture.DirectConnectionString);
        var store = new PostgresPaymentStore(dataSource);

        var subject = new PaymentSubject(PaymentSubjectKind.Order, Guid.NewGuid());
        await store.AppendAsync(NewEntry(orgAId, subject), CancellationToken.None);

        var entries = await store.GetEntriesAsync(subject, orgBId, CancellationToken.None);

        Assert.Empty(entries);
    }

    [Fact]
    public async Task AppendAsync_SameEntryIdTwice_IsIdempotent()
    {
        if (!_postgresAvailable)
        {
            Console.WriteLine("SKIPPED: no live Postgres. Start deploy/dev/compose.yaml.");
            return;
        }

        var orgId = Guid.NewGuid();
        using (var owner = new NpgsqlConnection(PostgresTestFixture.OwnerConnectionString))
        {
            owner.Open();
            ApplyMigrationsAndReset(owner);
            using var insertOrg = new NpgsqlCommand("INSERT INTO organizations (id, name) VALUES ($1, 'Org A')", owner);
            insertOrg.Parameters.AddWithValue(orgId);
            insertOrg.ExecuteNonQuery();
        }

        await using var dataSource = NpgsqlDataSource.Create(PostgresTestFixture.DirectConnectionString);
        var store = new PostgresPaymentStore(dataSource);
        var subject = new PaymentSubject(PaymentSubjectKind.Order, Guid.NewGuid());
        var entry = NewEntry(orgId, subject);

        await store.AppendAsync(entry, CancellationToken.None);
        await store.AppendAsync(entry, CancellationToken.None);

        var entries = await store.GetEntriesAsync(subject, orgId, CancellationToken.None);
        Assert.Single(entries);
    }
}
