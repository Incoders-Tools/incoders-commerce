using System.IO;
using Commerce.Application.Access;
using Commerce.Application.Audit;
using Commerce.BranchNode;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace Commerce.Pos.Windows;

/// <summary>
/// Composition root for the WPF process (design.md "BranchNode hosting").
/// Wires <see cref="BranchSyncStore"/>, <see cref="BranchNodeService"/>,
/// <see cref="TenantAuthorizationService"/>, <see cref="IAuditSink"/>, and
/// <see cref="CloudSyncClient"/> via <see cref="HostApplicationBuilder"/>.
/// Extracted from App.xaml.cs so it is unit-testable without launching WPF
/// (Component Reuse Policy: reuses Application/BranchNode services as-is).
/// </summary>
public static class PosHostBuilder
{
    public static IHost Build(string? branchDataDirectory = null)
    {
        var builder = Host.CreateApplicationBuilder();

        var dataDirectory = branchDataDirectory ?? DefaultDataDirectory();
        Directory.CreateDirectory(dataDirectory);
        var databasePath = Path.Combine(dataDirectory, "branch.db");

        var cloudApiBaseUrl = builder.Configuration["Commerce:CloudApiBaseUrl"] ?? "http://localhost:8080";

        builder.Services.AddSingleton(_ => new BranchSyncStore($"Data Source={databasePath}"));
        builder.Services.AddSingleton<IAuditSink, InMemoryAuditSink>();
        builder.Services.AddSingleton<TenantAuthorizationService>();
        builder.Services.AddSingleton<BranchNodeService>();
        builder.Services.AddSingleton(_ => new LocalInstallationStore(Path.Combine(dataDirectory, "installation.json")));

        builder.Services.AddHttpClient<CloudSyncClient>(client =>
        {
            client.BaseAddress = new Uri(cloudApiBaseUrl);
        });
        builder.Services.AddHttpClient<DevicePairingClient>(client =>
        {
            client.BaseAddress = new Uri(cloudApiBaseUrl);
        });

        // MainWindow is NOT registered here: it requires an already-paired
        // LocalInstallationRecord, which App.xaml.cs resolves via
        // PairingWindow before MainWindow can be constructed (design.md
        // "Data Flow" — "MainWindow is not created; there is no branch to
        // sell into" when unpaired).

        return builder.Build();
    }

    private static string DefaultDataDirectory() => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "Incoders",
        "Commerce");
}
