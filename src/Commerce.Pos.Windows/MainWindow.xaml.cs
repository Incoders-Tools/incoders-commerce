using System.Collections.ObjectModel;
using System.Globalization;
using System.Linq;
using System.Reflection;
using System.Windows;
using System.Windows.Input;
using Commerce.Application.Pricing;
using Commerce.BranchNode;
using Commerce.Domain.CashSessions;
using Commerce.Domain.Discounts;
using Commerce.Domain.Identity;
using Commerce.Domain.Sales;
using Commerce.Pos.Windows.Controls;
using Commerce.Updater;
using Commerce.Domain.Sync;

namespace Commerce.Pos.Windows;

/// <summary>
/// Minimal but functionally real shell (design.md "Data Flow"): proves the
/// app launches, BranchNode initializes in-process, an offline sale lands in
/// the local SQLite branch.db, and sync against Cloud.Api is visibly
/// attempted and its outcome shown.
///
/// Sync-blocked-not-sales-blocked (structural, design.md): `DeviceToken` is
/// read in exactly ONE expression — the `PushAsync` call inside
/// <see cref="SyncButton_Click"/>. <see cref="CommitSaleButton_Click"/> never
/// reads it and never calls <see cref="CloudSyncClient"/> — a revoked
/// credential therefore cannot reach the local-write path even in principle.
/// </summary>
public partial class MainWindow : Window
{
    private readonly BranchSyncStore _store;
    private readonly BranchNodeService _branchNodeService;
    private readonly CloudSyncClient _syncClient;
    private readonly DevicePairingClient _pairingClient;
    private readonly OperatorProvisioningClient _operatorProvisioningClient;
    private readonly LocalInstallationStore _localInstallationStore;
    private readonly LocalOperatorStore _localOperatorStore;
    private readonly CurrentOperator _currentOperator;
    private readonly OperatorSessionActions _operatorSession;
    private readonly CustomerReplicaClient _customerReplicaClient;
    private readonly CatalogPriceReplicaClient _catalogPriceReplicaClient;
    private readonly IDiscountAuthorizer _discountAuthorizer;
    private readonly PricingResolutionService _pricingResolutionService;
    private readonly Func<CustomerAdminClient> _customerAdminClientFactory;
    private readonly Func<UserAdminClient> _userAdminClientFactory;
    private readonly ApplicationBranding _branding;
    private readonly UpdateChecker _updateChecker;
    private readonly UpdateInstallWorkflowFactory _updateWizardFactory;
    private readonly PendingUpgradeReport _upgradeReport;
    private readonly Version _localVersion;
    private UpdateCheckResult _updateCheckResult;
    private readonly SingleFlight _updateCheckFlight = new();
    private readonly Guid _installationId;
    private readonly TerminalIdentityRefresher _terminalIdentityRefresher;
    private readonly SaleCart _cart;
    private readonly ObservableCollection<ProductCardViewModel> _catalogCards = new();
    private readonly System.Windows.Threading.DispatcherTimer _searchDebounce;
    private readonly SyncRunner _syncRunner;
    private readonly SyncScheduler _syncScheduler;
    private DevicePairing _pairing;
    private Guid? _selectedCategoryId;
    private bool _railRefreshing;
    private string _lastSyncResult = "Sincronización lista.";
    private CashSession? _cashSession;
    private bool _openCashPrompted;
    private readonly ShellNavigation _shell = new();
    private readonly SectionLifecycle _sections = new();
    private readonly LockScreenView _lockScreen;
    private bool _locked;

    public MainWindow(
        BranchSyncStore store,
        BranchNodeService branchNodeService,
        CloudSyncClient syncClient,
        DevicePairingClient pairingClient,
        OperatorProvisioningClient operatorProvisioningClient,
        LocalInstallationStore localInstallationStore,
        LocalOperatorStore localOperatorStore,
        CurrentOperator currentOperator,
        CustomerReplicaClient customerReplicaClient,
        CatalogPriceReplicaClient catalogPriceReplicaClient,
        DiscountPinReplicaClient discountPinReplicaClient,
        PricingResolutionService pricingResolutionService,
        Func<CustomerAdminClient> customerAdminClientFactory,
        Func<UserAdminClient> userAdminClientFactory,
        ApplicationBranding branding,
        UpdateChecker updateChecker,
        UpdateInstallWorkflowFactory updateWizardFactory,
        PendingUpgradeStore pendingUpgradeStore,
        TerminalIdentityRefresher terminalIdentityRefresher,
        LocalInstallationRecord identity)
    {
        InitializeComponent();
        _sections.DetachedReleased += OnDetachedSectionReleased;

        _store = store;
        _branchNodeService = branchNodeService;
        _syncClient = syncClient;
        _pairingClient = pairingClient;
        _operatorProvisioningClient = operatorProvisioningClient;
        _localInstallationStore = localInstallationStore;
        _localOperatorStore = localOperatorStore;
        _currentOperator = currentOperator;
        _operatorSession = new OperatorSessionActions(currentOperator);
        _customerReplicaClient = customerReplicaClient;
        _catalogPriceReplicaClient = catalogPriceReplicaClient;
        _pricingResolutionService = pricingResolutionService;
        _customerAdminClientFactory = customerAdminClientFactory;
        _userAdminClientFactory = userAdminClientFactory;
        _branding = branding;
        _updateChecker = updateChecker;
        _updateWizardFactory = updateWizardFactory;
        _terminalIdentityRefresher = terminalIdentityRefresher;
        _localVersion = ReadLocalVersion();
        _updateCheckResult = new UpdateCheckResult(UpdateCheckStatus.Checking, _localVersion);
        // The previous run may have handed an update to Windows: report how it ended.
        _upgradeReport = pendingUpgradeStore.ResolveOnStartup(_localVersion);
        Title = branding.MainWindowTitle;
        _installationId = identity.InstallationId;
        _pairing = identity.Pairing
            ?? throw new InvalidOperationException("MainWindow requires an already-paired identity; App.xaml.cs must pair first.");

        _cart = new SaleCart(_pricingResolutionService);
        // Discounts are authorized with the branch PIN cached in branch.db; the
        // branch is read through the pairing so a re-pair is followed.
        _discountAuthorizer = new BranchPinDiscountAuthorizer(_store, () => _pairing.BranchId);
        SaleTable.ItemsSource = _cart.Lines;
        CatalogCardsItemsControl.ItemsSource = _catalogCards;
        _searchDebounce = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromMilliseconds(200) };
        _searchDebounce.Tick += (_, _) =>
        {
            _searchDebounce.Stop();
            RefreshCatalogCards();
            RefreshScannedTotal();
        };

