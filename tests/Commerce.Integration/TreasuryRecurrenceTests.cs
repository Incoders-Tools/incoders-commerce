using Commerce.Cloud.Api.Endpoints;
using Commerce.Cloud.Api.Persistence;
using Commerce.Cloud.Api.Tenancy;
using Commerce.Domain.Identity;
using Commerce.Domain.Treasury;
using Npgsql;
using static Commerce.Integration.StockTestSeed;

namespace Commerce.Integration;

/// <summary>
/// Recurring treasury movements (0051): the dates of a schedule (weekly, monthly with the day clamped to short months,
/// yearly, every N, and its end) and their recording: each date once however often it runs, from today unless the past
/// dates were asked for, a voided one never again, a pause skipping its dates, an edit changing only what comes next.
/// </summary>
[Collection("Postgres")]
public sealed class TreasuryRecurrenceTests : IDisposable
{
    private readonly bool _postgresAvailable = PostgresTestFixture.TryPing(PostgresTestFixture.DirectConnectionString);
    private readonly NpgsqlDataSource? _dataSource;

    public TreasuryRecurrenceTests()
    {
        if (!_postgresAvailable) return;
        using (var owner = OpenOwner())
        {
            var dir = Path.Combine(PostgresTestFixture.RepoRoot(), "deploy", "db", "migrations");
            foreach (var file in Directory.GetFiles(dir, "*.sql").OrderBy(Path.GetFileName))
            {
                PostgresTestFixture.ApplyMigration(owner, Path.GetFileName(file));
            }
        }

        _dataSource = NpgsqlDataSource.Create(PostgresTestFixture.DirectConnectionString);
    }

    public void Dispose() => _dataSource?.Dispose();

    private static RecurrenceSchedule Schedule(DateOnly start, string frequency, int interval = 1, string end = RecurrenceEnd.Never, DateOnly? endDate = null, int? max = null) =>
        new(start, frequency, interval, end, endDate, max);

    [Fact]
    public void AMonthlyDate_KeepsItsDay_AndAShortMonthTakesItsLastDay()
    {
        var rent = Schedule(new DateOnly(2026, 1, 31), RecurrenceFrequency.Monthly);
        Assert.Equal(
            [new DateOnly(2026, 1, 31), new DateOnly(2026, 2, 28), new DateOnly(2026, 3, 31), new DateOnly(2026, 4, 30)],
            rent.OccurrencesUntil(new DateOnly(2026, 4, 30)));

        var everyTwoMonths = Schedule(new DateOnly(2026, 11, 10), RecurrenceFrequency.Monthly, interval: 2);
        Assert.Equal([new DateOnly(2026, 11, 10), new DateOnly(2027, 1, 10), new DateOnly(2027, 3, 10)], everyTwoMonths.OccurrencesUntil(new DateOnly(2027, 3, 31)));

        var weekly = Schedule(new DateOnly(2026, 10, 5), RecurrenceFrequency.Weekly);
        Assert.Equal(new DateOnly(2026, 10, 19), weekly.OccurrenceAt(2));
        var leap = Schedule(new DateOnly(2028, 2, 29), RecurrenceFrequency.Yearly);
        Assert.Equal(new DateOnly(2029, 2, 28), leap.OccurrenceAt(1));
    }

    [Fact]
    public void ARecurrence_EndsOnADate_OrAfterACount_OrNever()
    {
        var start = new DateOnly(2026, 1, 10);
        Assert.Equal(3, Schedule(start, RecurrenceFrequency.Monthly, end: RecurrenceEnd.AfterCount, max: 3).OccurrencesUntil(new DateOnly(2030, 1, 1)).Count());
        Assert.Equal(new DateOnly(2026, 6, 10),
            Schedule(start, RecurrenceFrequency.Monthly, end: RecurrenceEnd.OnDate, endDate: new DateOnly(2026, 6, 20)).OccurrencesUntil(new DateOnly(2030, 1, 1)).Last());
        Assert.Null(Schedule(start, RecurrenceFrequency.Monthly, end: RecurrenceEnd.AfterCount, max: 2).NextAfter(new DateOnly(2026, 3, 1)));
        Assert.Equal(new DateOnly(2026, 11, 10), Schedule(start, RecurrenceFrequency.Monthly).NextAfter(new DateOnly(2026, 10, 10)));

        Assert.True(Schedule(start, RecurrenceFrequency.Monthly).IsValid());
        Assert.False(Schedule(start, RecurrenceFrequency.Monthly, interval: 0).IsValid());
        Assert.False(Schedule(start, RecurrenceFrequency.Monthly, end: RecurrenceEnd.OnDate, endDate: start.AddDays(-1)).IsValid());
        Assert.False(Schedule(start, RecurrenceFrequency.Monthly, end: RecurrenceEnd.AfterCount).IsValid());
    }

