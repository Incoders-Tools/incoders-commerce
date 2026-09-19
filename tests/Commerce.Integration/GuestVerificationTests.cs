using Commerce.Cloud.Api.Email;
using Commerce.Cloud.Api.Ordering;
using Commerce.Cloud.Api.Persistence;
using Commerce.Cloud.Api.Tenancy;
using Commerce.Domain.Ordering;
using Npgsql;

namespace Commerce.Integration;

/// <summary>
/// Covers commerce-guest-ordering tasks 2.4, 2.5, 2.6, 2.8:
/// <see cref="GuestVerificationService"/> lifecycle against a LIVE Postgres
/// instance (`deploy/dev/compose.yaml`), through
/// <see cref="PostgresGuestVerificationStore"/>. Mirrors
/// <see cref="PasswordRecoveryStoreTests"/>'s convention: if compose is not
/// running, these tests report the gap clearly and return without asserting
/// pass/fail. Uses a <see cref="FakeEmailSender"/> so no test EVER reaches
/// the network (public-order-surface spec.md threat matrix "Process
/// integration").
/// </summary>
[Collection("Postgres")]
public sealed class GuestVerificationTests : IDisposable
{
    private readonly bool _postgresAvailable = PostgresTestFixture.TryPing(PostgresTestFixture.DirectConnectionString);
    private readonly NpgsqlDataSource? _dataSource;

    public GuestVerificationTests()
    {
        if (!_postgresAvailable)
        {
            return;
        }

        ApplyMigrationsAndReset();
        _dataSource = NpgsqlDataSource.Create(PostgresTestFixture.DirectConnectionString);
    }

    public void Dispose() => _dataSource?.Dispose();

