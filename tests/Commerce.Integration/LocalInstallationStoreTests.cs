using System.Text;
using Commerce.Pos.Windows;

namespace Commerce.Integration;

/// <summary>
/// Covers commerce-pos-installation-identity task 5.2: plain xUnit, no
/// Postgres. `LocalInstallationStore` round-trips a paired identity, a
/// fresh/first run yields an unpaired record with a fresh `InstallationId`,
/// re-pairing preserves the SAME `InstallationId` (it's a terminal label,
/// never an authorization input), and a decrypt failure (e.g. the file was
/// copied to a different machine/user) is treated identically to "no valid
/// credential" — it must never throw.
/// </summary>
public sealed class LocalInstallationStoreTests : IDisposable
{
    private readonly string _filePath = Path.Combine(Path.GetTempPath(), "commerce-pos-tests", Guid.NewGuid().ToString(), "installation.json");

    public LocalInstallationStoreTests()
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

    [Fact]
    public void LoadOrCreate_FirstRun_YieldsFreshInstallationId_WithNoPairing()
    {
        var store = new LocalInstallationStore(_filePath);

        var record = store.LoadOrCreate();

        Assert.NotEqual(Guid.Empty, record.InstallationId);
        Assert.Null(record.Pairing);
    }

    [Fact]
    public void SaveThenLoad_RoundTrips_PairingData()
    {
        var store = new LocalInstallationStore(_filePath);
        var first = store.LoadOrCreate();

        var pairing = new DevicePairing(
            Guid.NewGuid(), Guid.NewGuid(), "Main", "operator@example.com", "device-token-plaintext");
        store.Save(new LocalInstallationRecord(first.InstallationId, pairing));

        var reloaded = store.LoadOrCreate();

        Assert.Equal(first.InstallationId, reloaded.InstallationId);
        Assert.NotNull(reloaded.Pairing);
        Assert.Equal(pairing.OrganizationId, reloaded.Pairing!.OrganizationId);
        Assert.Equal(pairing.BranchId, reloaded.Pairing.BranchId);
        Assert.Equal(pairing.BranchName, reloaded.Pairing.BranchName);
        Assert.Equal(pairing.OperatorEmail, reloaded.Pairing.OperatorEmail);
        Assert.Equal(pairing.DeviceToken, reloaded.Pairing.DeviceToken);
    }

    [Fact]
    public void Save_EncryptsDeviceTokenAtRest_NeverPlaintextOnDisk()
    {
        var store = new LocalInstallationStore(_filePath);
        var first = store.LoadOrCreate();

        const string plaintextToken = "super-secret-device-token-value";
        store.Save(new LocalInstallationRecord(
            first.InstallationId,
            new DevicePairing(Guid.NewGuid(), Guid.NewGuid(), "Main", "operator@example.com", plaintextToken)));

        var rawFileContents = File.ReadAllText(_filePath);

        Assert.DoesNotContain(plaintextToken, rawFileContents);
    }

    [Fact]
    public void Repair_OverwritesPairing_ButPreservesSameInstallationId()
    {
        var store = new LocalInstallationStore(_filePath);
        var first = store.LoadOrCreate();

        store.Save(new LocalInstallationRecord(
            first.InstallationId,
            new DevicePairing(Guid.NewGuid(), Guid.NewGuid(), "Original Branch", "op1@example.com", "token-1")));

        // Re-pair: same store, new pairing.
        var beforeRepair = store.LoadOrCreate();
        store.Save(new LocalInstallationRecord(
            beforeRepair.InstallationId,
            new DevicePairing(Guid.NewGuid(), Guid.NewGuid(), "New Branch", "op2@example.com", "token-2")));

        var afterRepair = store.LoadOrCreate();

        Assert.Equal(first.InstallationId, afterRepair.InstallationId);
        Assert.Equal("New Branch", afterRepair.Pairing!.BranchName);
        Assert.Equal("token-2", afterRepair.Pairing.DeviceToken);
    }

    [Fact]
    public void LoadOrCreate_CorruptedCiphertext_IsTreatedAsNoValidCredential_NeverThrows()
    {
        var store = new LocalInstallationStore(_filePath);
        var first = store.LoadOrCreate();
        store.Save(new LocalInstallationRecord(
            first.InstallationId,
            new DevicePairing(Guid.NewGuid(), Guid.NewGuid(), "Main", "operator@example.com", "some-token")));

        // Simulate installation.json copied to a different machine/user:
        // DPAPI (CurrentUser scope) can never decrypt this on this run, since
        // we corrupt the encrypted payload directly.
        var json = File.ReadAllText(_filePath);
        var corrupted = json.Replace("\"EncryptedDeviceToken\":\"", "\"EncryptedDeviceToken\":\"zzzz-corrupted-");
        File.WriteAllText(_filePath, corrupted);

        var exception = Record.Exception(() => store.LoadOrCreate());

        Assert.Null(exception);
        var reloaded = store.LoadOrCreate();
        Assert.Null(reloaded.Pairing);
        // InstallationId (the terminal label) survives even a corrupted
        // credential — only the pairing/credential is discarded.
        Assert.Equal(first.InstallationId, reloaded.InstallationId);
    }
}
