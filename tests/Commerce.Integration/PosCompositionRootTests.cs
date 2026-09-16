using Commerce.Application.Access;
using Commerce.Application.Audit;
using Commerce.BranchNode;
using Commerce.Pos.Windows;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;

namespace Commerce.Integration;

/// <summary>
/// Covers Unit 4 task 4.1: the WPF composition root (design.md "BranchNode
/// hosting") is the only genuinely unit-testable surface of Commerce.Pos.Windows
/// — this proves <see cref="PosHostBuilder"/> resolves the reused Application
/// services and the local sync client from a real <c>HostApplicationBuilder</c>,
/// without launching any WPF window.
/// </summary>
public sealed class PosCompositionRootTests : IDisposable
{
    private readonly string _dataDirectory;

    public PosCompositionRootTests()
    {
        _dataDirectory = Path.Combine(Path.GetTempPath(), "commerce-pos-tests", Guid.NewGuid().ToString());
    }

    [Fact]
    public void Build_Resolves_BranchNodeService()
    {
        using var host = PosHostBuilder.Build(_dataDirectory);

        var branchNodeService = host.Services.GetRequiredService<BranchNodeService>();

        Assert.NotNull(branchNodeService);
    }

    [Fact]
    public void Build_Resolves_TenantAuthorizationService()
    {
        using var host = PosHostBuilder.Build(_dataDirectory);

        var authorizationService = host.Services.GetRequiredService<TenantAuthorizationService>();

        Assert.NotNull(authorizationService);
    }

    [Fact]
    public void Build_Resolves_IAuditSink()
    {
        using var host = PosHostBuilder.Build(_dataDirectory);

        var auditSink = host.Services.GetRequiredService<IAuditSink>();

        Assert.NotNull(auditSink);
    }

    [Fact]
    public void Build_Resolves_BranchSyncStore_BackedBySqliteFileUnderDataDirectory()
    {
        using var host = PosHostBuilder.Build(_dataDirectory);

        var store = host.Services.GetRequiredService<BranchSyncStore>();

        Assert.NotNull(store);
        Assert.True(File.Exists(Path.Combine(_dataDirectory, "branch.db")));
    }

    [Fact]
    public void Build_Resolves_CloudSyncClient()
    {
        using var host = PosHostBuilder.Build(_dataDirectory);

        var syncClient = host.Services.GetRequiredService<CloudSyncClient>();

        Assert.NotNull(syncClient);
    }

    [Fact]
    public void Build_Resolves_MainWindowDependencies_AsSingletons()
    {
        using var host = PosHostBuilder.Build(_dataDirectory);

        var first = host.Services.GetRequiredService<BranchNodeService>();
        var second = host.Services.GetRequiredService<BranchNodeService>();

        Assert.Same(first, second);
    }

    public void Dispose()
    {
        // BranchSyncStore holds a SQLite connection that pools the file handle
        // even after Dispose(); clear pools so the temp directory can be
        // removed deterministically between test runs.
        SqliteConnection.ClearAllPools();

        if (Directory.Exists(_dataDirectory))
        {
            Directory.Delete(_dataDirectory, recursive: true);
        }
    }
}
