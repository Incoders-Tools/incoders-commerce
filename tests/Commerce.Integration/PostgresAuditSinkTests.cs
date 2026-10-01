using Commerce.Application.Access;
using Commerce.Application.Audit;
using Commerce.Application.Management;
using Commerce.Application.Ordering;
using Commerce.Cloud.Api.Auditing;
using Commerce.Cloud.Api.Management;
using Commerce.Cloud.Api.Tenancy;
using Commerce.Domain.Audit;
using Commerce.Domain.Catalog;
using Commerce.Domain.Identity;
using Commerce.Domain.Ordering;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;

namespace Commerce.Integration;

/// <summary>
/// Durable audit: every decision of the shared <see cref="IAuditSink"/> (staff authorization and catalog
/// management as `org-user`, customer catalog access as `customer`) used to be kept by an in-memory sink and
/// vanished on every restart. <see cref="PostgresAuditSink"/> writes them to the append-only `audit_log`;
/// every test reads the rows back with the owner role (app_runtime has no SELECT by design) and through a
/// brand-new data source where the point is surviving a restart.
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

    private static AuditEntry Entry(Guid org, AuditActorKind kind = AuditActorKind.OrgUser, string outcome = "denied") =>
        new(Guid.NewGuid(), kind, org, Guid.Empty, "customer-ordering-access", outcome, DateTimeOffset.UtcNow, Guid.NewGuid(), "not-found");

    private static UserAccount Staff(Guid org, Guid branch, Permission permissions) =>
        new(Guid.NewGuid(), org, new[] { branch }, new[] { new Role("catalog-manager", permissions) });

    private static Product ProductOf(Guid org) =>
        new(Guid.NewGuid(), org, "Original", Guid.NewGuid(), Guid.NewGuid());

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
    public async Task TheSynchronousRecord_IsDurable_NotATrap()
    {
        if (!_postgresAvailable) { Console.WriteLine("SKIPPED: no live Postgres."); return; }

        var org = await SeedOrganizationAsync();
        using var source = NpgsqlDataSource.Create(PostgresTestFixture.DirectConnectionString);
        IAuditSink sink = new PostgresAuditSink(source);

        sink.Record(Entry(org));   // sync callers get the same durable, fail-open behaviour

        var row = Assert.Single(await ReadRowsAsync(org));
        Assert.Equal(AuditActorKinds.OrgUser, row.ActorKind);
    }

    [Fact]
    public void TheSynchronousRecord_FailsOpen_AndLogsAnError()
    {
        var logger = new CapturingLogger<PostgresAuditSink>();
        using var source = NpgsqlDataSource.Create("Host=127.0.0.1;Port=1;Timeout=1");
        IAuditSink sink = new PostgresAuditSink(source, logger);

        sink.Record(Entry(Guid.NewGuid()));   // does not throw

        Assert.Equal(Microsoft.Extensions.Logging.LogLevel.Error, Assert.Single(logger.Entries).Level);
    }

    [Fact]
    public async Task AStaffDecision_IsWrittenAsAnOrgUserRow_AndSurvivesANewDataSource()
    {
        if (!_postgresAvailable) { Console.WriteLine("SKIPPED: no live Postgres."); return; }

        var org = await SeedOrganizationAsync();
        var branch = Guid.NewGuid();
        var actor = Staff(org, branch, Permission.ManageCatalog);
        var correlationId = Guid.NewGuid();

        using (var source = NpgsqlDataSource.Create(PostgresTestFixture.DirectConnectionString))
        {
            var adapter = new CloudCatalogManagementAdapter(new CatalogManagementService(
                new TenantAuthorizationService(new PostgresAuditSink(source))));
            var outcome = await adapter.RenameProductAsync(
                new CloudTenantScope(org), actor, ProductOf(org), branch, "Renamed", false, correlationId, CancellationToken.None);
            Assert.Equal(ManagementOutcomeStatus.Allowed, outcome.Status);
        }

        var row = Assert.Single(await ReadRowsAsync(org));
        Assert.Equal(AuditActorKinds.OrgUser, row.ActorKind);
        Assert.Equal(actor.Id, row.ActorId);
        Assert.Equal("update-product-name.allowed", row.Action);
        Assert.Contains(correlationId.ToString(), row.NewValue);
    }

    [Fact]
    public async Task ADeniedStaffDecision_IsWrittenToo()
    {
        if (!_postgresAvailable) { Console.WriteLine("SKIPPED: no live Postgres."); return; }

        var org = await SeedOrganizationAsync();
        var branch = Guid.NewGuid();
        using var source = NpgsqlDataSource.Create(PostgresTestFixture.DirectConnectionString);
        var auth = new TenantAuthorizationService(new PostgresAuditSink(source));

        var outcome = await new CatalogManagementService(auth).RenameProductAsync(
            Staff(org, branch, Permission.None), ProductOf(org),
            new ManagementRequest(org, branch, Guid.NewGuid(), "x", false, Guid.NewGuid()), CancellationToken.None);

        Assert.Equal(ManagementOutcomeStatus.Denied, outcome.Status);
        var row = Assert.Single(await ReadRowsAsync(org));
        Assert.Equal("update-product-name.denied", row.Action);
        Assert.Contains("insufficient-permission", row.NewValue);
    }

    [Fact]
    public async Task AFailingAuditWrite_NeverChangesAStaffDecision_AndIsLoggedAtError()
    {
        var org = Guid.NewGuid();
        var branch = Guid.NewGuid();
        var logger = new CapturingLogger<PostgresAuditSink>();
        using var source = NpgsqlDataSource.Create("Host=127.0.0.1;Port=1;Timeout=1");
        var service = new CatalogManagementService(new TenantAuthorizationService(new PostgresAuditSink(source, logger)));

        var outcome = await service.RenameProductAsync(
            Staff(org, branch, Permission.ManageCatalog), ProductOf(org),
            new ManagementRequest(org, branch, Guid.NewGuid(), "x", false, Guid.NewGuid()), CancellationToken.None);

        Assert.Equal(ManagementOutcomeStatus.Allowed, outcome.Status);
        Assert.Equal(Microsoft.Extensions.Logging.LogLevel.Error, Assert.Single(logger.Entries).Level);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task AFailingAuditWrite_FailsOpen_TheDecisionStandsAndAnErrorIsLogged(bool credentialEnabled)
    {
        var org = Guid.NewGuid();
        var access = new CustomerOrderingAccess(org, Guid.NewGuid(), Guid.NewGuid(), isEnabled: credentialEnabled);
        var logger = new CapturingLogger<PostgresAuditSink>();
        // Nothing listens on port 1: opening the audit connection fails.
        using var source = NpgsqlDataSource.Create("Host=127.0.0.1;Port=1;Timeout=1");
        var service = new CustomerCatalogAccessService(new StubResolver(access), new PostgresAuditSink(source, logger));

        var result = await service.AuthorizeAsync(org, Guid.NewGuid(), Guid.NewGuid(), CancellationToken.None);

        Assert.Equal(credentialEnabled, result.Allowed);   // never blocked, never flipped by the audit failure
        var entry = Assert.Single(logger.Entries);
        Assert.Equal(Microsoft.Extensions.Logging.LogLevel.Error, entry.Level);
        Assert.NotNull(entry.Exception);
        Assert.Contains(org.ToString(), entry.Message);
        Assert.Contains(credentialEnabled ? "allowed" : "denied", entry.Message);
    }

    [Fact]
    public async Task ACancelledWrite_IsNotSwallowed()
    {
        using var source = NpgsqlDataSource.Create("Host=127.0.0.1;Port=1;Timeout=1");
        var logger = new CapturingLogger<PostgresAuditSink>();
        var sink = new PostgresAuditSink(source, logger);
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => sink.RecordAsync(Entry(Guid.NewGuid()), cts.Token));
        Assert.Empty(logger.Entries);
    }

    [Fact]
    public async Task TheSharedAuditSink_IsDurable_AndEachConsumerGetsItsOwnActorKind()
    {
        if (!_postgresAvailable) { Console.WriteLine("SKIPPED: no live Postgres."); return; }

        var org = await SeedOrganizationAsync();
        var branch = Guid.NewGuid();
        using var source = NpgsqlDataSource.Create(PostgresTestFixture.DirectConnectionString);
        var services = new ServiceCollection();
        services.AddSingleton(source);
        services.AddLogging();
        services.AddDurableAuditSink();   // what Program registers as THE IAuditSink
        services.AddSingleton<ICustomerOrderingAccessResolver>(new StubResolver(null));
        services.AddSingleton<TenantAuthorizationService>();
        services.AddSingleton<CatalogManagementService>();
        services.AddSingleton<CustomerCatalogAccessService>();
        using var provider = services.BuildServiceProvider();

        Assert.IsType<PostgresAuditSink>(provider.GetRequiredService<IAuditSink>());

        await provider.GetRequiredService<CatalogManagementService>().RenameProductAsync(
            Staff(org, branch, Permission.ManageCatalog), ProductOf(org),
            new ManagementRequest(org, branch, Guid.NewGuid(), "x", false, Guid.NewGuid()), CancellationToken.None);
        await provider.GetRequiredService<CustomerCatalogAccessService>()
            .AuthorizeAsync(org, Guid.NewGuid(), Guid.NewGuid(), CancellationToken.None);

        var rows = await ReadRowsAsync(org);
        Assert.Equal([AuditActorKinds.OrgUser, AuditActorKinds.Customer], rows.Select(r => r.ActorKind));
    }
}