    private static string RepoRoot()
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
        return dir.FullName;
    }

    private static void ApplyMigrationsAndReset()
    {
        using var owner = new NpgsqlConnection(PostgresTestFixture.OwnerConnectionString);
        owner.Open();

        var root = RepoRoot();

        var initSql = File.ReadAllText(Path.Combine(root, "deploy", "db", "migrations", "0001_init_rls.sql"))
            .Replace("__APP_RUNTIME_PASSWORD__", "dev-only-password");
        using (var cmd = new NpgsqlCommand(initSql, owner)) cmd.ExecuteNonQuery();

        foreach (var file in new[]
                 {
                     "0002_users.sql", "0003_organizations_branches.sql",
                     "0004_device_credentials.sql", "0005_password_recovery.sql",
                     "0010_guest_ordering.sql",
                 })
        {
            var sql = File.ReadAllText(Path.Combine(root, "deploy", "db", "migrations", file));
            using var cmd = new NpgsqlCommand(sql, owner);
            cmd.ExecuteNonQuery();
        }

        using var resetCmd = new NpgsqlCommand(
            "TRUNCATE TABLE guest_order_verifications, organizations CASCADE", owner);
        resetCmd.ExecuteNonQuery();
    }

    private static CloudTenantScope SeedOrganization()
    {
        var organizationId = Guid.NewGuid();
        using var owner = new NpgsqlConnection(PostgresTestFixture.OwnerConnectionString);
        owner.Open();
        using var cmd = new NpgsqlCommand("INSERT INTO organizations (id, name) VALUES ($1, 'Org A')", owner);
        cmd.Parameters.AddWithValue(organizationId);
        cmd.ExecuteNonQuery();
        return new CloudTenantScope(organizationId);
    }

    /// <summary>
    /// Records every message it would have sent, in memory, and NEVER
    /// touches the network — the sole guarantee the threat matrix requires.
    /// </summary>
    private sealed class FakeEmailSender : IEmailSender
    {
        public List<EmailMessage> Sent { get; } = [];
        public bool NextResult { get; set; } = true;

        public Task<bool> SendAsync(EmailMessage message, CancellationToken ct)
        {
            Sent.Add(message);
            return Task.FromResult(NextResult);
        }
    }

    private static string ExtractCode(EmailMessage message)
    {
        // The service's composed text body always contains a 6-digit code
        // BEFORE the first period ("...code is 123456. This code expires...");
        // extracting it here (rather than hardcoding "000000") proves the
        // assertion is coupled to the REAL generated code, not a fake value.
        // Digits elsewhere in the body ("expires in 10 minutes") must NOT be
        // captured, hence anchoring on the segment before the first '.'.
        var firstSentence = message.TextBody[..message.TextBody.IndexOf('.')];
        var digits = new string(firstSentence.Where(char.IsDigit).ToArray());
        Assert.True(digits.Length == 6, $"Expected a 6-digit code in the email body, got: {message.TextBody}");
        return digits;
    }

    [Fact]
    public async Task RequestAsync_IssuesRow_AndSendsEmail_WithSixDigitCode()
    {
        if (!_postgresAvailable) { Console.WriteLine("SKIPPED: no live Postgres."); return; }

        var scope = SeedOrganization();
        var sender = new FakeEmailSender();
        var store = new PostgresGuestVerificationStore(_dataSource!);
        var service = new GuestVerificationService(store, sender);

        var verificationId = await service.RequestAsync(
            scope, "30111222333", GuestContactChannel.Email, "guest@example.com", CancellationToken.None);

        Assert.NotEqual(Guid.Empty, verificationId);
        Assert.Single(sender.Sent);
        Assert.Equal("guest@example.com", sender.Sent[0].To);

        var record = await store.FindAsync(verificationId, CancellationToken.None);
        Assert.NotNull(record);
        Assert.Equal(scope.OrganizationId, record!.OrganizationId);
        Assert.Equal("30111222333", record.DocumentId);
        Assert.Equal("guest@example.com", record.ContactAddress);
        Assert.Equal(0, record.AttemptCount);
        Assert.Null(record.ConfirmedAt);
        Assert.Null(record.ConsumedAt);
        // The code is NEVER persisted in plaintext (threat matrix / design.md
        // "Verification state shape") — only a SHA-256 hex hash is stored,
        // which is 64 lowercase hex characters and structurally cannot equal
        // the 6-digit code the email carried.
        var code = ExtractCode(sender.Sent[0]);
        Assert.NotEqual(code, record.CodeHash);
        Assert.Equal(64, record.CodeHash.Length);
    }

    [Fact]
    public async Task ConfirmAsync_WrongCode_IncrementsAttemptCount_AndReturnsInvalidOrExpired()
    {
        if (!_postgresAvailable) { Console.WriteLine("SKIPPED: no live Postgres."); return; }

        var scope = SeedOrganization();
        var sender = new FakeEmailSender();
        var store = new PostgresGuestVerificationStore(_dataSource!);
        var service = new GuestVerificationService(store, sender);
        var verificationId = await service.RequestAsync(
            scope, "30111222333", GuestContactChannel.Email, "guest@example.com", CancellationToken.None);

        var result = await service.ConfirmAsync(verificationId, "000000-wrong", CancellationToken.None);

        Assert.Equal(GuestVerificationConfirmResult.InvalidOrExpired, result);
        var record = await store.FindAsync(verificationId, CancellationToken.None);
        Assert.Equal(1, record!.AttemptCount);
        Assert.Null(record.ConfirmedAt);
    }

    [Fact]
    public async Task ConfirmAsync_CorrectCode_Succeeds_AndSetsConfirmedAt()
    {
        if (!_postgresAvailable) { Console.WriteLine("SKIPPED: no live Postgres."); return; }

        var scope = SeedOrganization();
        var sender = new FakeEmailSender();
        var store = new PostgresGuestVerificationStore(_dataSource!);
        var service = new GuestVerificationService(store, sender);
        var verificationId = await service.RequestAsync(
            scope, "30111222333", GuestContactChannel.Email, "guest@example.com", CancellationToken.None);
        var code = ExtractCode(sender.Sent[0]);

        var result = await service.ConfirmAsync(verificationId, code, CancellationToken.None);

        Assert.Equal(GuestVerificationConfirmResult.Confirmed, result);
        var record = await store.FindAsync(verificationId, CancellationToken.None);
        Assert.NotNull(record!.ConfirmedAt);
        Assert.Equal(0, record.AttemptCount);
    }

    [Fact]
    public async Task ConfirmAsync_FifthWrongAttempt_BurnsTheRow_SixthAttemptStillFails()
    {
        if (!_postgresAvailable) { Console.WriteLine("SKIPPED: no live Postgres."); return; }

        var scope = SeedOrganization();
        var sender = new FakeEmailSender();
        var store = new PostgresGuestVerificationStore(_dataSource!);
        var service = new GuestVerificationService(store, sender);
        var verificationId = await service.RequestAsync(
            scope, "30111222333", GuestContactChannel.Email, "guest@example.com", CancellationToken.None);
        var realCode = ExtractCode(sender.Sent[0]);

        for (var i = 0; i < 5; i++)
        {
            var wrong = await service.ConfirmAsync(verificationId, "999999", CancellationToken.None);
            Assert.Equal(GuestVerificationConfirmResult.InvalidOrExpired, wrong);
        }

        var record = await store.FindAsync(verificationId, CancellationToken.None);
        Assert.Equal(5, record!.AttemptCount);

        // The row is burned: even the CORRECT code now fails the same way.
        var withRealCode = await service.ConfirmAsync(verificationId, realCode, CancellationToken.None);
        Assert.Equal(GuestVerificationConfirmResult.InvalidOrExpired, withRealCode);
    }

    [Fact]
    public async Task ConfirmAsync_ExpiredRow_ReturnsInvalidOrExpired_SameAsWrongCode()
    {
        if (!_postgresAvailable) { Console.WriteLine("SKIPPED: no live Postgres."); return; }

        var scope = SeedOrganization();
        var sender = new FakeEmailSender();
        var store = new PostgresGuestVerificationStore(_dataSource!);
        var now = DateTimeOffset.UtcNow;
        var clockBox = new[] { now };
        var service = new GuestVerificationService(store, sender, () => clockBox[0]);
        var verificationId = await service.RequestAsync(
            scope, "30111222333", GuestContactChannel.Email, "guest@example.com", CancellationToken.None);
        var realCode = ExtractCode(sender.Sent[0]);

        clockBox[0] = now.AddMinutes(10).AddSeconds(1); // past the 10-minute expiry

        var result = await service.ConfirmAsync(verificationId, realCode, CancellationToken.None);

        Assert.Equal(GuestVerificationConfirmResult.InvalidOrExpired, result);
    }

    [Fact]
    public async Task ConfirmAsync_AlreadyConsumedRow_ReturnsInvalidOrExpired_SameAsWrongCode()
    {
        if (!_postgresAvailable) { Console.WriteLine("SKIPPED: no live Postgres."); return; }

        var scope = SeedOrganization();
        var sender = new FakeEmailSender();
        var store = new PostgresGuestVerificationStore(_dataSource!);
        var service = new GuestVerificationService(store, sender);
        var verificationId = await service.RequestAsync(
            scope, "30111222333", GuestContactChannel.Email, "guest@example.com", CancellationToken.None);
        var realCode = ExtractCode(sender.Sent[0]);
        Assert.Equal(GuestVerificationConfirmResult.Confirmed, await service.ConfirmAsync(verificationId, realCode, CancellationToken.None));
        var consumed = await service.TryConsumeAsync(
            verificationId, "30111222333", "guest@example.com", Guid.NewGuid(), CancellationToken.None);
        Assert.True(consumed);

        var result = await service.ConfirmAsync(verificationId, realCode, CancellationToken.None);

        Assert.Equal(GuestVerificationConfirmResult.InvalidOrExpired, result);
    }

    [Fact]
    public async Task ConfirmAsync_UnknownVerificationId_ReturnsInvalidOrExpired_SameAsWrongCode()
    {
        if (!_postgresAvailable) { Console.WriteLine("SKIPPED: no live Postgres."); return; }

        var sender = new FakeEmailSender();
        var store = new PostgresGuestVerificationStore(_dataSource!);
        var service = new GuestVerificationService(store, sender);

        var result = await service.ConfirmAsync(Guid.NewGuid(), "123456", CancellationToken.None);

        Assert.Equal(GuestVerificationConfirmResult.InvalidOrExpired, result);
    }

    [Fact]
    public async Task TryConsumeAsync_MismatchedContact_Fails_AndLeavesRowUnconsumed()
    {
        if (!_postgresAvailable) { Console.WriteLine("SKIPPED: no live Postgres."); return; }

        var scope = SeedOrganization();
        var sender = new FakeEmailSender();
        var store = new PostgresGuestVerificationStore(_dataSource!);
        var service = new GuestVerificationService(store, sender);
        var verificationId = await service.RequestAsync(
            scope, "30111222333", GuestContactChannel.Email, "guest@example.com", CancellationToken.None);
        var realCode = ExtractCode(sender.Sent[0]);
        await service.ConfirmAsync(verificationId, realCode, CancellationToken.None);

        var consumed = await service.TryConsumeAsync(
            verificationId, "30111222333", "someone-else@example.com", Guid.NewGuid(), CancellationToken.None);

        Assert.False(consumed);
        var record = await store.FindAsync(verificationId, CancellationToken.None);
        Assert.Null(record!.ConsumedAt);
    }

    [Fact]
    public async Task RequestAsync_SupersedesPriorUnconsumedRow_ForSameContact()
    {
        if (!_postgresAvailable) { Console.WriteLine("SKIPPED: no live Postgres."); return; }

        var scope = SeedOrganization();
        var sender = new FakeEmailSender();
        var store = new PostgresGuestVerificationStore(_dataSource!);
        var service = new GuestVerificationService(store, sender);
        var firstId = await service.RequestAsync(
            scope, "30111222333", GuestContactChannel.Email, "guest@example.com", CancellationToken.None);
        var firstCode = ExtractCode(sender.Sent[0]);

        await service.RequestAsync(
            scope, "30111222333", GuestContactChannel.Email, "guest@example.com", CancellationToken.None);

        // The FIRST verification is superseded (consumed_at set) by the
        // second request for the same contact — design.md "same tx:
        // supersede prior unconsumed rows for the same contact" — so it can
        // no longer be confirmed even with its own correct code.
        var result = await service.ConfirmAsync(firstId, firstCode, CancellationToken.None);
        Assert.Equal(GuestVerificationConfirmResult.InvalidOrExpired, result);
    }

    [Fact]
    public async Task RequestAsync_FailingEmailSender_StillReturnsVerificationId_AndStillIssuesRow()
    {
        if (!_postgresAvailable) { Console.WriteLine("SKIPPED: no live Postgres."); return; }

        var scope = SeedOrganization();
        var sender = new FakeEmailSender { NextResult = false };
        var store = new PostgresGuestVerificationStore(_dataSource!);
        var service = new GuestVerificationService(store, sender);

        var verificationId = await service.RequestAsync(
            scope, "30111222333", GuestContactChannel.Email, "guest@example.com", CancellationToken.None);

        Assert.NotEqual(Guid.Empty, verificationId);
        var record = await store.FindAsync(verificationId, CancellationToken.None);
        Assert.NotNull(record);
        // The row was issued regardless of the send failure — the same
        // uniform-outcome precedent as reset-password/request.
    }
}
