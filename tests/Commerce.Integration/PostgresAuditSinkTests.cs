using Commerce.Application.Audit;
using Commerce.Application.Ordering;
using Commerce.Cloud.Api.Auditing;
using Commerce.Domain.Audit;
using Commerce.Domain.Ordering;
using Microsoft.Extensions.DependencyInjection;
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
    public void TheSynchronousRecord_IsNotSupported_SoNoRequestThreadBlocksOnTheDatabase()
    {
        using var source = NpgsqlDataSource.Create("Host=127.0.0.1;Port=1;Timeout=1");
        IAuditSink sink = new PostgresAuditSink(source);

        Assert.Throws<NotSupportedException>(() => sink.Record(
            new AuditEntry(Guid.NewGuid(), Guid.NewGuid(), Guid.Empty, "customer-ordering-access", "denied", DateTimeOffset.UtcNow, Guid.NewGuid(), "not-found")));
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

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => sink.RecordAsync(
            new AuditEntry(Guid.NewGuid(), Guid.NewGuid(), Guid.Empty, "customer-ordering-access", "denied", DateTimeOffset.UtcNow, Guid.NewGuid(), "not-found"), cts.Token));
        Assert.Empty(logger.Entries);
    }

    [Fact]
    public async Task OnlyTheCustomerAccessPath_GetsThePostgresSink_EveryOtherConsumerKeepsTheSharedOne()
    {
        if (!_postgresAvailable) { Console.WriteLine("SKIPPED: no live Postgres."); return; }

        var org = await SeedOrganizationAsync();
        using var source = NpgsqlDataSource.Create(PostgresTestFixture.DirectConnectionString);
        var services = new ServiceCollection();
        services.AddSingleton(source);
        services.AddLogging();
        services.AddSingleton<IAuditSink, InMemoryAuditSink>();   // the shared sink, as Program registers it
        services.AddSingleton<ICustomerOrderingAccessResolver>(new StubResolver(null));
        services.AddCustomerCatalogAccessAudit();
        using var provider = services.BuildServiceProvider();

        // Shared consumers (TenantAuthorizationService, CatalogManagementService, ...) still get the in-memory sink.
        var shared = provider.GetRequiredService<IAuditSink>();
        Assert.IsType<InMemoryAuditSink>(shared);
        shared.Record(new AuditEntry(Guid.NewGuid(), org, Guid.Empty, "catalog.update", "allowed", DateTimeOffset.UtcNow, Guid.NewGuid(), "ok"));
        Assert.Empty(await ReadRowsAsync(org));   // a staff decision never becomes a `customer` row

        // The customer path writes to audit_log.
        await provider.GetRequiredService<CustomerCatalogAccessService>()
            .AuthorizeAsync(org, Guid.NewGuid(), Guid.NewGuid(), CancellationToken.None);
        var row = Assert.Single(await ReadRowsAsync(org));
        Assert.Equal(AuditActorKinds.Customer, row.ActorKind);
    }
}

internal sealed class CapturingLogger<T> : Microsoft.Extensions.Logging.ILogger<T>
{
    public List<(Microsoft.Extensions.Logging.LogLevel Level, string Message, Exception? Exception)> Entries { get; } = [];

    public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

    public bool IsEnabled(Microsoft.Extensions.Logging.LogLevel logLevel) => true;

    public void Log<TState>(Microsoft.Extensions.Logging.LogLevel logLevel, Microsoft.Extensions.Logging.EventId eventId,
        TState state, Exception? exception, Func<TState, Exception?, string> formatter) =>
        Entries.Add((logLevel, formatter(state, exception), exception));
}
