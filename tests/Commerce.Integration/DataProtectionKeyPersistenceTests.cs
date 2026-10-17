using System.Net;
using System.Net.Http.Json;
using System.Xml.Linq;
using Commerce.Cloud.Api.Authentication;
using Commerce.Cloud.Api.Endpoints;
using Commerce.Cloud.Api.Persistence;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;

namespace Commerce.Integration;

/// <summary>
/// railway-deploy-pipeline S10: a redeploy or restart of Cloud.Api keeps existing auth cookies valid, because the Data
/// Protection key ring lives in Postgres (`0061_data_protection_keys.sql`) instead of inside the container.
/// </summary>
[Collection("Postgres")]
public sealed class DataProtectionKeyPersistenceTests
{
    private const string PlatformReadonlyConnectionString =
        "Host=localhost;Port=5432;Database=" + PostgresTestFixture.Database +
        ";Username=platform_readonly;Password=dev-only-platform-readonly-password;Timeout=3";

    private static bool _migrated;

    private readonly bool _postgresAvailable = PostgresTestFixture.TryPing(PostgresTestFixture.OwnerConnectionString);

    public DataProtectionKeyPersistenceTests()
    {
        if (_postgresAvailable && !_migrated)
        {
            using var owner = new NpgsqlConnection(PostgresTestFixture.OwnerConnectionString);
            owner.Open();
            ApplyAllMigrations(owner);
            _migrated = true;
        }
    }

    private static void ApplyAllMigrations(NpgsqlConnection owner)
    {
        var dir = Path.Combine(PostgresTestFixture.RepoRoot(), "deploy", "db", "migrations");
        foreach (var file in Directory.GetFiles(dir, "*.sql").Select(Path.GetFileName).OfType<string>().OrderBy(f => f, StringComparer.Ordinal))
        {
            PostgresTestFixture.ApplyMigration(owner, file);
        }
    }

    private static T Scalar<T>(string connectionString, string sql, params object[] parameters)
    {
        using var conn = new NpgsqlConnection(connectionString);
        conn.Open();
        using var cmd = new NpgsqlCommand(sql, conn);
        foreach (var parameter in parameters) cmd.Parameters.AddWithValue(parameter);
        return (T)cmd.ExecuteScalar()!;
    }

    private static void Exec(string connectionString, string sql)
    {
        using var conn = new NpgsqlConnection(connectionString);
        conn.Open();
        using var cmd = new NpgsqlCommand(sql, conn);
        cmd.ExecuteNonQuery();
    }

