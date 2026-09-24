using Commerce.Updater;

namespace Commerce.Upgrade;

public sealed class UpdateDiscoveryTests : IDisposable
{
    private readonly string _manifestPath = Path.Combine(Path.GetTempPath(), $"update-manifest-{Guid.NewGuid():N}.json");
    private readonly ReleaseDiscovery _discovery = new();
    private static readonly Version LocalVersion = new(1, 0, 0);
    private static readonly UpdateEnvironment CompatibleMachine = new(19045, "x64");

    public void Dispose()
    {
        if (File.Exists(_manifestPath))
        {
            File.Delete(_manifestPath);
        }
    }

    [Fact]
    public void MissingManifest_ReturnsManifestNotConfigured()
    {
        var result = _discovery.CheckForUpdates(LocalVersion, new LocalUpdateManifestSource(_manifestPath), CompatibleMachine);

        Assert.Equal(UpdateCheckStatus.ManifestNotConfigured, result.Status);
        Assert.Equal("Manifest de updates no configurado", ReleaseDiscovery.FormatCompactStatus(result));
    }

    [Fact]
    public void SameVersion_ReturnsUpToDate()
    {
        WriteManifest("1.0.0");

        var result = _discovery.CheckForUpdates(LocalVersion, new LocalUpdateManifestSource(_manifestPath), CompatibleMachine);

        Assert.Equal(UpdateCheckStatus.UpToDate, result.Status);
        Assert.Equal("Actualizado", ReleaseDiscovery.FormatCompactStatus(result));
    }

    [Fact]
    public void HigherCompatibleVersion_ReturnsAvailable()
    {
        WriteManifest("1.1.0");

        var result = _discovery.CheckForUpdates(LocalVersion, new LocalUpdateManifestSource(_manifestPath), CompatibleMachine);

        Assert.Equal(UpdateCheckStatus.Available, result.Status);
        Assert.Equal(new Version(1, 1, 0), result.AvailableVersion);
        Assert.Equal("Update 1.1.0 disponible", ReleaseDiscovery.FormatCompactStatus(result));
    }

    [Fact]
    public void HigherVersionBelowWindowsFloor_ReturnsIncompatibleWindows()
    {
        WriteManifest("1.1.0", minimumWindowsBuild: 22621);

        var result = _discovery.CheckForUpdates(LocalVersion, new LocalUpdateManifestSource(_manifestPath), CompatibleMachine);

        Assert.Equal(UpdateCheckStatus.IncompatibleWindows, result.Status);
    }

    [Fact]
    public void UnsupportedArchitecture_ReturnsIncompatibleArchitecture()
    {
        WriteManifest("1.1.0", architectures: "arm64");

        var result = _discovery.CheckForUpdates(LocalVersion, new LocalUpdateManifestSource(_manifestPath), CompatibleMachine);

        Assert.Equal(UpdateCheckStatus.IncompatibleArchitecture, result.Status);
    }

    [Fact]
    public void InvalidManifest_ReturnsCheckFailedInvalid()
    {
        File.WriteAllText(_manifestPath, "{ not json }");

        var result = _discovery.CheckForUpdates(LocalVersion, new LocalUpdateManifestSource(_manifestPath), CompatibleMachine);

        Assert.Equal(UpdateCheckStatus.CheckFailedInvalid, result.Status);
    }

    [Fact]
    public void UnsupportedSchema_ReturnsUnsupportedSchema()
    {
        WriteManifest("1.1.0", schemaVersion: 2);

        var result = _discovery.CheckForUpdates(LocalVersion, new LocalUpdateManifestSource(_manifestPath), CompatibleMachine);

        Assert.Equal(UpdateCheckStatus.UnsupportedSchema, result.Status);
    }

    [Fact]
    public void NoMatchingPackage_ReturnsNoCompatiblePackage()
    {
        WriteManifest("1.1.0", packageArchitecture: "arm64");

        var result = _discovery.CheckForUpdates(LocalVersion, new LocalUpdateManifestSource(_manifestPath), CompatibleMachine);

        Assert.Equal(UpdateCheckStatus.NoCompatiblePackage, result.Status);
    }

    private void WriteManifest(
        string version,
        int schemaVersion = 1,
        int minimumWindowsBuild = 19041,
        string architectures = "x64",
        string packageArchitecture = "x64")
    {
        File.WriteAllText(_manifestPath, $$"""
        {
          "schemaVersion": {{schemaVersion}},
          "product": "Commerce.Pos.Windows",
          "channel": "internal",
          "version": "{{version}}",
          "releaseNotesUrl": "https://example.invalid/releases/{{version}}",
          "compatibility": {
            "minimumWindowsBuild": {{minimumWindowsBuild}},
            "architectures": ["{{architectures}}"],
            "syncContractVersion": 1,
            "schemaVersion": 1
          },
          "packages": [
            {
              "format": "msi",
              "architecture": "{{packageArchitecture}}",
              "minimumWindowsBuild": 0,
              "url": "file:///C:/updates/Commerce.Pos.Windows-{{version}}.msi",
              "sha256": "abc123",
              "publisherId": "trusted-publisher",
              "signatureRequired": true,
              "attestationRequired": true
            }
          ]
        }
        """);
    }
}
