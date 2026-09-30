using System.Diagnostics;
using Commerce.Updater;

namespace Commerce.Upgrade;

/// <summary>
/// Proves the manifest emitted by deploy/release/new-release-manifest.ps1 (the
/// script the release workflow runs) is understood by ReleaseDiscovery.
/// </summary>
public sealed class ReleaseManifestGeneratorTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), $"release-manifest-{Guid.NewGuid():N}");
    private readonly ReleaseDiscovery _discovery = new();

    public ReleaseManifestGeneratorTests() => Directory.CreateDirectory(_dir);

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    [Fact]
    public void GeneratedStableManifest_ResolvesAsAvailableCompatibleUpdate()
    {
        var manifestPath = Generate("1.4.0", "stable", "1.4.0", "CN=Incoders Commerce (Interim)");
        if (manifestPath is null) return; // no PowerShell on this host

        var result = _discovery.CheckForUpdates(
            new Version(1, 3, 0), new LocalUpdateManifestSource(manifestPath), new UpdateEnvironment(19045, "x64"));

        Assert.Equal(UpdateCheckStatus.Available, result.Status);
        Assert.Equal(new Version(1, 4, 0), result.AvailableVersion);
        var package = Assert.IsType<UpdatePackage>(result.Package);
        Assert.Equal(PackageFormat.Msix, package.Format);
        Assert.Equal("x64", package.Architecture);
        Assert.Equal("CN=Incoders Commerce (Interim)", package.PublisherId);
        Assert.Equal(new string('a', 64), package.Sha256);
        Assert.Equal(
            "https://github.com/Incoders-Tools/incoders-commerce/releases/download/v1.4.0/Commerce.Pos.Windows-1.4.0-win-x64.msix",
            package.Url);
        Assert.True(package.SignatureRequired);
    }

    [Fact]
    public void GeneratedInternalManifest_UsesFourPartVersionAndChannel()
    {
        var manifestPath = Generate("1.4.0-internal.3", "internal", "1.4.0.3", "CN=Incoders Commerce (Interim)");
        if (manifestPath is null) return;

        var result = _discovery.CheckForUpdates(
            new Version(1, 4, 0), new LocalUpdateManifestSource(manifestPath), new UpdateEnvironment(19045, "x64"));

        Assert.Equal(UpdateCheckStatus.Available, result.Status);
        Assert.Equal(new Version(1, 4, 0, 3), result.AvailableVersion);
        Assert.Contains("\"channel\": \"internal\"", File.ReadAllText(manifestPath));
    }

    [Fact]
    public void GeneratedManifest_OnOldWindows_IsIncompatible()
    {
        var manifestPath = Generate("1.4.0", "stable", "1.4.0", "CN=Incoders Commerce (Interim)");
        if (manifestPath is null) return;

        var result = _discovery.CheckForUpdates(
            new Version(1, 3, 0), new LocalUpdateManifestSource(manifestPath), new UpdateEnvironment(18363, "x64"));

        Assert.Equal(UpdateCheckStatus.IncompatibleWindows, result.Status);
    }

    private string? Generate(string tag, string channel, string manifestVersion, string publisher)
    {
        var shell = FindPowerShell();
        if (shell is null) return null;

        var msixName = $"Commerce.Pos.Windows-{manifestVersion}-win-x64.msix";
        var buildInfo = Path.Combine(_dir, "build-info.json");
        File.WriteAllText(buildInfo, $$"""
            {"file":"{{msixName}}","version":"{{manifestVersion}}","msixVersion":"{{manifestVersion}}","channel":"{{channel}}",
             "publisher":"{{publisher}}","sha256":"{{new string('a', 64)}}","sizeBytes":1234}
            """);
        var output = Path.Combine(_dir, "commerce-pos-release-manifest.json");
        var script = Path.Combine(FindRepositoryRoot(), "deploy", "release", "new-release-manifest.ps1");

        var start = new ProcessStartInfo(shell)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false
        };
        foreach (var arg in new[]
        {
            "-NoProfile", "-File", script,
            "-BuildInfoPath", buildInfo,
            "-Repository", "Incoders-Tools/incoders-commerce",
            "-Tag", $"v{tag}",
            "-OutputPath", output
        })
        {
            start.ArgumentList.Add(arg);
        }

        using var process = Process.Start(start)!;
        var stdout = process.StandardOutput.ReadToEnd();
        var stderr = process.StandardError.ReadToEnd();
        process.WaitForExit();
        Assert.True(process.ExitCode == 0, $"generator failed: {stdout}{stderr}");
        return output;
    }

    private static string? FindPowerShell()
    {
        foreach (var name in new[] { "pwsh", "powershell" })
        {
            var exe = OperatingSystem.IsWindows() ? name + ".exe" : name;
            var found = (Environment.GetEnvironmentVariable("PATH") ?? string.Empty)
                .Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries)
                .Select(dir => Path.Combine(dir, exe))
                .FirstOrDefault(File.Exists);
            if (found is not null) return found;
        }
        return null;
    }

    private static string FindRepositoryRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "Commerce.sln")))
        {
            dir = dir.Parent;
        }
        return dir?.FullName ?? throw new InvalidOperationException("Commerce.sln not found above the test output.");
    }
}
