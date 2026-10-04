using System.IO;
using System.Windows;
using Commerce.BranchNode;
using Commerce.Updater;
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
    private bool _started;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        // Logging and the global handlers come first so that even a failure
        // while building the host is recorded and shown in friendly terms.
        PosLog.Configure(new PosFileLogger(Path.Combine(PosHostBuilder.DefaultDataDirectory(), "logs")));
        InstallGlobalHandlers();

        DesktopThemeService.ApplySavedTheme();

        _host = PosHostBuilder.Build();
        _host.Start();
        var branding = _host.Services.GetRequiredService<ApplicationBranding>();

        var localInstallationStore = _host.Services.GetRequiredService<LocalInstallationStore>();
        var identity = localInstallationStore.LoadOrCreate();

        if (identity.Pairing is null)
        {
            var pairingClient = _host.Services.GetRequiredService<DevicePairingClient>();
            var pairingWindow = new PairingWindow(pairingClient, localInstallationStore, identity.InstallationId) { Title = $"{branding.MainWindowTitle} — Configurar terminal" };
            var paired = pairingWindow.ShowDialog();

            if (paired != true || pairingWindow.PairedRecord is null)
            {
                Shutdown();
                return;
            }

            identity = pairingWindow.PairedRecord;
        }

        // Nobody is signed in at startup: MainWindow opens on its lock screen (operator
        // tiles + PIN, or email + password on first run) and the sale stays out of reach
        // until an operator gets in.
        var currentOperator = _host.Services.GetRequiredService<CurrentOperator>();

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
            _host.Services.GetRequiredService<DiscountPinReplicaClient>(),
            _host.Services.GetRequiredService<StockReplicaClient>(),
            _host.Services.GetRequiredService<PriceListsReplicaClient>(),
            _host.Services.GetRequiredService<Commerce.Application.Pricing.PricingResolutionService>(),
            _host.Services.GetRequiredService<ManagementConnection>(),
            branding,
            _host.Services.GetRequiredService<UpdateChecker>(),
            _host.Services.GetRequiredService<UpdateInstallWorkflowFactory>(),
            _host.Services.GetRequiredService<PendingUpgradeStore>(),
            _host.Services.GetRequiredService<TerminalIdentityRefresher>(),
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
        _started = true;
    }

    private void InstallGlobalHandlers()
    {
        DispatcherUnhandledException += (_, args) =>
        {
            PosLog.Error("App", "Unhandled exception on the UI thread.", args.Exception);
            ShowUnexpectedError();

            if (_started)
            {
                args.Handled = true;
                return;
            }

            // Startup never finished: there is no window to fall back to and the
            // explicit shutdown mode would leave an invisible process behind.
            args.Handled = true;
            Shutdown(1);
        };

        AppDomain.CurrentDomain.UnhandledException += (_, args) =>
            PosLog.Error("App", $"Unhandled exception (terminating: {args.IsTerminating}).", args.ExceptionObject as Exception);

        TaskScheduler.UnobservedTaskException += (_, args) =>
        {
            PosLog.Error("App", "Unobserved task exception.", args.Exception);
            args.SetObserved();
        };
    }

    private static void ShowUnexpectedError()
    {
        try
        {
            MessageBox.Show(PosMessages.Unexpected, "Incoders Commerce", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        catch
        {
            // Showing the dialog must never raise a second crash.
        }
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _host?.Dispose();
        base.OnExit(e);
    }
}