    private async Task<(CloudTenantScope Scope, Guid Actor, Guid Bank)> SeedAsync()
    {
        var userStore = new PostgresUserAccountStore(_dataSource!);
        var (org, branch, actor) = (Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid());
        Assert.Equal(BootstrapOutcome.Created, await new PostgresOrganizationStore(_dataSource!, userStore).TryCreateBootstrapAsync(
            new CloudTenantScope(org), new NewOrganization(org, "Recurring Org"), new NewBranch(branch, "Arrecifes"),
            new NewUserAccount(actor, $"rec-{org}@example.com", "hash", [branch], [new RoleDto("admin", Permission.ManageUsers)]),
            CancellationToken.None));
        var scope = new CloudTenantScope(org);
        var (_, bank) = await new PostgresTreasuryStore(_dataSource!).CreateAccountAsync(
            scope, actor, PostgresTreasuryStore.Bank, "Banco", null, null, null, new DateOnly(2026, 1, 1), CancellationToken.None);
        return (scope, actor, bank!.AccountId);
    }

    private static TreasuryRecurrenceInput Electricity(Guid bank, decimal amount = 45_000m, bool past = false, string end = RecurrenceEnd.Never, int? max = null) =>
        new(bank, "Out", amount, "Luz (Edenor)", null, RecurrenceFrequency.Monthly, 1, new DateOnly(2026, 7, 10), end, null, max, past);

    private decimal Balance(CloudTenantScope scope, Guid bank, DateOnly today) =>
        new PostgresTreasuryStore(_dataSource!).ListAccountsAsync(scope, null, today, CancellationToken.None).GetAwaiter().GetResult()
            .Single(a => a.AccountId == bank).Balance;

    [Fact]
    public async Task EachDate_IsRecordedOnce_FromToday_OrFromTheStartWhenAsked()
    {
        if (!_postgresAvailable) { Console.WriteLine("SKIPPED: no live Postgres."); return; }
        var (scope, actor, bank) = await SeedAsync();
        var store = new PostgresTreasuryRecurrenceStore(_dataSource!);
        var today = new DateOnly(2026, 10, 10);

        // From today: July, August and September are not recorded, October 10 is.
        await store.CreateAsync(scope, actor, Electricity(bank), today, CancellationToken.None);
        // The same expense with its past dates: July to October.
        await store.CreateAsync(scope, actor, Electricity(bank, 10_000m, past: true), today, CancellationToken.None);
        Assert.Equal(-45_000m - 40_000m, Balance(scope, bank, today));

        // Running again, on the same day or by the job, records nothing twice; a month later, one more each.
        Assert.Equal(0, await store.GenerateDueForOrganizationAsync(scope.OrganizationId, today, CancellationToken.None));
        Assert.Equal(2, await store.GenerateDueForOrganizationAsync(scope.OrganizationId, new DateOnly(2026, 11, 15), CancellationToken.None));

        var list = await store.ListAsync(scope, new DateOnly(2026, 11, 15), CancellationToken.None);
        var light = list.Single(r => r.Amount == 45_000m);
        Assert.Equal((2, new DateOnly(2026, 11, 10), new DateOnly(2026, 12, 10)), (light.OccurrencesRecorded, light.LastRecordedOn!.Value, light.NextOn!.Value));
    }