        // Task 4.6: ONE SyncRunner shared by every trigger (startup, the
        // scheduler's 60s sweep, the post-sale nudge, and the manual
        // button) — reentrancy-guarded by construction, never duplicated.
        _syncRunner = new SyncRunner(
            _store, _branchNodeService, _syncClient, _customerReplicaClient, _catalogPriceReplicaClient,
            _operatorProvisioningClient, _localOperatorStore, () => _pairing, discountPinReplicaClient);
        _syncScheduler = new SyncScheduler(RunSyncAsync);

        // The lock screen is the first thing the window shows: nobody is signed in yet.
        _lockScreen = new LockScreenView(
            new LockScreenModel(operatorProvisioningClient, localOperatorStore, () => _pairing.DeviceToken),
            () => _pairing.BranchName);
        _lockScreen.SignedIn += LockScreen_SignedIn;
        LockHost.Content = _lockScreen;

        RefreshIdentityText();
        RefreshStatus();
        RefreshCustomerPicker();
        RefreshCategoryRail();
        RefreshCatalogCards();
        RefreshScannedTotal();
        RefreshCatalogFreshness();
        RefreshCashSession();

        // pos-cash-session: the open-cash prompt is offered when the first operator
        // signs in (see LockScreen_SignedIn); a session left open by a previous run
        // simply resumes (no prompt).

