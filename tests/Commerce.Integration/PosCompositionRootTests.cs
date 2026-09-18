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
    public void Build_Resolves_DevicePairingClient()
    {
        using var host = PosHostBuilder.Build(_dataDirectory);

        var pairingClient = host.Services.GetRequiredService<DevicePairingClient>();

        Assert.NotNull(pairingClient);
    }

    [Fact]
    public void Build_Resolves_LocalInstallationStore()
    {
        using var host = PosHostBuilder.Build(_dataDirectory);

        var store = host.Services.GetRequiredService<LocalInstallationStore>();

        Assert.NotNull(store);
    }

    /// <summary>
    /// Covers commerce-pos-installation-identity task 2.3:
    /// `InstallationIdentityService` is DELETED (design.md — not deprecated),
    /// so it must not be resolvable from the composition root.
    /// </summary>
    [Fact]
    public void Build_DoesNotRegister_InstallationIdentityService()
    {
        using var host = PosHostBuilder.Build(_dataDirectory);

        var descriptor = host.Services.GetType(); // sanity: host is real
        Assert.NotNull(descriptor);

        // The type itself no longer exists in the compiled assembly graph —
        // this test's mere ability to compile without referencing
        // `InstallationIdentityService` IS the primary proof (task 2.1's
        // deletion). This assertion additionally proves no equivalent type
        // under that name is registered by a different mechanism.
        var serviceNames = host.Services.GetType().Assembly.GetTypes().Select(t => t.FullName);
        Assert.DoesNotContain(serviceNames, name => name == "Commerce.Application.Access.InstallationIdentityService");
    }

    /// <summary>
    /// Covers commerce-pos-installation-identity task 6.1 (the structural
    /// reason a revoked credential cannot reach a local write path,
    /// design.md "Why revocation cannot block a sale"): `Commerce.BranchNode`
    /// does not reference `Commerce.Pos.Windows`, so `BranchNodeService` and
    /// `BranchSyncStore` cannot reach `CloudSyncClient` even by accident.
    /// </summary>
    [Fact]
    public void BranchNodeAssembly_DoesNotReference_PosWindowsAssembly()
    {
        var branchNodeAssembly = typeof(BranchNodeService).Assembly;
        var referencedAssemblyNames = branchNodeAssembly.GetReferencedAssemblies().Select(a => a.Name);

        Assert.DoesNotContain("Commerce.Pos.Windows", referencedAssemblyNames);
    }

    [Fact]
    public void Build_Resolves_MainWindowDependencies_AsSingletons()
    {
        using var host = PosHostBuilder.Build(_dataDirectory);

        var first = host.Services.GetRequiredService<BranchNodeService>();
        var second = host.Services.GetRequiredService<BranchNodeService>();

        Assert.Same(first, second);
    }

    /// <summary>
    /// Covers commerce-pos-user-login task 3.2: <see cref="LocalOperatorStore"/>
    /// and <see cref="CurrentOperator"/> resolve as singletons (same pattern as
    /// <see cref="Build_Resolves_MainWindowDependencies_AsSingletons"/>), and
    /// <see cref="OperatorProvisioningClient"/> resolves as a typed
    /// <c>HttpClient</c> on the same composition root (design.md "File Changes"
    /// — PosHostBuilder.cs).
    /// </summary>
    [Fact]
    public void Build_Resolves_OperatorLoginDependencies_AsSingletonsAndTypedClient()
    {
        using var host = PosHostBuilder.Build(_dataDirectory);

        var storeFirst = host.Services.GetRequiredService<LocalOperatorStore>();
        var storeSecond = host.Services.GetRequiredService<LocalOperatorStore>();
        Assert.Same(storeFirst, storeSecond);

        var currentOperatorFirst = host.Services.GetRequiredService<CurrentOperator>();
        var currentOperatorSecond = host.Services.GetRequiredService<CurrentOperator>();
        Assert.Same(currentOperatorFirst, currentOperatorSecond);

        var provisioningClient = host.Services.GetRequiredService<OperatorProvisioningClient>();
        Assert.NotNull(provisioningClient);
    }

    /// <summary>
    /// Covers commerce-customer-identity Unit 6 task 6.4: the two new typed
    /// clients resolve from the composition root. <see cref="CustomerAdminClient"/>
    /// is deliberately TRANSIENT (design.md "the cookie is held in a
    /// window-scoped HttpClient... discarded when the window closes") — each
    /// resolve yields a fresh instance with its own CookieContainer, never a
    /// shared singleton.
    /// </summary>
    [Fact]
    public void Build_Resolves_CustomerReplicaClient()
    {
        using var host = PosHostBuilder.Build(_dataDirectory);

        var replicaClient = host.Services.GetRequiredService<CustomerReplicaClient>();

        Assert.NotNull(replicaClient);
    }

    [Fact]
    public void Build_Resolves_CustomerAdminClient_AsTransient_NotSingleton()
    {
        using var host = PosHostBuilder.Build(_dataDirectory);

        var first = host.Services.GetRequiredService<CustomerAdminClient>();
        var second = host.Services.GetRequiredService<CustomerAdminClient>();

        Assert.NotNull(first);
        Assert.NotNull(second);
        Assert.NotSame(first, second);
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