    [Fact]
    public async Task AVoidedDate_IsNotRecordedAgain_APauseSkipsItsDates_AndAnEditChangesWhatComesNext()
    {
        if (!_postgresAvailable) { Console.WriteLine("SKIPPED: no live Postgres."); return; }
        var (scope, actor, bank) = await SeedAsync();
        var store = new PostgresTreasuryRecurrenceStore(_dataSource!);
        var treasury = new PostgresTreasuryStore(_dataSource!);
        var (_, id) = await store.CreateAsync(scope, actor, Electricity(bank), new DateOnly(2026, 7, 10), CancellationToken.None);

        // July recorded; it is voided (paid apart): it never comes back.
        var july = (await treasury.ListMovementsAsync(scope, bank, null, null, CancellationToken.None))!.Single();
        Assert.Equal(id, july.RecurrenceId);
        Assert.True(july.CanVoid && july.CanEdit);
        Assert.Equal(TreasuryWriteOutcome.Done, await treasury.VoidAsync(scope, actor, july.MovementId, "Pagada en efectivo", CancellationToken.None));
        Assert.Equal(0, await store.GenerateDueForOrganizationAsync(scope.OrganizationId, new DateOnly(2026, 7, 31), CancellationToken.None));

        // Paused in August, resumed in October: August and September are skipped.
        await store.SetActiveAsync(scope, actor, id!.Value, false, new DateOnly(2026, 8, 1), CancellationToken.None);
        Assert.Equal(0, await store.GenerateDueForOrganizationAsync(scope.OrganizationId, new DateOnly(2026, 9, 30), CancellationToken.None));
        await store.SetActiveAsync(scope, actor, id.Value, true, new DateOnly(2026, 10, 1), CancellationToken.None);
        Assert.Equal(1, await store.GenerateDueForOrganizationAsync(scope.OrganizationId, new DateOnly(2026, 10, 15), CancellationToken.None));

        // The rate goes up: November takes the new amount, October keeps the old one.
        Assert.Equal(RecurrenceWriteOutcome.Done, await store.UpdateAsync(scope, actor, id.Value, Electricity(bank, 52_000m), new DateOnly(2026, 10, 15), CancellationToken.None));
        await store.GenerateDueForOrganizationAsync(scope.OrganizationId, new DateOnly(2026, 11, 10), CancellationToken.None);
        var amounts = (await treasury.ListMovementsAsync(scope, bank, null, null, CancellationToken.None))!
            .Where(m => !m.Voided).OrderBy(m => m.BusinessDate).Select(m => (m.BusinessDate, m.Amount));
        Assert.Equal([(new DateOnly(2026, 10, 10), 45_000m), (new DateOnly(2026, 11, 10), 52_000m)], amounts);

        // Its schedule is fixed once dates were recorded.
        var weekly = Electricity(bank, 52_000m) with { Frequency = RecurrenceFrequency.Weekly };
        Assert.Equal(RecurrenceWriteOutcome.ScheduleLocked, await store.UpdateAsync(scope, actor, id.Value, weekly, new DateOnly(2026, 11, 10), CancellationToken.None));

        using var owner = OpenOwner();
        Assert.Equal(1L, Scalar<long>(owner, "SELECT count(*) FROM audit_log WHERE action = 'treasury.recurrence_updated' AND entity_id = $1", id.Value));
    }

    [Fact]
    public async Task ARecurrenceThatEndsAfterACount_StopsRecording()
    {
        if (!_postgresAvailable) { Console.WriteLine("SKIPPED: no live Postgres."); return; }
        var (scope, actor, bank) = await SeedAsync();
        var store = new PostgresTreasuryRecurrenceStore(_dataSource!);
        await store.CreateAsync(scope, actor, Electricity(bank, past: true, end: RecurrenceEnd.AfterCount, max: 3), new DateOnly(2026, 7, 10), CancellationToken.None);

        Assert.Equal(2, await store.GenerateDueForOrganizationAsync(scope.OrganizationId, new DateOnly(2027, 12, 31), CancellationToken.None));
        Assert.Null((await store.ListAsync(scope, new DateOnly(2027, 12, 31), CancellationToken.None)).Single().NextOn);
    }
}
