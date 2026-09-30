using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Commerce.Updater;

namespace Commerce.Pos.Windows;

/// <summary>
/// Themed install wizard. It only orchestrates: every stage, gate and typed
/// failure lives in <see cref="UpdateInstallWorkflow"/>; the text lives in
/// <see cref="UpdateWizardText"/>. Nothing runs until the operator presses
/// "Instalar actualización".
/// </summary>
public partial class UpdateWizardWindow : Window
{
    private enum StageState
    {
        Pending,
        Active,
        Done,
        Failed
    }

    private readonly UpdateCheckResult _check;
    private readonly Func<UpdateInstallWorkflow> _createWorkflow;
    private readonly Dictionary<UpdateInstallStage, (TextBlock Glyph, TextBlock Label)> _rows = new();
    private CancellationTokenSource? _cancellation;
    private bool _running;
    // The last stage reported by the workflow: an exception that escapes it is
    // shown at the stage that was actually running, not at a guessed one.
    private UpdateInstallStage _lastStage = UpdateInstallStage.Preflight;

    public UpdateWizardWindow(
        UpdateCheckResult check,
        UpdateEnvironment environment,
        string trustedPublisher,
        bool isPackaged,
        Func<UpdateInstallWorkflow> createWorkflow)
    {
        InitializeComponent();

        _check = check;
        _createWorkflow = createWorkflow;

        var details = UpdateWizardText.Describe(check, environment);
        CurrentVersionText.Text = details.CurrentVersion;
        TargetVersionText.Text = details.TargetVersion;
        FormatText.Text = details.Format;
        SizeText.Text = details.Size;
        CompatibilityText.Text = details.Compatibility;
        PublisherText.Text = trustedPublisher;
        UnpackagedBorder.Visibility = isPackaged ? Visibility.Collapsed : Visibility.Visible;

        foreach (var stage in UpdateWizardText.VisibleStages)
        {
            var glyph = new TextBlock { Width = 22, FontFamily = new FontFamily("Segoe UI Symbol"), VerticalAlignment = VerticalAlignment.Center };
            var label = new TextBlock { Text = UpdateWizardText.StageLabel(stage), VerticalAlignment = VerticalAlignment.Center };
            var row = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 5) };
            row.Children.Add(glyph);
            row.Children.Add(label);
            StagesPanel.Children.Add(row);
            _rows[stage] = (glyph, label);
            SetStage(stage, StageState.Pending);
        }

        ProgressBar.Value = 0;
        StatusText.Text = "Listo para comenzar.";
    }

    /// <summary>Shows a stage as running (also used to preview mid-progress states).</summary>
    public void ShowProgress(UpdateInstallStage stage, double? fraction, string message)
    {
        _lastStage = stage;
        foreach (var visible in UpdateWizardText.VisibleStages)
        {
            SetStage(visible, visible < stage ? StageState.Done : visible == stage ? StageState.Active : StageState.Pending);
        }

        if (fraction is { } value)
        {
            ProgressBar.IsIndeterminate = false;
            ProgressBar.Value = value;
        }
        else
        {
            ProgressBar.IsIndeterminate = true;
        }

        StatusText.Text = message;
    }

    /// <summary>Shows the typed result of a run (also used to preview end states).</summary>
    public void ShowOutcome(UpdateInstallOutcome outcome)
    {
        ProgressBar.IsIndeterminate = false;
        var success = outcome.Succeeded;
        foreach (var visible in UpdateWizardText.VisibleStages)
        {
            SetStage(visible, success || visible < outcome.Stage ? StageState.Done
                : visible == outcome.Stage ? StageState.Failed
                : StageState.Pending);
        }

        ProgressBar.Value = success ? 1 : ProgressBar.Value;
        StatusText.Text = success ? "Instalación enviada a Windows." : "La actualización no se instaló.";

        ResultBorder.Visibility = Visibility.Visible;
        ResultBorder.Background = (Brush)FindResource(success ? "SurfaceSoftBrush" : "DangerSurfaceBrush");
        ResultBorder.BorderBrush = (Brush)FindResource(success ? "SuccessBrush" : "DangerBrush");
        ResultTitleText.Foreground = (Brush)FindResource(success ? "SuccessBrush" : "DangerBrush");
        ResultTitleText.Text = success ? "Actualización instalada" : UpdateWizardText.FailureTitle(outcome.Reason ?? UpdateFailureReason.InstallFailed);
        ResultMessageText.Text = outcome.Message;

        InstallButton.Visibility = success ? Visibility.Collapsed : Visibility.Visible;
        InstallButton.Content = "Reintentar";
        CloseButton.Content = "Cerrar";
    }

    private async void InstallButton_Click(object sender, RoutedEventArgs e)
    {
        if (_running)
        {
            return;
        }

        _running = true;
        InstallButton.IsEnabled = false;
        CloseButton.Content = "Cancelar";
        ResultBorder.Visibility = Visibility.Collapsed;
        _cancellation = new CancellationTokenSource();
        _lastStage = UpdateInstallStage.Preflight;

        UpdateInstallOutcome outcome;
        try
        {
            var workflow = _createWorkflow();
            var progress = new Progress<UpdateProgress>(p => ShowProgress(p.Stage, p.Fraction, p.Message));
            outcome = await Task.Run(() => workflow.RunAsync(_check, progress, _cancellation.Token));
        }
        catch (Exception ex)
        {
            // The workflow types its own failures; this only covers an exception
            // that escaped it (for example while creating the workflow).
            outcome = new UpdateInstallOutcome(false, UpdateFailureReason.UnexpectedError, _lastStage,
                $"Error inesperado: {ex.Message}");
        }
        finally
        {
            _running = false;
            InstallButton.IsEnabled = true;
        }

        ShowOutcome(outcome);
    }

    private void CloseButton_Click(object sender, RoutedEventArgs e)
    {
        if (_running)
        {
            _cancellation?.Cancel();
            return;
        }

        Close();
    }

    protected override void OnClosing(CancelEventArgs e)
    {
        // The install itself cannot be interrupted once it is handed to Windows.
        e.Cancel = _running;
        base.OnClosing(e);
    }

    private void SetStage(UpdateInstallStage stage, StageState state)
    {
        var (glyph, label) = _rows[stage];
        glyph.Text = state switch
        {
            StageState.Done => "✓",
            StageState.Active => "●",
            StageState.Failed => "✗",
            _ => "○"
        };
        var brushKey = state switch
        {
            StageState.Done => "SuccessBrush",
            StageState.Active => "AccentTextBrush",
            StageState.Failed => "DangerBrush",
            _ => "MutedTextBrush"
        };
        glyph.SetResourceReference(TextBlock.ForegroundProperty, brushKey);
        label.SetResourceReference(TextBlock.ForegroundProperty, state == StageState.Pending ? "MutedTextBrush" : "TextBrush");
        label.FontWeight = state == StageState.Active ? FontWeights.SemiBold : FontWeights.Normal;
    }
}
