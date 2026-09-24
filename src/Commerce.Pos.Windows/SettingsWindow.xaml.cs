using System.Windows;
using System.Windows.Controls;

namespace Commerce.Pos.Windows;

public partial class SettingsWindow : Window
{
    private readonly Func<string> _getStatusSummary;
    private readonly Func<string> _getIdentitySummary;
    private readonly Func<string> _getSyncResult;
    private readonly Func<string> _getVersionStatus;
    private readonly Func<Task<string>> _syncPendingAsync;
    private readonly Func<Window, bool> _reconfigureTerminal;
    private bool _themePickerReady;

    public SettingsWindow(
        Func<string> getStatusSummary,
        Func<string> getIdentitySummary,
        Func<string> getSyncResult,
        Func<string> getVersionStatus,
        Func<Task<string>> syncPendingAsync,
        Func<Window, bool> reconfigureTerminal)
    {
        InitializeComponent();

        _getStatusSummary = getStatusSummary;
        _getIdentitySummary = getIdentitySummary;
        _getSyncResult = getSyncResult;
        _getVersionStatus = getVersionStatus;
        _syncPendingAsync = syncPendingAsync;
        _reconfigureTerminal = reconfigureTerminal;

        InitializeThemePicker();
        RefreshSummaries();
    }

    private void InitializeThemePicker()
    {
        var savedTheme = DesktopThemeService.LoadSavedTheme();
        SetThemeSelection(savedTheme);
        ThemeStatusText.Text = $"Tema actual: {DesktopThemeService.GetDisplayName(savedTheme)}.";
        _themePickerReady = true;
    }

    private void SetThemeSelection(DesktopTheme theme)
    {
        DarkThemeButton.IsChecked = theme == DesktopTheme.Dark;
        LightThemeButton.IsChecked = theme == DesktopTheme.Light;
        VacaVerdeThemeButton.IsChecked = theme == DesktopTheme.VacaVerde;
    }

    private void RefreshSummaries()
    {
        StatusText.Text = _getStatusSummary();
        IdentityText.Text = _getIdentitySummary();
        SyncResultText.Text = _getSyncResult();
        VersionStatusText.Text = _getVersionStatus();
    }

    private void ThemeRadioButton_Checked(object sender, RoutedEventArgs e)
    {
        if (!_themePickerReady || sender is not RadioButton { Tag: string themeName } ||
            !Enum.TryParse<DesktopTheme>(themeName, out var theme))
        {
            return;
        }

        DesktopThemeService.SaveAndApplyTheme(theme);
        ThemeStatusText.Text = DesktopThemeService.GetAppliedMessage(theme);
    }

    private async void SyncPendingButton_Click(object sender, RoutedEventArgs e)
    {
        SyncPendingButton.IsEnabled = false;
        SyncResultText.Text = "Sincronizando pendientes...";

        try
        {
            SyncResultText.Text = await _syncPendingAsync();
        }
        finally
        {
            SyncPendingButton.IsEnabled = true;
            RefreshSummaries();
        }
    }

    private void ReconfigureTerminalButton_Click(object sender, RoutedEventArgs e)
    {
        if (_reconfigureTerminal(this))
        {
            RefreshSummaries();
        }
    }
}
