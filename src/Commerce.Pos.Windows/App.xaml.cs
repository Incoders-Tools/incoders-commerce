using System.Windows;
using Commerce.BranchNode;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace Commerce.Pos.Windows;

/// <summary>
/// Composition root entry point (design.md "Data Flow" — PAIRING). If the
/// terminal is unpaired (fresh install, or a decrypt failure treated as
/// unpaired), <see cref="PairingWindow"/> is shown modally FIRST; on cancel,
/// the app shuts down — an unpaired terminal has no branch to sell into.
/// </summary>
// Fully qualified: this namespace (Commerce.Pos.Windows) sits under the
// enclosing Commerce namespace alongside Commerce.Application, so the bare
// name "Application" resolves to that sibling namespace before System.Windows.
public partial class App : System.Windows.Application
{
    private IHost? _host;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        _host = PosHostBuilder.Build();
        _host.Start();

        var localInstallationStore = _host.Services.GetRequiredService<LocalInstallationStore>();
        var identity = localInstallationStore.LoadOrCreate();

        if (identity.Pairing is null)
        {
            var pairingClient = _host.Services.GetRequiredService<DevicePairingClient>();
            var pairingWindow = new PairingWindow(pairingClient, localInstallationStore, identity.InstallationId);
            var paired = pairingWindow.ShowDialog();

            if (paired != true || pairingWindow.PairedRecord is null)
            {
                Shutdown();
                return;
            }

            identity = pairingWindow.PairedRecord;
        }

        // Unlike PairingWindow, cancel/close of OperatorLoginWindow does NOT
        // Shutdown(): an unidentified operator still has a branch to sell
        // into (design.md "Cancel does not shut down; actorId is total").
        // Both "Continue without operator" and cancel/close leave
        // CurrentOperator unset, and MainWindow opens regardless.
        var currentOperator = _host.Services.GetRequiredService<CurrentOperator>();
        var operatorLoginWindow = new OperatorLoginWindow(
            _host.Services.GetRequiredService<OperatorProvisioningClient>(),
            _host.Services.GetRequiredService<LocalOperatorStore>(),
            identity.Pairing!.DeviceToken);

        var loggedIn = operatorLoginWindow.ShowDialog();
        if (loggedIn == true && operatorLoginWindow.ActiveOperator is not null)
        {
            currentOperator.Set(operatorLoginWindow.ActiveOperator);
        }

        var mainWindow = new MainWindow(
            _host.Services.GetRequiredService<BranchSyncStore>(),
            _host.Services.GetRequiredService<BranchNodeService>(),
            _host.Services.GetRequiredService<CloudSyncClient>(),
            _host.Services.GetRequiredService<DevicePairingClient>(),
            _host.Services.GetRequiredService<OperatorProvisioningClient>(),
            localInstallationStore,
            _host.Services.GetRequiredService<LocalOperatorStore>(),
            currentOperator,
            _host.Services.GetRequiredService<CustomerReplicaClient>(),
            _host.Services.GetRequiredService<CatalogPriceReplicaClient>(),
            _host.Services.GetRequiredService<Commerce.Application.Pricing.PricingResolutionService>(),
            _host.Services.GetRequiredService<Func<CustomerAdminClient>>(),
            identity);

        // ShutdownMode is OnExplicitShutdown (App.xaml) specifically so that
        // PairingWindow.Close() above (when it was shown) does not drop the
        // open-window count to zero and shut the whole app down BEFORE
        // MainWindow ever appears. Now that MainWindow exists, hand shutdown
        // control back to the normal "closing the main window exits the app"
        // behavior.
        MainWindow = mainWindow;
        ShutdownMode = ShutdownMode.OnMainWindowClose;
        mainWindow.Show();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _host?.Dispose();
        base.OnExit(e);
    }
}
