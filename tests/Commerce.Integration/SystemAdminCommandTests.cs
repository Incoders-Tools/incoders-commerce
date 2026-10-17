using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Commerce.Cloud.Api.Endpoints;
using Commerce.Cloud.Api.Operations;
using Commerce.Cloud.Api.Persistence;
using Commerce.Cloud.Api.Tenancy;
using Commerce.Domain.Identity;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Npgsql;

namespace Commerce.Integration;

/// <summary>
/// railway-deploy-pipeline S12: the platform system administrator is created outside Development by a console command
/// inside the Cloud.Api executable (`dotnet Commerce.Cloud.Api.dll create-system-admin --email ...`), never by an HTTP
/// endpoint. The password comes from the terminal (or redirected stdin), never from an argument. Against live Postgres
/// through `app_runtime`, the same connection the web uses.
/// </summary>
[Collection("Postgres")]
public sealed class SystemAdminCommandTests
{
    private const string Email = "admin@incoders.example";
    private const string FirstPassword = "first-correct-horse-battery";
    private const string SecondPassword = "second-correct-horse-battery";

    private static bool _migrated;

    private readonly bool _postgresAvailable = PostgresTestFixture.TryPing(PostgresTestFixture.OwnerConnectionString);

    public SystemAdminCommandTests()
    {
        if (!_postgresAvailable)
        {
            return;
        }

        using var owner = new NpgsqlConnection(PostgresTestFixture.OwnerConnectionString);
        owner.Open();
        if (!_migrated)
        {
            var dir = Path.Combine(PostgresTestFixture.RepoRoot(), "deploy", "db", "migrations");
            foreach (var file in Directory.GetFiles(dir, "*.sql").Select(Path.GetFileName).OfType<string>().OrderBy(f => f, StringComparer.Ordinal))
            {
                PostgresTestFixture.ApplyMigration(owner, file);
            }
            _migrated = true;
        }

        using var reset = new NpgsqlCommand(
            "TRUNCATE TABLE password_reset_tokens, user_directory, users, device_credentials, branches, organizations CASCADE", owner);
        reset.ExecuteNonQuery();
    }