    /// <summary>One Cloud.Api process: its own container, its own data source, the production registration.</summary>
    private static (ServiceProvider Services, IDataProtectionProvider Provider) BuildInstance()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton(NpgsqlDataSource.Create(PostgresTestFixture.DirectConnectionString));
        services.AddCommerceDataProtection();
        var provider = services.BuildServiceProvider();
        return (provider, provider.GetRequiredService<IDataProtectionProvider>());
    }

    [Fact]
    public void KeyRing_IsStoredInPostgres_WithNoOtherEncryptorOrRepositoryTakingOver()
    {
        // Guards S10 against a later PersistKeysTo*/ProtectKeysWith* call or a
        // platform default (DPAPI on Windows) silently replacing the store.
        var (services, _) = BuildInstance();
        using (services)
        {
            var options = services.GetRequiredService<Microsoft.Extensions.Options.IOptions<
                Microsoft.AspNetCore.DataProtection.KeyManagement.KeyManagementOptions>>().Value;
            Assert.IsType<PostgresDataProtectionKeyRepository>(options.XmlRepository);
            Assert.Null(options.XmlEncryptor);
        }
    }

    [Fact]
    public void PayloadProtectedByOneInstance_IsUnprotectedByAnIndependentlyBuiltSecondInstance()
    {
        if (!_postgresAvailable) { Console.WriteLine("SKIPPED: no live Postgres. Start deploy/dev/compose.yaml."); return; }

        // An empty ring: the first instance has to create the key, the second can only know it from the database.
        Exec(PostgresTestFixture.OwnerConnectionString, "TRUNCATE TABLE data_protection_keys");

        var (firstServices, first) = BuildInstance();
        string protectedPayload;
        using (firstServices)
        {
            protectedPayload = first.CreateProtector("S10.redeploy").Protect("signed-in user");
        }

        Assert.True(Scalar<long>(PostgresTestFixture.OwnerConnectionString, "SELECT count(*) FROM data_protection_keys") >= 1);

        var (secondServices, second) = BuildInstance();
        using (secondServices)
        {
            Assert.Equal("signed-in user", second.CreateProtector("S10.redeploy").Unprotect(protectedPayload));
        }
    }

    [Fact]
    public void StoreElement_TwiceWithTheSameFriendlyName_DoesNotThrow_AndKeepsOneRow()
    {
        if (!_postgresAvailable) { Console.WriteLine("SKIPPED: no live Postgres. Start deploy/dev/compose.yaml."); return; }

        var friendlyName = "test-race-" + Guid.NewGuid().ToString("N");
        using var dataSource = NpgsqlDataSource.Create(PostgresTestFixture.DirectConnectionString);
        var repository = new PostgresDataProtectionKeyRepository(dataSource);
        try
        {
            // Not a <key> element: the key manager skips unknown elements, so this row cannot disturb the real ring.
            repository.StoreElement(new XElement("test-element", new XAttribute("n", 1)), friendlyName);
            repository.StoreElement(new XElement("test-element", new XAttribute("n", 2)), friendlyName);

            Assert.Equal(1L, Scalar<long>(PostgresTestFixture.OwnerConnectionString,
                "SELECT count(*) FROM data_protection_keys WHERE friendly_name = $1", friendlyName));
            Assert.Equal("""<test-element n="1" />""", Scalar<string>(PostgresTestFixture.OwnerConnectionString,
                "SELECT xml FROM data_protection_keys WHERE friendly_name = $1", friendlyName));
        }
        finally
        {
            using var owner = new NpgsqlConnection(PostgresTestFixture.OwnerConnectionString);
            owner.Open();
            using var cmd = new NpgsqlCommand("DELETE FROM data_protection_keys WHERE friendly_name = $1", owner);
            cmd.Parameters.AddWithValue(friendlyName);
            cmd.ExecuteNonQuery();
        }
    }

    [Fact]
    public void AppRuntime_ReadsAndInserts_ButCannotChangeOrDelete_AndPlatformReadonlyCannotRead()
    {
        if (!_postgresAvailable) { Console.WriteLine("SKIPPED: no live Postgres. Start deploy/dev/compose.yaml."); return; }

        var friendlyName = "test-grants-" + Guid.NewGuid().ToString("N");
        using var dataSource = NpgsqlDataSource.Create(PostgresTestFixture.DirectConnectionString);
        var repository = new PostgresDataProtectionKeyRepository(dataSource);
        try
        {
            repository.StoreElement(new XElement("test-element"), friendlyName);
            Assert.Contains(repository.GetAllElements(), element => element.Name.LocalName == "test-element");

            foreach (var sql in new[]
                     {
                         "UPDATE data_protection_keys SET xml = '<x/>'",
                         "DELETE FROM data_protection_keys",
                         "TRUNCATE TABLE data_protection_keys",
                     })
            {
                var denied = Assert.Throws<PostgresException>(() => Exec(PostgresTestFixture.DirectConnectionString, sql));
                Assert.Equal(PostgresErrorCodes.InsufficientPrivilege, denied.SqlState);
            }

            var readonlyDenied = Assert.Throws<PostgresException>(() =>
                Scalar<long>(PlatformReadonlyConnectionString, "SELECT count(*) FROM data_protection_keys"));
            Assert.Equal(PostgresErrorCodes.InsufficientPrivilege, readonlyDenied.SqlState);

            Assert.False(Scalar<bool>(PostgresTestFixture.OwnerConnectionString,
                "SELECT has_table_privilege('public', 'data_protection_keys', 'SELECT')"));
        }
        finally
        {
            using var owner = new NpgsqlConnection(PostgresTestFixture.OwnerConnectionString);
            owner.Open();
            using var cmd = new NpgsqlCommand("DELETE FROM data_protection_keys WHERE friendly_name = $1", owner);
            cmd.Parameters.AddWithValue(friendlyName);
            cmd.ExecuteNonQuery();
        }
    }

    [Fact]
    public void Migration_IsRerunnable_AndForcesRowLevelSecurity()
    {
        if (!_postgresAvailable) { Console.WriteLine("SKIPPED: no live Postgres. Start deploy/dev/compose.yaml."); return; }

        using (var owner = new NpgsqlConnection(PostgresTestFixture.OwnerConnectionString))
        {
            owner.Open();
            PostgresTestFixture.ApplyMigration(owner, "0061_data_protection_keys.sql");
            PostgresTestFixture.ApplyMigration(owner, "0061_data_protection_keys.sql");
        }

        Assert.True(Scalar<bool>(PostgresTestFixture.OwnerConnectionString,
            "SELECT relrowsecurity AND relforcerowsecurity FROM pg_class WHERE relname = 'data_protection_keys'"));
    }

    [Fact]
    public void DevInitSnapshot_CarriesTheKeyRingTable()
    {
        if (!_postgresAvailable) { Console.WriteLine("SKIPPED: no live Postgres. Start deploy/dev/compose.yaml."); return; }

        var dbName = "dpk0061_" + Guid.NewGuid().ToString("N");
        Exec(PostgresTestFixture.OwnerConnectionString, $"CREATE DATABASE {dbName} OWNER commerce_owner");
        try
        {
            var scratch = new NpgsqlConnectionStringBuilder(PostgresTestFixture.OwnerConnectionString) { Database = dbName }.ConnectionString;
            var sql = File.ReadAllText(Path.Combine(PostgresTestFixture.RepoRoot(), "deploy", "dev", "db", "init-rls.sql"))
                .Replace("__APP_RUNTIME_PASSWORD__", "dev-only-password")
                .Replace("__PLATFORM_READONLY_PASSWORD__", "dev-only-platform-readonly-password");
            Exec(scratch, sql);

            Assert.True(Scalar<bool>(scratch,
                "SELECT relrowsecurity AND relforcerowsecurity FROM pg_class WHERE relname = 'data_protection_keys'"));
            Assert.True(Scalar<bool>(scratch, "SELECT has_table_privilege('app_runtime', 'data_protection_keys', 'INSERT')"));
        }
        finally
        {
            NpgsqlConnection.ClearAllPools();
            Exec(PostgresTestFixture.OwnerConnectionString, $"DROP DATABASE IF EXISTS {dbName} WITH (FORCE)");
        }
    }

    /// <summary>
    /// The whole S10 path through the real host: a staff cookie issued by one Cloud.Api instance is accepted by a second
    /// instance (a redeploy) sharing the database.
    /// </summary>
    [Fact]
    public async Task StaffCookieIssuedByOneHost_IsAcceptedByASecondHostSharingTheDatabase()
    {
        if (!_postgresAvailable) { Console.WriteLine("SKIPPED: no live Postgres. Start deploy/dev/compose.yaml."); return; }

        Exec(PostgresTestFixture.OwnerConnectionString, "TRUNCATE TABLE data_protection_keys");
        var email = "dp-redeploy-" + Guid.NewGuid().ToString("N")[..8] + "@example.com";
        const string password = "correct-password";
        var options = new WebApplicationFactoryClientOptions { HandleCookies = false, AllowAutoRedirect = false, BaseAddress = new Uri("https://localhost") };

        string cookie;
        using (var before = NewHost())
        {
            var organizationId = Guid.NewGuid();
            var token = before.Services.GetRequiredService<BootstrapTokenRegistry>().Issue(organizationId);
            var bootstrap = await before.CreateClient(options).PostAsJsonAsync(
                "/account/bootstrap", new BootstrapRequest(organizationId, token, "Org " + organizationId, "HQ", email, password));
            bootstrap.EnsureSuccessStatusCode();

            var signIn = await before.CreateClient(options).PostAsJsonAsync("/account/sign-in", new SignInRequest(email, password));
            signIn.EnsureSuccessStatusCode();
            cookie = string.Join("; ", signIn.Headers.GetValues("Set-Cookie").Select(header => header.Split(';')[0]));
        }

        // The key that protects the cookie is in the database, not in a per-machine folder both hosts happen to share.
        Assert.True(Scalar<long>(PostgresTestFixture.OwnerConnectionString,
            "SELECT count(*) FROM data_protection_keys WHERE xml LIKE '<key %'") >= 1);

        using var after = NewHost();
        using var request = new HttpRequestMessage(HttpMethod.Get, "/account/me");
        request.Headers.Add("Cookie", cookie);
        var me = await after.CreateClient(options).SendAsync(request);

        Assert.Equal(HttpStatusCode.OK, me.StatusCode);
    }

    private static WebApplicationFactory<Program> NewHost() =>
        new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
            builder.UseSetting("ConnectionStrings:Commerce", PostgresTestFixture.DirectConnectionString));
}
