using Commerce.Pos.Windows;

namespace Commerce.Integration;

/// <summary>
/// Covers commerce-pos-user-login tasks 1.5/1.6 (design.md "LocalOperatorStore
/// file and DPAPI scope"): `operators.json` mirrors `LocalInstallationStore`'s
/// exact tolerant-decrypt contract — a corrupted/undecryptable verifier makes
/// only that entry silently absent, truncated/garbage JSON yields an empty
/// list, a missing file yields an empty list, and none of these ever throw.
/// </summary>
public sealed class LocalOperatorStoreTests : IDisposable
{
    private readonly string _filePath = Path.Combine(Path.GetTempPath(), "commerce-pos-tests", Guid.NewGuid().ToString(), "operators.json");

    public LocalOperatorStoreTests()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_filePath)!);
    }

    public void Dispose()
    {
        var dir = Path.GetDirectoryName(_filePath)!;
        if (Directory.Exists(dir))
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    private static CachedOperator MakeOperator(string email, int permissions = 0) => new(
        Guid.NewGuid(), email, Guid.NewGuid(),
        OperatorPinCredential.Derive("482913").Salt,
        OperatorPinCredential.Derive("482913").Subkey,
        DateTimeOffset.UtcNow,
        permissions);

    [Fact]
    public void Load_MissingFile_YieldsEmptyList()
    {
        var store = new LocalOperatorStore(_filePath);

        var operators = store.Load();

        Assert.Empty(operators);
    }

    [Fact]
    public void Upsert_ThenLoad_RoundTripsOneOperator()
    {
        var store = new LocalOperatorStore(_filePath);
        var op = MakeOperator("alice@example.com");

        store.Upsert(op);
        var reloaded = store.Load();

        Assert.Single(reloaded);
        Assert.Equal(op.UserId, reloaded[0].UserId);
        Assert.Equal(op.Email, reloaded[0].Email);
        Assert.Equal(op.OrganizationId, reloaded[0].OrganizationId);
        Assert.Equal(op.Salt, reloaded[0].Salt);
        Assert.Equal(op.Subkey, reloaded[0].Subkey);
    }

    /// <summary>
    /// Covers commerce-customer-identity Unit 6 task 6.3 (design.md "Desktop
    /// authorization for customer create/edit"): the server-derived
    /// `Permissions` int survives the store round trip alongside the
    /// PIN-verifier fields, so the terminal can show/hide the "Manage
    /// customers" button without re-verifying.
    /// </summary>
    [Fact]
    public void Upsert_ThenLoad_RoundTripsPermissions()
    {
        var store = new LocalOperatorStore(_filePath);
        var op = MakeOperator("alice@example.com", permissions: 7);

        store.Upsert(op);
        var reloaded = store.Load();

        Assert.Single(reloaded);
        Assert.Equal(7, reloaded[0].Permissions);
    }

    [Fact]
    public void Upsert_MultipleOperators_Coexist()
    {
        var store = new LocalOperatorStore(_filePath);
        var opA = MakeOperator("alice@example.com");
        var opB = MakeOperator("bob@example.com");

        store.Upsert(opA);
        store.Upsert(opB);
        var reloaded = store.Load();

        Assert.Equal(2, reloaded.Count);
        Assert.Contains(reloaded, o => o.UserId == opA.UserId);
        Assert.Contains(reloaded, o => o.UserId == opB.UserId);
    }

    [Fact]
    public void Upsert_SameUserId_ReplacesInPlace_DoesNotDuplicate()
    {
        var store = new LocalOperatorStore(_filePath);
        var op = MakeOperator("alice@example.com");
        store.Upsert(op);

        var updated = op with { LastVerifiedUtc = op.LastVerifiedUtc.AddDays(1) };
        store.Upsert(updated);
        var reloaded = store.Load();

        Assert.Single(reloaded);
        Assert.Equal(updated.LastVerifiedUtc, reloaded[0].LastVerifiedUtc);
    }

    [Fact]
    public void Remove_DeletesOnlyTargetedEntry()
    {
        var store = new LocalOperatorStore(_filePath);
        var opA = MakeOperator("alice@example.com");
        var opB = MakeOperator("bob@example.com");
        store.Upsert(opA);
        store.Upsert(opB);

        store.Remove(opA.UserId);
        var reloaded = store.Load();

        Assert.Single(reloaded);
        Assert.Equal(opB.UserId, reloaded[0].UserId);
    }

    [Fact]
    public void TouchVerified_UpdatesOnlyThatEntrysLastVerifiedUtc()
    {
        var store = new LocalOperatorStore(_filePath);
        var opA = MakeOperator("alice@example.com");
        var opB = MakeOperator("bob@example.com");
        store.Upsert(opA);
        store.Upsert(opB);

        var newTimestamp = DateTimeOffset.UtcNow.AddDays(2);
        store.TouchVerified(opA.UserId, newTimestamp);
        var reloaded = store.Load();

        var reloadedA = reloaded.Single(o => o.UserId == opA.UserId);
        var reloadedB = reloaded.Single(o => o.UserId == opB.UserId);
        Assert.Equal(newTimestamp, reloadedA.LastVerifiedUtc);
        Assert.Equal(opB.LastVerifiedUtc, reloadedB.LastVerifiedUtc);
    }

    [Fact]
    public void Load_TamperedCiphertextOnOneEntry_ThatEntryIsSilentlyAbsent_OthersUnaffected()
    {
        var store = new LocalOperatorStore(_filePath);
        var opA = MakeOperator("alice@example.com");
        var opB = MakeOperator("bob@example.com");
        store.Upsert(opA);
        store.Upsert(opB);

        // Corrupt the first entry's protected verifier field directly in the
        // persisted JSON, mirroring LocalInstallationStoreTests' corruption
        // technique for EncryptedDeviceToken.
        var json = File.ReadAllText(_filePath);
        const string marker = "\"ProtectedVerifier\":\"";
        var firstIndex = json.IndexOf(marker, StringComparison.Ordinal);
        var corrupted = string.Concat(
            json.AsSpan(0, firstIndex + marker.Length),
            "zzzz-corrupted-",
            json.AsSpan(firstIndex + marker.Length));
        File.WriteAllText(_filePath, corrupted);

        var exception = Record.Exception(() => { store.Load(); });

        Assert.Null(exception);
        var reloaded = store.Load();
        Assert.Single(reloaded);
        Assert.Equal(opB.UserId, reloaded[0].UserId);
    }

    [Fact]
    public void Load_TruncatedGarbageJson_YieldsEmptyList_NeverThrows()
    {
        var store = new LocalOperatorStore(_filePath);
        store.Upsert(MakeOperator("alice@example.com"));
        File.WriteAllText(_filePath, "{ this is not valid json at all ][");

        var exception = Record.Exception(() => { store.Load(); });

        Assert.Null(exception);
        Assert.Empty(store.Load());
    }
}
