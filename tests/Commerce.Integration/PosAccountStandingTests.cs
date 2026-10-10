using System.Net;
using System.Text;
using System.Text.RegularExpressions;
using Commerce.Application.Access;
using Commerce.Application.Audit;
using Commerce.BranchNode;
using Commerce.Pos.Windows;

namespace Commerce.Integration;

/// <summary>
/// odd/tasks/organization-account-standing.md T8, POS side: the standing INPUTS reach the terminal on every sync sweep
/// (`GET /device/organization/settings`), are kept in `branch.db` as the last known value, and the terminal evaluates
/// them with the same domain rule on its own business day, so the red footer notice keeps counting offline. The POS
/// only warns: nothing here blocks a sale (ADR-002). Every signed-in operator sees it; the closing line depends on the
/// role.
/// </summary>
public sealed class PosAccountStandingTests : IDisposable
{
    private static readonly DateOnly Today = new(2026, 10, 9);
    private readonly string _dbPath = Path.Combine(Path.GetTempPath(), $"branch-standing-{Guid.NewGuid():N}.db");

    private string ConnectionString => $"Data Source={_dbPath}";

    public void Dispose()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        foreach (var path in new[] { _dbPath, _dbPath + "-wal", _dbPath + "-shm" })
        {
            if (File.Exists(path))
            {
                try { File.Delete(path); } catch (IOException) { }
            }
        }
    }

    private sealed class StubHandler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(respond(request));
    }

    private static HttpResponseMessage Json(string body) =>
        new(HttpStatusCode.OK) { Content = new StringContent(body, Encoding.UTF8, "application/json") };

    private static OrganizationSettingsReplicaClient Client(Func<string> body) =>
        new(new HttpClient(new StubHandler(_ => Json(body()))) { BaseAddress = new Uri("http://stub.local") });

    // ---- store --------------------------------------------------------------

    [Fact]
    public void Store_NeverSynced_HasNoStanding()
    {
        using var store = new BranchSyncStore(ConnectionString);

        Assert.Null(store.GetAccountStanding());
    }

    [Fact]
    public void Store_KeepsTheLastKnownStanding_AcrossAReopen()
    {
        using (var store = new BranchSyncStore(ConnectionString))
        {
            store.ApplyAccountStanding(new AccountStandingReplica(null, 30, false));
            store.ApplyAccountStanding(new AccountStandingReplica(new DateOnly(2026, 10, 8), 45, true));
        }

        using var reopened = new BranchSyncStore(ConnectionString);
        Assert.Equal(new AccountStandingReplica(new DateOnly(2026, 10, 8), 45, true), reopened.GetAccountStanding());
    }

    // ---- client -------------------------------------------------------------

    [Fact]
    public async Task Pull_ReadsTheStandingInputs()
    {
        var outcome = await Client(() => """{"quantityDecimalSeparator":"Comma","accountStanding":{"dueOn":"2026-10-08","graceDays":30,"suspended":false}}""")
            .PullAsync("t");

        Assert.True(outcome.Success);
        Assert.Equal(new AccountStandingReplica(new DateOnly(2026, 10, 8), 30, false), outcome.AccountStanding);
    }

    [Fact]
    public async Task Pull_FromAServerThatPredatesTheStanding_StillSucceeds_WithoutOne()
    {
        var outcome = await Client(() => """{"quantityDecimalSeparator":"Dot"}""").PullAsync("t");

        Assert.True(outcome.Success);
        Assert.Equal("Dot", outcome.QuantityDecimalSeparator);
        Assert.Null(outcome.AccountStanding);
    }

    // ---- sweep --------------------------------------------------------------

    private static SyncRunner NewRunner(BranchSyncStore store, OrganizationSettingsReplicaClient settings)
    {
        var auditSink = new InMemoryAuditSink();
        var service = new BranchNodeService(store, new TenantAuthorizationService(auditSink), auditSink);
        var unreachable = new HttpClient { BaseAddress = new Uri("http://127.0.0.1:1"), Timeout = TimeSpan.FromSeconds(2) };
        var pairing = new DevicePairing(Guid.NewGuid(), Guid.NewGuid(), "Branch", "op@example.com", "token");
        return new SyncRunner(
            store, service, new CloudSyncClient(unreachable), new CustomerReplicaClient(unreachable),
            new CatalogPriceReplicaClient(unreachable), new OperatorProvisioningClient(unreachable),
            new LocalOperatorStore(Path.Combine(Path.GetTempPath(), $"operators-{Guid.NewGuid():N}.json")),
            () => pairing, organizationSettingsReplicaClient: settings);
    }

    [Fact]
    public async Task TheSweep_StoresTheStanding_AndAnOlderServerLeavesTheLastKnownOne()
    {
        var body = """{"quantityDecimalSeparator":"Comma","accountStanding":{"dueOn":"2026-10-08","graceDays":30,"suspended":true}}""";
        using var store = new BranchSyncStore(ConnectionString);
        var runner = NewRunner(store, Client(() => body));

        await runner.RunAsync(SyncTrigger.Startup);
        Assert.Equal(new AccountStandingReplica(new DateOnly(2026, 10, 8), 30, true), store.GetAccountStanding());

        body = """{"quantityDecimalSeparator":"Comma"}""";
        await runner.RunAsync(SyncTrigger.Timer);
        Assert.Equal(new AccountStandingReplica(new DateOnly(2026, 10, 8), 30, true), store.GetAccountStanding());
    }

    // ---- notice -------------------------------------------------------------

    [Fact]
    public void NoStandingYet_OrActive_ShowsNothing()
    {
        Assert.Null(AccountStandingNotice.Compose(null, Today, isAdmin: true));
        Assert.Null(AccountStandingNotice.Compose(new AccountStandingReplica(null, 30, false), Today, isAdmin: true));
        Assert.Null(AccountStandingNotice.Compose(new AccountStandingReplica(new DateOnly(2026, 10, 9), 30, false), Today, isAdmin: false));
    }

    [Fact]
    public void Overdue_TellsACashierToInformTheAdministrator_WithTheSuspensionDateInTheTooltip()
    {
        var notice = AccountStandingNotice.Compose(new AccountStandingReplica(new DateOnly(2026, 10, 8), 30, false), Today, isAdmin: false);

        Assert.Equal("Pago pendiente: el acceso web se suspende en 30 días. Informe al administrador.", notice!.Text);
        Assert.Equal("Fecha de suspensión: 08/11/2026", notice.ToolTip);
    }

    [Fact]
    public void Overdue_TellsAnAdministratorToContactIncoders_InSingularOnTheLastDay()
    {
        var notice = AccountStandingNotice.Compose(new AccountStandingReplica(new DateOnly(2026, 10, 8), 30, false), new DateOnly(2026, 11, 7), isAdmin: true);

        Assert.Equal("Pago pendiente: el acceso web se suspende en 1 día. Comuníquese con Incoders.", notice!.Text);
    }

    [Fact]
    public void TheCountdownMovesWithTheBusinessDay_WithoutASync()
    {
        var inputs = new AccountStandingReplica(new DateOnly(2026, 10, 8), 30, false);

        Assert.Contains("en 30 días", AccountStandingNotice.Compose(inputs, Today, isAdmin: false)!.Text);
        Assert.Contains("en 29 días", AccountStandingNotice.Compose(inputs, Today.AddDays(1), isAdmin: false)!.Text);
        Assert.StartsWith("Acceso web suspendido", AccountStandingNotice.Compose(inputs, new DateOnly(2026, 11, 8), isAdmin: false)!.Text);
    }

    [Theory]
    [InlineData(false, "Acceso web suspendido por pago pendiente. Informe al administrador.")]
    [InlineData(true, "Acceso web suspendido por pago pendiente. Comuníquese con Incoders.")]
    public void Suspended_ByHand_SaysWebAccess_NotTheService_BecauseThePosKeepsSelling(bool isAdmin, string expected)
    {
        var notice = AccountStandingNotice.Compose(new AccountStandingReplica(null, 30, true), Today, isAdmin);

        Assert.Equal(expected, notice!.Text);
        Assert.Null(notice.ToolTip);
    }

    [Fact]
    public void BadInputsFromTheWire_NeverThrow_TheyJustShowNothing()
    {
        Assert.Null(AccountStandingNotice.Compose(new AccountStandingReplica(new DateOnly(2026, 10, 8), 500, false), Today, isAdmin: false));
    }

    // ---- shell --------------------------------------------------------------

    private static string Read(string file)
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "Commerce.sln"))) directory = directory.Parent;
        return File.ReadAllText(Path.Combine(directory!.FullName, "src", "Commerce.Pos.Windows", file));
    }

    [Fact]
    public void TheNotice_IsARedPillInTheFooter_RefreshedAfterEverySync_OnSignInAndOnLock()
    {
        var xaml = Read("MainWindow.xaml");
        var notice = Regex.Match(xaml, @"<Border[^>]*x:Name=""AccountStandingPill""[^>]*>", RegexOptions.Singleline);
        Assert.True(notice.Success, "AccountStandingPill not found");
        Assert.Contains("DangerSurfaceBrush", notice.Value);
        Assert.Contains(@"Visibility=""Collapsed""", notice.Value);
        // In the footer (the status bar row), not in a screen that may be hidden.
        Assert.True(xaml.IndexOf(@"x:Name=""AccountStandingPill""", StringComparison.Ordinal) > xaml.IndexOf(@"x:Name=""LockHost""", StringComparison.Ordinal));

        var code = Read("MainWindow.xaml.cs");
        Assert.Contains("await Dispatcher.InvokeAsync(RefreshAccountStandingNotice);", code);
        Assert.Matches(@"private void ApplyLockState\(\)[\s\S]*?RefreshAccountStandingNotice\(\);", code);
        Assert.Matches(@"private void LockScreen_SignedIn\([\s\S]*?RefreshAccountStandingNotice\(\);", code);
    }
}