    private static IConfiguration Configuration() =>
        new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["ConnectionStrings:Commerce"] = PostgresTestFixture.DirectConnectionString })
            .Build();

    private sealed record CommandRun(int? ExitCode, string Output, string Error);

    private static async Task<CommandRun> RunAsync(string[] args, string stdin)
    {
        var output = new StringWriter();
        var error = new StringWriter();
        var console = new CommandConsole(new StringReader(stdin), inputRedirected: true, output, error);
        var exitCode = await OperationsCommands.TryRunAsync(args, Configuration(), console, CancellationToken.None);
        return new CommandRun(exitCode, output.ToString(), error.ToString());
    }

    private static string[] SplitArgs(string commandLine) => commandLine.Split(' ', StringSplitOptions.RemoveEmptyEntries);

    private static Task<CommandRun> CreateSystemAdminAsync(string password, string? confirmation = null) =>
        RunAsync(["create-system-admin", "--email", Email], $"{password}\n{confirmation ?? password}\n");

    private static WebApplicationFactory<Program> NewFactory() =>
        new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
            builder.UseSetting("ConnectionStrings:Commerce", PostgresTestFixture.DirectConnectionString));

    private static HttpClient NewClient(WebApplicationFactory<Program> factory) =>
        // https: the auth cookie is Secure.
        factory.CreateClient(new WebApplicationFactoryClientOptions { BaseAddress = new Uri("https://localhost"), AllowAutoRedirect = false });

    private static T Scalar<T>(string sql, params object[] parameters)
    {
        using var owner = new NpgsqlConnection(PostgresTestFixture.OwnerConnectionString);
        owner.Open();
        using var cmd = new NpgsqlCommand(sql, owner);
        foreach (var parameter in parameters) cmd.Parameters.AddWithValue(parameter);
        return (T)cmd.ExecuteScalar()!;
    }

    private static void AssertNoSecretPrinted(CommandRun run, params string[] secrets)
    {
        foreach (var secret in secrets)
        {
            Assert.DoesNotContain(secret, run.Output, StringComparison.Ordinal);
            Assert.DoesNotContain(secret, run.Error, StringComparison.Ordinal);
        }
        Assert.DoesNotContain("AQAAAA", run.Output + run.Error, StringComparison.Ordinal); // PasswordHasher v3 hash prefix
    }

    [Fact]
    public async Task Create_SignsInThroughTheRealEndpoint_AndMeReportsTheSystemAdminFlag()
    {
        if (!_postgresAvailable) { Console.WriteLine("SKIPPED: no live Postgres. Start deploy/dev/compose.yaml."); return; }

        var run = await CreateSystemAdminAsync(FirstPassword);

        Assert.Equal(0, run.ExitCode);
        Assert.Contains($"Created system administrator {Email}.", run.Output, StringComparison.Ordinal);
        Assert.Equal(string.Empty, run.Error);
        AssertNoSecretPrinted(run, FirstPassword);

        // The account shape the Development seed creates: its own organization row and branch, zero roles, empty scope.
        Assert.Equal(1L, Scalar<long>("SELECT count(*) FROM users WHERE email = $1 AND is_system_admin AND roles = '[]'::jsonb AND branch_scope = '{}'", Email));
        Assert.Equal("Platform System Administrator", Scalar<string>(
            "SELECT o.name FROM organizations o JOIN users u ON u.organization_id = o.id WHERE u.email = $1", Email));
        Assert.Equal("Main", Scalar<string>(
            "SELECT b.name FROM branches b JOIN users u ON u.organization_id = b.organization_id WHERE u.email = $1", Email));
        Assert.Equal(1L, Scalar<long>(
            "SELECT count(*) FROM audit_log a JOIN users u ON u.id = a.entity_id WHERE u.email = $1 AND a.action = 'system-admin.created' AND a.actor_kind = 'console'", Email));

        await using var factory = NewFactory();
        var client = NewClient(factory);
        var signIn = await client.PostAsJsonAsync("/account/sign-in", new { email = Email, password = FirstPassword });
        Assert.Equal(HttpStatusCode.OK, signIn.StatusCode);

        var me = await client.GetAsync("/account/me");
        Assert.Equal(HttpStatusCode.OK, me.StatusCode);
        var body = await me.Content.ReadFromJsonAsync<JsonElement>();
        Assert.True(body.GetProperty("isSystemAdmin").GetBoolean());
        Assert.Equal(0, body.GetProperty("permissions").GetInt32());
    }

    [Fact]
    public async Task RunAgainWithANewPassword_UpdatesThePassword_OldRejected_NewAccepted_StillOneUser()
    {
        if (!_postgresAvailable) { Console.WriteLine("SKIPPED: no live Postgres. Start deploy/dev/compose.yaml."); return; }

        Assert.Equal(0, (await CreateSystemAdminAsync(FirstPassword)).ExitCode);
        var userId = Scalar<Guid>("SELECT id FROM users WHERE email = $1", Email);

        var run = await CreateSystemAdminAsync(SecondPassword);

        Assert.Equal(0, run.ExitCode);
        Assert.Contains($"Password updated for system administrator {Email}.", run.Output, StringComparison.Ordinal);
        AssertNoSecretPrinted(run, FirstPassword, SecondPassword);
        Assert.Equal(1L, Scalar<long>("SELECT count(*) FROM users WHERE email = $1", Email));
        Assert.Equal(1L, Scalar<long>("SELECT count(*) FROM organizations"));
        Assert.Equal(userId, Scalar<Guid>("SELECT id FROM users WHERE email = $1", Email));
        Assert.Equal(1L, Scalar<long>(
            "SELECT count(*) FROM audit_log WHERE entity_id = $1 AND action = 'system-admin.password-reset' AND actor_kind = 'console'", userId));

        await using var factory = NewFactory();
        var client = NewClient(factory);
        var oldPassword = await client.PostAsJsonAsync("/account/sign-in", new { email = Email, password = FirstPassword });
        Assert.Equal(HttpStatusCode.Unauthorized, oldPassword.StatusCode);
        var newPassword = await client.PostAsJsonAsync("/account/sign-in", new { email = Email, password = SecondPassword });
        Assert.Equal(HttpStatusCode.OK, newPassword.StatusCode);
    }

    [Fact]
    public async Task EmailOfAnExistingOrganizationUser_Refuses_AndLeavesTheUserUnchanged()
    {
        if (!_postgresAvailable) { Console.WriteLine("SKIPPED: no live Postgres. Start deploy/dev/compose.yaml."); return; }

        var dataSource = NpgsqlDataSource.Create(PostgresTestFixture.DirectConnectionString);
        await using (dataSource)
        {
            var userStore = new PostgresUserAccountStore(dataSource);
            var organizationStore = new PostgresOrganizationStore(dataSource, userStore);
            var organizationId = Guid.NewGuid();
            var branchId = Guid.NewGuid();
            var userId = Guid.NewGuid();
            var hash = new PasswordHasher<UserAccount>().HashPassword(new UserAccount(userId, organizationId, [], []), FirstPassword);
            var outcome = await organizationStore.TryCreateBootstrapAsync(
                new CloudTenantScope(organizationId), new NewOrganization(organizationId, "Client"), new NewBranch(branchId, "Main"),
                new NewUserAccount(userId, Email, hash, [branchId], [new RoleDto(RoleCatalog.BusinessAdmin, RoleCatalog.BusinessAdminPermissions)]),
                CancellationToken.None);
            Assert.Equal(BootstrapOutcome.Created, outcome);
        }
        var hashBefore = Scalar<string>("SELECT password_hash FROM users WHERE email = $1", Email);
        var versionBefore = Scalar<int>("SELECT session_version FROM users WHERE email = $1", Email);
        // audit_log outlives the per-test reset, so compare with the rows other tests left behind.
        var systemAdminAuditBefore = Scalar<long>("SELECT count(*) FROM audit_log WHERE action LIKE 'system-admin.%'");

        var run = await CreateSystemAdminAsync(SecondPassword);

        Assert.NotNull(run.ExitCode);
        Assert.NotEqual(0, run.ExitCode);
        Assert.Contains($"{Email} belongs to an organization user", run.Error, StringComparison.Ordinal);
        AssertNoSecretPrinted(run, SecondPassword);
        Assert.Equal(hashBefore, Scalar<string>("SELECT password_hash FROM users WHERE email = $1", Email));
        Assert.Equal(versionBefore, Scalar<int>("SELECT session_version FROM users WHERE email = $1", Email));
        Assert.False(Scalar<bool>("SELECT is_system_admin FROM users WHERE email = $1", Email));
        Assert.Equal(systemAdminAuditBefore, Scalar<long>("SELECT count(*) FROM audit_log WHERE action LIKE 'system-admin.%'"));
    }

    [Fact]
    public async Task MismatchedConfirmation_FailsWithoutWritingAnything()
    {
        if (!_postgresAvailable) { Console.WriteLine("SKIPPED: no live Postgres. Start deploy/dev/compose.yaml."); return; }

        var run = await CreateSystemAdminAsync(FirstPassword, SecondPassword);

        Assert.NotNull(run.ExitCode);
        Assert.NotEqual(0, run.ExitCode);
        Assert.Contains("The passwords do not match.", run.Error, StringComparison.Ordinal);
        AssertNoSecretPrinted(run, FirstPassword, SecondPassword);
        Assert.Equal(0L, Scalar<long>("SELECT count(*) FROM users"));
        Assert.Equal(0L, Scalar<long>("SELECT count(*) FROM organizations"));
    }

    [Fact]
    public async Task BlankPassword_FailsUnderTheStaffPasswordRule_WithoutWritingAnything()
    {
        if (!_postgresAvailable) { Console.WriteLine("SKIPPED: no live Postgres. Start deploy/dev/compose.yaml."); return; }

        var run = await CreateSystemAdminAsync("   ");

        Assert.NotNull(run.ExitCode);
        Assert.NotEqual(0, run.ExitCode);
        Assert.Contains("A password is required.", run.Error, StringComparison.Ordinal);
        Assert.Equal(0L, Scalar<long>("SELECT count(*) FROM users"));
        Assert.Equal(0L, Scalar<long>("SELECT count(*) FROM organizations"));
    }

    [Fact]
    public async Task PasswordAsAnArgument_IsRejected_WithoutWritingAnything()
    {
        if (!_postgresAvailable) { Console.WriteLine("SKIPPED: no live Postgres. Start deploy/dev/compose.yaml."); return; }

        var run = await RunAsync(["create-system-admin", "--email", Email, "--password", FirstPassword], $"{FirstPassword}\n{FirstPassword}\n");

        Assert.Equal(2, run.ExitCode);
        Assert.Contains("Unknown argument '--password'.", run.Error, StringComparison.Ordinal);
        Assert.Equal(0L, Scalar<long>("SELECT count(*) FROM users"));
    }

    [Theory]
    [InlineData("create-system-admin")]
    [InlineData("create-system-admin --email")]
    [InlineData("create-system-admin --email not-an-email")]
    public async Task MissingOrInvalidEmail_IsAUsageError(string commandLine)
    {
        var run = await RunAsync(SplitArgs(commandLine), string.Empty);

        Assert.Equal(2, run.ExitCode);
        Assert.Contains("usage: dotnet Commerce.Cloud.Api.dll create-system-admin --email <address>", run.Error, StringComparison.Ordinal);
    }

    [Fact]
    public async Task MissingConnectionString_Fails_WithAClearMessage()
    {
        var output = new StringWriter();
        var error = new StringWriter();
        var console = new CommandConsole(new StringReader($"{FirstPassword}\n{FirstPassword}\n"), inputRedirected: true, output, error);

        var exitCode = await OperationsCommands.TryRunAsync(
            ["create-system-admin", "--email", Email], new ConfigurationBuilder().Build(), console, CancellationToken.None);

        Assert.Equal(1, exitCode);
        Assert.Contains("Missing ConnectionStrings:Commerce (env: ConnectionStrings__Commerce).", error.ToString(), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("")]
    [InlineData("--urls http://localhost:5000")]
    [InlineData("serve")]
    public async Task NoArgsOrAnUnknownFirstArgument_IsNotACommand_SoTheWebHostStarts(string commandLine)
    {
        var run = await RunAsync(SplitArgs(commandLine), string.Empty);

        Assert.Null(run.ExitCode);
        Assert.Equal(string.Empty, run.Output);
        Assert.Equal(string.Empty, run.Error);
    }
}
