using Commerce.Application.Audit;
using Commerce.Application.Ordering;
using Commerce.Cloud.Api.Auditing;
using Commerce.Domain.Audit;
using Commerce.Domain.Ordering;
using Npgsql;

namespace Commerce.Integration;

/// <summary>
/// persist-web-orders T4: the customer catalog / ordering access decisions (denials above all) used to
/// be kept by an in-memory sink and vanished on every restart. <see cref="PostgresAuditSink"/> writes
/// them to the append-only `audit_log`; every test reads the rows back with the owner role (app_runtime
/// has no SELECT by design) and through a brand-new data source where the point is surviving a restart.
/// </summary>
[Collection("Postgres")]
public sealed class PostgresAuditSinkTests
{
    private readonly bool _postgresAvailable = PostgresTestFixture.TryPing(PostgresTestFixture.OwnerConnectionString);

    private sealed class StubResolver(CustomerOrderingAccess? access) : ICustomerOrderingAccessResolver
    {
        public Task<CustomerOrderingAccess?> ResolveAsync(Guid organizationId, Guid credential, CancellationToken ct) =>
            Task.FromResult(access);
    }

    private static async Task<Guid> SeedOrganizationAsync()
    {
        var orgId = Guid.NewGuid();
        await using var connection = new NpgsqlConnection(PostgresTestFixture.OwnerConnectionString);
        await connection.OpenAsync();
        await using var cmd = new NpgsqlCommand("INSERT INTO organizations (id, name) VALUES ($1, 'Org')", connection);
        cmd.Parameters.AddWithValue(orgId);
        await cmd.ExecuteNonQueryAsync();
        return orgId;
    }

    private static async Task<List<(string ActorKind, Guid ActorId, string EntityType, string Action, string NewValue)>> ReadRowsAsync(Guid orgId)
    {
        await using var connection = new NpgsqlConnection(PostgresTestFixture.OwnerConnectionString);
        await connection.OpenAsync();
        await using var cmd = new NpgsqlCommand(
            "SELECT actor_kind, actor_id, entity_type, action, new_value::text FROM audit_log WHERE organization_id = $1 ORDER BY id", connection);
        cmd.Parameters.AddWithValue(orgId);
        await using var reader = await cmd.ExecuteReaderAsync();
        var rows = new List<(string, Guid, string, string, string)>();
        while (await reader.ReadAsync())
        {
            rows.Add((reader.GetString(0), reader.GetGuid(1), reader.GetString(2), reader.GetString(3), reader.GetString(4)));
        }
        return rows;
    }

    [Fact]
    public async Task ADeniedAccess_IsWrittenToTheAuditLog_AndSurvivesANewDataSource()
    {
        if (!_postgresAvailable) { Console.WriteLine("SKIPPED: no live Postgres."); return; }

        var org = await SeedOrganizationAsync();
        var correlationId = Guid.NewGuid();

        using (var source = NpgsqlDataSource.Create(PostgresTestFixture.DirectConnectionString))
        {
            var service = new CustomerCatalogAccessService(new StubResolver(null), new PostgresAuditSink(source));
            var result = await service.AuthorizeAsync(org, Guid.NewGuid(), correlationId, CancellationToken.None);
            Assert.False(result.Allowed);
        }

        // "Restart": nothing of the previous process is left, the row is in the database.
        var rows = await ReadRowsAsync(org);
        var row = Assert.Single(rows);
        Assert.Equal(AuditActorKinds.Customer, row.ActorKind);
        Assert.Equal(Guid.Empty, row.ActorId);               // unknown credential: no customer to name
        Assert.Equal("customer-ordering-access", row.EntityType);
        Assert.Equal("customer-ordering-access.denied", row.Action);
        Assert.Contains("\"reason\": \"not-found\"", row.NewValue);
        Assert.Contains(correlationId.ToString(), row.NewValue);
    }

    [Fact]
    public async Task ARevokedCredential_NamesTheCustomer_AndAnAllowedAccessIsRecordedToo()
    {
        if (!_postgresAvailable) { Console.WriteLine("SKIPPED: no live Postgres."); return; }

        var org = await SeedOrganizationAsync();
        var customerId = Guid.NewGuid();
        using var source = NpgsqlDataSource.Create(PostgresTestFixture.DirectConnectionString);
        var sink = new PostgresAuditSink(source);

        var revoked = new CustomerOrderingAccess(org, customerId, Guid.NewGuid(), isEnabled: false);
        var enabled = new CustomerOrderingAccess(org, customerId, Guid.NewGuid(), isEnabled: true);
        await new CustomerCatalogAccessService(new StubResolver(revoked), sink).AuthorizeAsync(org, Guid.NewGuid(), Guid.NewGuid(), CancellationToken.None);
        await new CustomerCatalogAccessService(new StubResolver(enabled), sink).AuthorizeAsync(org, Guid.NewGuid(), Guid.NewGuid(), CancellationToken.None);

        var rows = await ReadRowsAsync(org);
        Assert.Equal(["customer-ordering-access.denied", "customer-ordering-access.allowed"], rows.Select(r => r.Action));
        Assert.All(rows, r => Assert.Equal(customerId, r.ActorId));
        Assert.Contains("credential-revoked", rows[0].NewValue);
    }

    [Fact]
    public async Task TheSynchronousRecord_ReachesTheSameLog()
    {
        if (!_postgresAvailable) { Console.WriteLine("SKIPPED: no live Postgres."); return; }

        var org = await SeedOrganizationAsync();
        using var source = NpgsqlDataSource.Create(PostgresTestFixture.DirectConnectionString);
        IAuditSink sink = new PostgresAuditSink(source);

        sink.Record(new AuditEntry(Guid.NewGuid(), org, Guid.Empty, "customer-ordering-access", "denied", DateTimeOffset.UtcNow, Guid.NewGuid(), "not-found"));

        Assert.Single(await ReadRowsAsync(org));
    }
}