        // Fire-and-forget: never awaited by the constructor (design.md Data
        // Flow — the sale path, and window startup, never await a sync).
        _ = _syncScheduler.StartAsync();
        _ = RunUpdateCheckAsync();
        _ = RefreshTerminalIdentityAsync();
    }

    /// <summary>
    /// A terminal paired before registers existed (or one that was offline when it paired) learns its
    /// branch code and register number from the server. Fire-and-forget like the other startup work:
    /// offline it simply stays unknown and the sale path keeps working.
    /// </summary>
    private async Task RefreshTerminalIdentityAsync()
    {
        try
        {
            var current = _pairing;
            var refresh = await _terminalIdentityRefresher.EnsureAsync(_installationId, current);
            // Apply only what was actually saved, and only to the pairing it was computed for.
            if (refresh.Persisted && ReferenceEquals(_pairing, current))
            {
                _pairing = refresh.Pairing;
                RefreshIdentityText();
            }
        }
        catch (Exception ex)
        {
            PosLog.Error("App", "Could not refresh the terminal identity.", ex);
        }
    }

    /// <summary>
    /// Runs the release check off the UI thread and refreshes the footer when
    /// it completes. It can never throw into the UI and never blocks startup
    /// or a sale: any failure is the typed "could not check" status.
    /// </summary>
    private Task RunUpdateCheckAsync() =>
        // A manual check during the startup check awaits that check and then
        // shows its result, instead of returning while "Comprobando..." stays.
        _updateCheckFlight.RunAsync(RunUpdateCheckCoreAsync);

    private async Task RunUpdateCheckCoreAsync()
    {
        _updateCheckResult = new UpdateCheckResult(UpdateCheckStatus.Checking, _localVersion);
        RefreshStatus();
        try
        {
            _updateCheckResult = await Task.Run(() => _updateChecker.CheckAsync(_localVersion));
        }
        catch (Exception ex)
        {
            _updateCheckResult = new UpdateCheckResult(UpdateCheckStatus.CheckFailedInvalid, _localVersion, Detail: ex.Message);
        }

        RefreshStatus();
    }

    /// <summary>
    /// Reads the terminal's open cash session and reflects it: the header state,
    /// "Cerrar Caja", and the lock over the sale screen while none is open.
    /// </summary>
    private void RefreshCashSession()
    {
        _cashSession = _branchNodeService.GetOpenCashSession();
        var isOpen = _cashSession is not null;
        NavBar.SetCashSession(CashSessionInput.HeaderText(_cashSession), isOpen);
        SaleScreen.IsEnabled = isOpen;
        CashClosedOverlay.Visibility = isOpen || _shell.Current != ShellSection.Sale ? Visibility.Collapsed : Visibility.Visible;
    }

    private void OpenCashButton_Click(object sender, RoutedEventArgs e) => PromptOpenCash();

    /// <summary>
    /// Asks for the opening float (naming the signed-in operator) and opens the session.
    /// Cancelling leaves the sale screen
    /// locked behind the "Abrir caja" prompt. Never reads the device credential.
    /// </summary>
    private void PromptOpenCash()
    {
        if (_currentOperator.Value is not { } signedIn || !CashSessionInput.CanPromptOpenCash(signedIn.Email))
        {
            // Nobody is signed in: the prompt never opens without an operator; make sure the lock screen is what shows.
            ApplyLockState();
            return;
        }

        var window = new OpenCashWindow(signedIn.Email) { Owner = this };
        if (window.ShowDialog() == true && window.OpeningFloat is { } openingFloat && _currentOperator.Value is not null)
        {
            var result = _branchNodeService.OpenCashSession(
                _pairing.OrganizationId, _pairing.BranchId, _currentOperator.ResolveActorId(_installationId), openingFloat, Guid.NewGuid());
            CashClosedMessageText.Text = result.Outcome switch
            {
                CashSessionOpenOutcome.Opened => CashClosedMessageText.Text,
                CashSessionOpenOutcome.AlreadyOpen => "Ya hay una caja abierta en esta terminal.",
                _ => "El efectivo inicial no es válido.",
            };
            if (result.Outcome == CashSessionOpenOutcome.Opened)
            {
                SaleResultText.Text = $"Caja abierta con {openingFloat:C} iniciales.";
                RefreshStatus();
                _ = RunSyncAsync(SyncTrigger.PostSale);
            }
        }

        RefreshCashSession();
        if (_cashSession is not null)
        {
            ScanCodeTextBox.Focus();
        }
    }

    /// <summary>
    /// The lock screen let an operator in: adopt them, lift the lock and, the first
    /// time, offer the open-cash prompt when no cash session is open (the prompt is
    /// opened after the lock closed, not from inside the sign-in event).
    /// </summary>
    private void LockScreen_SignedIn(CachedOperator signedIn)
    {
        _currentOperator.Set(signedIn);
        RefreshIdentityText();
        RefreshCashSession();

        if (!_openCashPrompted && _cashSession is null)
        {
            _openCashPrompted = true;
            Dispatcher.BeginInvoke(PromptOpenCash, System.Windows.Threading.DispatcherPriority.Background);
        }
    }

    /// <summary>
    /// Nobody signed in: only the lock screen is shown. The nav, the sale, the sections
    /// and the cash prompt are collapsed (neither visible nor reachable by keyboard or
    /// scanner), but nothing is cleared: the cart and the cash session stay as they
    /// are for the next operator (pos-operator-session "Sign-Out Keeps The Cash
    /// Session And The Cart").
    /// </summary>
    private void ApplyLockState()
    {
        var locked = _currentOperator.Value is null;
        ShellContent.Visibility = locked ? Visibility.Collapsed : Visibility.Visible;
        LockHost.Visibility = locked ? Visibility.Visible : Visibility.Collapsed;
        if (locked == _locked)
        {
            return;
        }

        _locked = locked;
        if (locked)
        {
            _lockScreen.Show();
        }
        else if (_shell.Current == ShellSection.Sale && _cashSession is not null)
        {
            ScanCodeTextBox.Focus();
        }
    }

    /// <summary>
    /// After a sync the status check may have dropped operators (revoked or inactive):
    /// the active one is signed out, which brings the lock screen back, and the lock
    /// screen's tiles follow the store.
    /// </summary>
    private void ReconcileOperatorsAfterSync()
    {
        _operatorSession.Reconcile(_localOperatorStore.Load());
        RefreshIdentityText();
        if (_locked)
        {
            _lockScreen.ReloadTiles();
        }
    }

    /// <summary>
    /// "Cerrar Caja": shows the totals computed from this session's recorded
    /// tenders, asks for the counted cash, records the close with its difference
    /// and returns to the "Abrir caja" state. A sale still being built must be
    /// completed or cleared first.
    /// </summary>
    private void CloseCashButton_Click(object sender, RoutedEventArgs e)
    {
        var session = _branchNodeService.GetOpenCashSession();
        if (session is null)
        {
            RefreshCashSession();
            return;
        }

        if (!_cart.IsEmpty)
        {
            ScanMessageText.Text = "Cobre o vacíe la venta actual antes de cerrar la caja.";
            return;
        }

        var summary = _branchNodeService.GetCashSessionSummary(session.SessionId)!;
        var window = new CloseCashWindow(session, summary) { Owner = this };
        if (window.ShowDialog() != true || window.CountedCash is not { } counted)
        {
            return;
        }

        var result = _branchNodeService.CloseCashSession(
            session.SessionId, _currentOperator.ResolveActorId(_installationId), counted, Guid.NewGuid());
        if (result.Outcome == CashSessionCloseOutcome.Closed && result.Session?.Closure is { } closure)
        {
            var summaryText =
                $"Caja cerrada. Esperado {closure.Summary.ExpectedCash:C}, contado {closure.CountedCash:C}: {CashSessionInput.DifferenceLabel(closure.Difference)}.";
            SaleResultText.Text = summaryText;
            CashClosedMessageText.Text = summaryText + " Abra la caja con el efectivo inicial para seguir vendiendo.";
            RefreshStatus();
            _ = RunSyncAsync(SyncTrigger.PostSale);
        }
        else
        {
            ScanMessageText.Text = "No se pudo cerrar la caja: ya estaba cerrada o el importe no es válido.";
        }

        RefreshCashSession();
    }

    /// <summary>The message shown when a sale commit was refused for lack of an open cash session.</summary>
    private void ReportNoOpenCashSession()
    {
        ScanMessageText.Text = "No hay una caja abierta. Abra la caja para registrar ventas.";
        RefreshCashSession();
    }

    private void RefreshIdentityText()
    {
        NavBar.OperatorLabel = _currentOperator.Value is { } currentOperator
            ? currentOperator.Email
            : "Sin operador activo";

        // pos-operator-session spec "Admin-Only Customer Management Screen
        // Gated by Current Operator Role": no operator identified, or an
        // operator without ManageUsers, hides the button entirely. This is a
        // UX affordance only — the server re-checks ManageUsers on every
        // /customers call regardless (design.md "Desktop authorization for
        // customer create/edit").
        NavBar.AdminEntriesVisible =
            _currentOperator.Value is { } current && ((Permission)current.Permissions).HasFlag(Permission.ManageUsers);
        ReconcileShell();
        ApplyLockState();
    }

    private string BuildStatusSummary()
    {
        var status = _branchNodeService.GetStatus(_pairing.BranchId, isOffline: true);
        return
            $"Operaciones pendientes: {status.PendingOperationCount}\n" +
            $"Última confirmación: {(status.LastAcknowledgedUtc?.ToString("O") ?? "nunca")}";
    }

    private void RefreshStatus()
    {
        BottomSyncStatusText.Text = BuildCompactSyncStatus();
        // The footer stays compact: the full update status lives in Settings;
        // the footer only offers the wizard when a compatible release exists.
        BottomVersionStatusText.Text = $"v{_localVersion}";
        BottomVersionStatusText.ToolTip = BuildVersionStatus();
        BottomUpdateAvailableButton.Content = $"Actualización {_updateCheckResult.AvailableVersion} disponible";
        BottomUpdateAvailableButton.Visibility = _updateCheckResult.IsUpdateAvailable
            ? Visibility.Visible
            : Visibility.Collapsed;
        BottomUpgradeReportText.Text = _upgradeReport.Message;
        BottomUpgradeReportText.Visibility = _upgradeReport.Status == PendingUpgradeStatus.None
            ? Visibility.Collapsed
            : Visibility.Visible;
    }

    /// <summary>
    /// Opens the install wizard. Never installs by itself: the operator starts
    /// it inside the wizard, and the workflow refuses while a sale is being built.
    /// </summary>
    private void UpdateAvailableButton_Click(object sender, RoutedEventArgs e)
    {
        if (!_updateCheckResult.IsUpdateAvailable)
        {
            return;
        }

        var wizard = new UpdateWizardWindow(
            _updateCheckResult,
            UpdateEnvironment.Current(),
            _updateWizardFactory.TrustedPublisher,
            _updateWizardFactory.IsPackaged,
            () => _updateWizardFactory.Create(() => _cart.Lines.Count > 0))
        {
            Owner = this
        };

        wizard.ShowDialog();
        RefreshStatus();
    }

    private string BuildCompactSyncStatus()
    {
        var status = _branchNodeService.GetStatus(_pairing.BranchId, isOffline: true);
        var lastAck = status.LastAcknowledgedUtc?.ToLocalTime().ToString("yyyy-MM-dd HH:mm") ?? "nunca";
        return $"Sincronización: {status.PendingOperationCount} pendientes - Última confirmación: {lastAck} - {_lastSyncResult}";
    }

    private string BuildVersionStatus() => $"Versión {_localVersion} · {ReleaseDiscovery.FormatCompactStatus(_updateCheckResult)}";

    private static Version ReadLocalVersion()
    {
        var informationalVersion = typeof(App).Assembly
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()
            ?.InformationalVersion
            .Split('+', 2)[0];

        return Version.TryParse(informationalVersion, out var parsed)
            ? parsed
            : new Version(0, 0, 0);
    }

    private string BuildIdentitySummary()
    {
        var operatorLine = _currentOperator.Value is { } op
            ? $"Operador actual: {op.Email}"
            : "Operador actual: sin operador activo";

        return
            $"Organización: {_pairing.OrganizationId}\n" +
            $"Terminal: {TerminalLabel.Format(_pairing)}\n" +
            $"Operador de emparejamiento: {_pairing.OperatorEmail}\n" +
            $"Instalación: {_installationId}\n" +
            $"{operatorLine}";
    }

    /// <summary>
    /// Manual-total sale, completed with one of the tender buttons in the popup
    /// (its Tag is the method). Asks the tender, then commits with the selected
    /// customer and the tender.
    /// </summary>
    private void CommitSaleButton_Click(object sender, RoutedEventArgs e)
    {
        ManualSalePopup.IsOpen = false;

        // Task 7.5: mutual exclusion (design.md "POS: two explicit buttons,
        // not a mode toggle") — a mixed sale would need per-line provenance,
        // which is out of scope, so committing manually while scanned lines
        // are pending is blocked with an explicit message rather than
        // silently mixing the two flows in one transaction.
        if (!_cart.IsEmpty)
        {
            SaleResultText.Text = "No se puede cobrar una venta manual mientras hay productos escaneados pendientes. Cobre la venta escaneada o vacíe la lista primero.";
            return;
        }

        if (!decimal.TryParse(AmountTextBox.Text, NumberStyles.Number, CultureInfo.InvariantCulture, out var amount) || amount <= 0m)
        {
            SaleResultText.Text = "Importe inválido.";
            return;
        }

        var tender = CollectTender((sender as FrameworkElement)?.Tag as string ?? SaleTender.Cash, amount);
        if (tender is null)
        {
            return;
        }

        var customerId = (CustomerPickerComboBox.SelectedItem as SaleCustomerPickerItem)?.CustomerId;

        // Zero references to DeviceToken, zero HTTP, zero credential validity
        // check — a fully revoked device credential never reaches this path.
        var result = _branchNodeService.CompleteOfflineSale(
            organizationId: _pairing.OrganizationId,
            branchId: _pairing.BranchId,
            actorId: _currentOperator.ResolveActorId(_installationId),
            saleId: Guid.NewGuid(),
            totalAmount: amount,
            operationId: Guid.NewGuid(),
            correlationId: Guid.NewGuid(),
            customerId: customerId,
            tender: tender);

        if (result.Refusal is SaleCommitRefusal.NoOpenCashSession)
        {
            ReportNoOpenCashSession();
            return;
        }

        SaleResultText.Text = result.WasNewlyCommitted
            ? $"Venta {result.Effect.SaleId} registrada por {result.Effect.TotalAmount:C} ({TenderInput.Describe(tender)}) en branch.db."
            : $"La venta {result.Effect.SaleId} ya estaba registrada (reintento idempotente).";

        RefreshStatus();

        // commerce-customer-identity follow-up: the picker is optional and
        // resets to walk-in after every commit — anonymous counter sale stays
        // the fastest, zero-friction default for the NEXT sale too.
        CustomerPickerComboBox.SelectedIndex = 0;

        // Task 4.6: fire-and-forget post-sale nudge — never awaited, so a
        // slow or unreachable cloud can never delay or fail this commit
        // (ADR-002).
        _ = RunSyncAsync(SyncTrigger.PostSale);
    }

    /// <summary>
    /// Opens the tender prompt for <paramref name="method"/> and returns what the
    /// operator confirmed, or null when they cancelled (nothing is committed).
    /// </summary>
    private SaleTender? CollectTender(string method, decimal total)
    {
        var window = new TenderWindow(method, total) { Owner = this };
        return window.ShowDialog() == true ? window.Tender : null;
    }

    /// <summary>
    /// Optional wholesale/delivery customer attribution (commerce-customer-
    /// identity follow-up, verify-report CRITICAL: "Customer becomes
    /// selectable on POS after sync"). Defaults to
    /// <see cref="SaleCustomerPicker.WalkInLabel"/> at index 0 — anonymous
    /// walk-in retail stays the default, zero-friction path;
    /// <see cref="CommitSaleButton_Click"/> never requires a selection.
    /// </summary>
    private void RefreshCustomerPicker()
    {
        var selectedCustomerId = (CustomerPickerComboBox.SelectedItem as SaleCustomerPickerItem)?.CustomerId;

        var items = SaleCustomerPicker.BuildItems(_store.ListCustomers());
        CustomerPickerComboBox.ItemsSource = items;
        CustomerPickerComboBox.SelectedIndex = selectedCustomerId is null
            ? 0
            : Math.Max(0, items.ToList().FindIndex(i => i.CustomerId == selectedCustomerId));
    }

    /// <summary>
    /// Task 7.4/7.6: keyboard-wedge scan handler (design.md "POS scan-to-sell"
    /// — `Enter` -> lookup). Looks up the code against the local
    /// `catalog_replica`/`price_replica` cache; an unknown code or a known
    /// code with no locally effective price produces an explicit, visible,
    /// non-appending message — never a zero-priced line substitute.
    /// Re-scanning an already-added code merges into that line by
    /// re-resolving at the accumulated quantity, so rounding always comes
    /// from <see cref="PricingResolutionService"/>, never hand computed here.
    /// </summary>
    private async void ScanCodeTextBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter)
        {
            return;
        }

        var code = ScanCodeTextBox.Text.Trim();
        ScanCodeTextBox.Clear();
        if (string.IsNullOrWhiteSpace(code))
        {
            return;
        }

        var item = _store.FindByIdentificationCode(_pairing.OrganizationId, code);
        if (item is null)
        {
            ScanMessageText.Text = $"El código {code} no está en el catálogo de esta terminal.";
            ScanCodeTextBox.Focus();
            return;
        }

        var added = await _cart.AddAsync(item);
        ScanMessageText.Text = added.Succeeded ? string.Empty : added.Message;

        RefreshScannedTotal();
        ScanCodeTextBox.Focus();
    }

    /// <summary>
    /// Debounces name search: the cards grid follows the search box as the
    /// operator types, without a query per keystroke. Enter still resolves an
    /// exact identification code through <see cref="ScanCodeTextBox_KeyDown"/>.
    /// </summary>
    private void ScanCodeTextBox_TextChanged(object sender, System.Windows.Controls.TextChangedEventArgs e)
    {
        if (_searchDebounce is null)
        {
            return;
        }

        _searchDebounce.Stop();
        _searchDebounce.Start();
    }

    /// <summary>Rebuilds the product cards from the local catalog replica (search-filtered, capped).</summary>
    private void RefreshCatalogCards()
    {
        var result = _store.SearchCatalog(_pairing.OrganizationId, ScanCodeTextBox.Text, categoryId: _selectedCategoryId);
        _catalogCards.Clear();
        foreach (var item in result.Items)
        {
            _catalogCards.Add(new ProductCardViewModel(item));
        }

        var hasFilter = !string.IsNullOrWhiteSpace(ScanCodeTextBox.Text) || _selectedCategoryId is not null;
        CatalogEmptyText.Text = hasFilter
            ? "Ningún producto coincide con la búsqueda."
            : "No hay productos en el catálogo de esta terminal. Sincronice para descargarlos.";
        CatalogEmptyText.Visibility = _catalogCards.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        CatalogTruncatedText.Visibility = result.Truncated ? Visibility.Visible : Visibility.Collapsed;
    }

    /// <summary>
    /// Reloads the category rail from the local catalog replica ("Todos" plus
    /// the distinct local categories). The selection survives when its category
    /// still exists and otherwise falls back to "Todos", so a category that
    /// disappeared after a sync can never leave the grid filtered to nothing.
    /// </summary>
    private void RefreshCategoryRail()
    {
        _railRefreshing = true;
        try
        {
            CategoryRailControl.Categories = CategoryRailItem.Build(_store.ListCatalogCategories(_pairing.OrganizationId));
            _selectedCategoryId = Guid.TryParse(CategoryRailControl.SelectedCategory?.Key, out var id) ? id : null;
        }
        finally
        {
            _railRefreshing = false;
        }
    }

    private void CategoryRail_CategorySelected(object? sender, CategoryRailItem item)
    {
        if (_railRefreshing)
        {
            return;
        }

        _selectedCategoryId = Guid.TryParse(item.Key, out var id) ? id : null;
        RefreshCatalogCards();
    }

    private async void ProductCard_IncrementRequested(object sender, RoutedEventArgs e)
    {
        if ((e.Source as FrameworkElement)?.DataContext is ProductCardViewModel card)
        {
            await ApplyCartChangeAsync(() => _cart.AddAsync(card.Item));
        }
    }

    private async void ProductCard_DecrementRequested(object sender, RoutedEventArgs e)
    {
        if ((e.Source as FrameworkElement)?.DataContext is ProductCardViewModel card)
        {
            await ApplyCartChangeAsync(() => _cart.DecrementAsync(card.PresentationId));
        }
    }

    private async void SaleTable_QuantityEdited(object? sender, Controls.SaleLineQuantityEventArgs e) =>
        await ApplyCartChangeAsync(() => _cart.SetQuantityAsync(e.PresentationId, e.Quantity));

    private async void SaleTable_LineRemoved(object? sender, Guid presentationId) =>
        await ApplyCartChangeAsync(() => Task.FromResult(_cart.Remove(presentationId) ? SaleCartResult.Ok : SaleCartResult.Fail("El producto no está en la venta.")));

    private void SaleTable_LineDiscountRequested(object? sender, Guid presentationId)
    {
        var line = _cart.Lines.FirstOrDefault(l => l.PresentationId == presentationId);
        if (line is null)
        {
            return;
        }

        RequestDiscount(
            "Descuento de la línea", line.DisplayName, line.LineDiscountPercent,
            (percent, authorization) => _cart.SetLineDiscount(presentationId, percent, authorization),
            () => _cart.RemoveLineDiscount(presentationId));
    }

    private void TotalsPanel_SaleDiscountRequested(object sender, RoutedEventArgs e)
    {
        if (_cart.IsEmpty)
        {
            ScanMessageText.Text = "Agregue productos antes de aplicar un descuento a la venta.";
            return;
        }

        RequestDiscount(
            "Descuento de la venta", "Se aplica al subtotal después de los descuentos de las líneas.", _cart.SaleDiscountPercent,
            (percent, authorization) => _cart.SetSaleDiscount(percent, authorization),
            () => _cart.RemoveSaleDiscount());
    }

    /// <summary>
    /// Opens the discount prompt. Adding or changing a discount needs the branch
    /// PIN (checked offline against the cached verifier); removing one does not.
    /// The cart is only touched with an authorization the prompt granted.
    /// </summary>
    private void RequestDiscount(
        string heading, string subject, decimal? currentPercent,
        Func<decimal, DiscountAuthorization, SaleCartResult> apply, Func<bool> remove)
    {
        var window = new DiscountWindow(
            _discountAuthorizer, _currentOperator.ResolveActorId(_installationId), heading, subject, currentPercent)
        {
            Owner = this
        };

        if (window.ShowDialog() != true)
        {
            return;
        }

        if (window.Result == DiscountWindowResult.Removed)
        {
            remove();
            ScanMessageText.Text = string.Empty;
        }
        else if (window.Result == DiscountWindowResult.Applied && window.Authorization is { } authorization)
        {
            var result = apply(window.Percent, authorization);
            ScanMessageText.Text = result.Succeeded ? string.Empty : result.Message;
        }

        RefreshScannedTotal();
    }

    private async Task ApplyCartChangeAsync(Func<Task<SaleCartResult>> change)
    {
        var result = await change();
        ScanMessageText.Text = result.Succeeded ? string.Empty : result.Message;
        RefreshScannedTotal();
    }

    private void ManualSaleButton_Click(object sender, RoutedEventArgs e)
    {
        ManualSalePopup.IsOpen = true;
        AmountTextBox.Focus();
        AmountTextBox.SelectAll();
    }

    private void RefreshScannedTotal()
    {
        TotalsPanelControl.Subtotal = _cart.Subtotal;
        TotalsPanelControl.DiscountTotal = _cart.DiscountTotal;
        TotalsPanelControl.Total = _cart.Total;
        foreach (var card in _catalogCards)
        {
            card.ApplyLine(_cart.Lines.FirstOrDefault(l => l.PresentationId == card.PresentationId));
        }
    }

    /// <summary>
    /// Task 7.4: complete the scanned sale with a tender (raised by the totals
    /// panel's Efectivo / Tarjeta / QR buttons) — writes `sale_effects(sale_kind=
    /// 'Scanned') + sale_lines` atomically via
    /// <see cref="BranchNodeService.CompleteScannedSale"/>, distinct from
    /// <see cref="CommitSaleButton_Click"/>'s manual-total path.
    /// </summary>
    private void CommitScannedSaleButton_Click(object sender, TenderRequestedEventArgs e)
    {
        if (_cart.IsEmpty)
        {
            ScanMessageText.Text = "Escanee al menos un producto antes de cobrar.";
            return;
        }

        var total = _cart.Total;
        var tender = CollectTender(e.Method, total);
        if (tender is null)
        {
            return;
        }

        var saleId = Guid.NewGuid();
        var lines = _cart.BuildSaleLines(saleId);
        var customerId = (CustomerPickerComboBox.SelectedItem as SaleCustomerPickerItem)?.CustomerId;

        var result = _branchNodeService.CompleteScannedSale(
            organizationId: _pairing.OrganizationId,
            branchId: _pairing.BranchId,
            actorId: _currentOperator.ResolveActorId(_installationId),
            saleId: saleId,
            lines: lines,
            totalAmount: total,
            operationId: Guid.NewGuid(),
            correlationId: Guid.NewGuid(),
            customerId: customerId,
            saleDiscount: _cart.SaleDiscount,
            discountAuthorization: _cart.Authorization,
            tender: tender);

        if (result.Refusal is SaleCommitRefusal.NoOpenCashSession)
        {
            ReportNoOpenCashSession();
            return;
        }

        SaleResultText.Text = result.WasNewlyCommitted
            ? $"Venta escaneada {result.Effect.SaleId} registrada por {result.Effect.TotalAmount:C} ({TenderInput.Describe(tender)}) en branch.db."
            : $"La venta {result.Effect.SaleId} ya estaba registrada (reintento idempotente).";

        _cart.Clear();
        RefreshScannedTotal();
        RefreshStatus();
        CustomerPickerComboBox.SelectedIndex = 0;

        // Task 4.6: same fire-and-forget post-sale nudge as the manual-total path.
        _ = RunSyncAsync(SyncTrigger.PostSale);
    }

    /// <summary>
    /// Task 7.7: ADR-002's EXISTING freshness policy, verbatim — reuses
    /// <see cref="CachedOperator.Ttl"/> (14 days), the one freshness
    /// threshold already implemented in this app for cached admin data, read
    /// against the `catalog-prices` sync cursor age. No new TTL constant, no
    /// timer, no second mechanism. A stale cache never blocks a sale (ADR-002
    /// keeps the branch authoritative for its own sale) — this only makes
    /// staleness visible.
    /// </summary>
    private void RefreshCatalogFreshness()
    {
        var cursor = _store.GetCatalogPricesCursor();
        if (cursor is null || DateTimeOffset.UtcNow - cursor.Value <= CachedOperator.Ttl)
        {
            StaleCatalogBanner.Visibility = Visibility.Collapsed;
            return;
        }

        StaleCatalogBannerText.Text =
            $"El catálogo/precios se sincronizó por última vez {cursor.Value:O}; supera la ventana de vigencia de {CachedOperator.Ttl.TotalDays:0} días. " +
            "Los precios pueden estar desactualizados; las ventas no se bloquean.";
        StaleCatalogBanner.Visibility = Visibility.Visible;
    }

    /// <summary>
    /// Task 4.2/4.4 (commerce-sync-ownership design.md "Retry: where and
    /// how"): the ONE method every trigger calls, delegating the actual work
    /// to <see cref="SyncRunner.RunAsync"/> (reentrancy-guarded there). UI
    /// text updates ONLY for <see cref="SyncTrigger.Button"/> — startup,
    /// the scheduler sweep, and the post-sale nudge stay entirely invisible
    /// to the operator (answer (d)); a failure is still always durably
    /// recorded by <see cref="SyncRunner"/> via
    /// <c>BranchSyncStore.RecordAttemptFailure</c> regardless of trigger.
    /// </summary>
    private async Task RunSyncAsync(SyncTrigger trigger)
    {
        if (trigger == SyncTrigger.Button)
        {
            _lastSyncResult = "Sincronizando...";
            RefreshStatus();
        }

        var result = await _syncRunner.RunAsync(trigger);

        // The status check may have dropped operators: follow it (the active one is signed out, the lock returns).
        await Dispatcher.InvokeAsync(ReconcileOperatorsAfterSync);

        if (trigger != SyncTrigger.Button)
        {
            return;
        }

        RefreshCustomerPicker();
        RefreshCategoryRail();
        RefreshCatalogCards();
        RefreshScannedTotal();
        RefreshCatalogFreshness();

        if (result is null)
        {
            // Reentrant: a sweep was already in flight. Leave "Syncing..."
            // as-is rather than claiming a result that never ran.
            return;
        }

        _lastSyncResult = result.Summary;
        RefreshStatus();
    }

    private async void SyncButton_Click(object sender, RoutedEventArgs e) => await RunSyncAsync(SyncTrigger.Button);

    /// <summary>
    /// Always-visible button (design.md "Re-pairing": NOT an automatic
    /// interrupt-the-sale modal) opening <see cref="PairingWindow"/> modally
    /// and reloading identity on success. Never blocks
    /// <see cref="CommitSaleButton_Click"/> — the operator chooses when to
    /// re-pair.
    /// </summary>
    private void RepairButton_Click(object sender, RoutedEventArgs e)
    {
        var settingsWindow = new SettingsWindow(
            BuildStatusSummary,
            BuildIdentitySummary,
            () => _lastSyncResult,
            BuildVersionStatus,
            async () =>
            {
                await RunSyncAsync(SyncTrigger.Button);
                return _lastSyncResult;
            },
            ReconfigureTerminal,
            async () =>
            {
                await RunUpdateCheckAsync();
                return BuildVersionStatus();
            })
        {
            Owner = this
        };

        settingsWindow.ShowDialog();
        RefreshStatus();
    }

    /// <summary>
    /// Explicit terminal re-pairing action used from the configuration window.
    /// </summary>
    private bool ReconfigureTerminal(Window owner)
    {
        var pairingWindow = new PairingWindow(_pairingClient, _localInstallationStore, _installationId)
        {
            Owner = owner
        };

        var result = pairingWindow.ShowDialog();
        if (result != true || pairingWindow.PairedRecord?.Pairing is null)
        {
            return false;
        }

        _pairing = pairingWindow.PairedRecord.Pairing;
        RefreshIdentityText();
        _lastSyncResult = "Terminal reconfigurada. Los pendientes se enviarán en la próxima sincronización.";
        RefreshStatus();
        return true;
    }

    /// <summary>
    /// The operator button opens a menu (pos-operator-session "Operator Menu"):
    /// who is signed in, switch operator, sign out. The last two both return to the lock screen;
    /// a new operator signs in there with email and password. Nothing here is a precondition of
    /// <see cref="CommitSaleButton_Click"/>, and none of it closes the cash session.
    /// </summary>
    private void OperatorMenuButton_Click(object sender, RoutedEventArgs e) =>
        NavBar.OpenOperatorMenu(OperatorMenuPresenter.Build(_currentOperator.Value, _localOperatorStore.Load(), DateTimeOffset.UtcNow));

    private void SwitchOperatorButton_Click(object sender, RoutedEventArgs e)
    {
        _operatorSession.SwitchOperator();
        RefreshIdentityText();
    }

    private void OperatorSignOutMenu_Click(object sender, RoutedEventArgs e)
    {
        _operatorSession.SignOut();
        RefreshIdentityText();
    }

    private void SaleNavButton_Click(object sender, RoutedEventArgs e) => ShowSection(ShellSection.Sale);

    /// <summary>
    /// Shows Clientes inside the shell with a FRESH <see cref="CustomerAdminClient"/>
    /// (design.md "Desktop authorization for customer create/edit"): its cookie
    /// lives only while the section is open and is discarded when the operator
    /// leaves it, never persisted, never reused across visits. Button visibility
    /// is UX-only (see <see cref="RefreshIdentityText"/>); the server re-checks
    /// <c>ManageUsers</c> on every call the section makes.
    /// </summary>
    private void ManageCustomersButton_Click(object sender, RoutedEventArgs e) => ShowSection(ShellSection.Customers);

    /// <summary>
    /// Switches the content area to the target section. The sale screen is
    /// hidden, never rebuilt or cleared: the cart, the scan box and the cash
    /// session survive a visit to another section. A section with a request in
    /// flight keeps the focus until it ends.
    /// </summary>
    private void ShowSection(ShellSection target)
    {
        if (_shell.TeardownPending)
        {
            // The previous operator's section is still finishing a request: say so instead of ignoring the click.
            SaleResultText.Text = PosMessages.PreviousOperationRunning;
            return;
        }

        if (_shell.TeardownExpired)
        {
            // It never went idle in time: its request was cancelled, release it so the shell is never stuck.
            ReleaseDetachedSection();
        }

        if (_sections.Active?.IsBusy == true)
        {
            return;
        }

        if (_shell.Navigate(target, _currentOperator.Value?.Permissions))
        {
            ApplySection();
        }
    }

    /// <summary>Mirrors <see cref="_shell"/> in the window: disposes the section being left and builds the one being entered.</summary>
    private void ApplySection()
    {
        _sections.Show(null);
        SectionHost.Content = null;

        var section = _shell.Current;
        if (section == ShellSection.Customers)
        {
            var view = new CustomersView(_customerAdminClientFactory(), _currentOperator.Value?.Email);
            _sections.Show(view);
            SectionHost.Content = view;
        }
        else if (section == ShellSection.Staff && _currentOperator.Value is { } admin)
        {
            var view = new StaffView(_userAdminClientFactory(), _localOperatorStore, _pairing.BranchId, admin.UserId, admin.Email);
            view.OperatorsChanged += StaffView_OperatorsChanged;
            _sections.Show(view);
            SectionHost.Content = view;
        }

        var isSale = section == ShellSection.Sale;
        SaleScreen.Visibility = isSale ? Visibility.Visible : Visibility.Collapsed;
        SectionHost.Visibility = isSale ? Visibility.Collapsed : Visibility.Visible;
        NavBar.SetActiveSection(section);
        RefreshCashSession();
        if (isSale && _cashSession is not null)
        {
            ScanCodeTextBox.Focus();
        }
    }

    /// <summary>
    /// After the operator changed (sign-out, switch, removal): a section they may not open gives
    /// way to the sale. The model moves to the sale at once, so the screen always follows it. A
    /// section with a request in flight is never disposed under it: it is detached and hidden, and
    /// torn down when its request ends, or when the teardown timed out (<see cref="ShellNavigation.TeardownExpired"/>).
    /// </summary>
    private void ReconcileShell()
    {
        switch (_shell.Reconcile(_currentOperator.Value?.Permissions, _sections.Active?.IsBusy == true))
        {
            case ReconcileOutcome.Switched:
                ApplySection();
                break;
            case ReconcileOutcome.Deferred:
                SectionHost.Visibility = Visibility.Collapsed;
                SaleScreen.Visibility = Visibility.Visible;
                NavBar.SetActiveSection(ShellSection.Sale);
                _sections.DetachActive();
                RefreshCashSession();
                break;
        }
    }

    private void OnDetachedSectionReleased(bool clearHost)
    {
        if (clearHost)
        {
            SectionHost.Content = null;
        }

        _shell.CompleteTeardown();
    }

    private void ReleaseDetachedSection() => OnDetachedSectionReleased(_sections.ReleaseDetached());

    /// <summary>Shows Personal inside the shell: admin staff management plus removal of this terminal's operators (no provisioning).</summary>
    private void ManageStaffButton_Click(object sender, RoutedEventArgs e) => ShowSection(ShellSection.Staff);

    /// <summary>
    /// An operator was removed from this terminal in Personal: the active operator is
    /// reconciled with what is stored (removed: signed out, which also leaves Personal).
    /// </summary>
    private void StaffView_OperatorsChanged(object? sender, EventArgs e)
    {
        _operatorSession.Reconcile(_localOperatorStore.Load());
        RefreshIdentityText();
    }

    // Task 4.6: the customer pull, catalog/price pull, and operator
    // reconciliation that used to live here moved verbatim into
    // SyncRunner (Commerce.Pos.Windows/SyncRunner.cs) — every trigger
    // (startup, the 60s sweep, the post-sale nudge, and this button) now
    // shares that ONE implementation instead of only SyncButton_Click
    // owning it.
}
