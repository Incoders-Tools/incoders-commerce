using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

namespace Commerce.Pos.Windows;

/// <summary>
/// The full-window sign-in shown while nobody is signed in: operator tiles + PIN,
/// or email + password (then a new PIN when the operator is new to this terminal).
/// It only mirrors <see cref="LockScreenModel"/> and forwards what was typed; the
/// decisions live in the model. The one network action (email + password) runs
/// through <see cref="BusyController"/> like every other POS screen.
/// </summary>
public partial class LockScreenView : UserControl
{
    private readonly Func<string> _branchName;
    private readonly BusyController _busy;
    private string? _unexpected;

    public LockScreenView(LockScreenModel model, Func<string> branchName)
    {
        InitializeComponent();
        Model = model;
        _branchName = branchName;
        _busy = new BusyController(ApplyBusy, nameof(LockScreenView), message => _unexpected = message);

        Model.SignedIn += operatorRecord =>
        {
            ClearSecrets();
            SignedIn?.Invoke(operatorRecord);
        };
    }

    public LockScreenModel Model { get; }

    /// <summary>Raised when an operator got in; the host adopts them and lifts the lock.</summary>
    public event Action<CachedOperator>? SignedIn;

    public bool IsBusy => _busy.IsBusy;

    /// <summary>Starts over (tiles, or email + password on first run) each time the lock appears.</summary>
    public void Show()
    {
        Model.Refresh();
        EmailTextBox.Clear();
        ClearSecrets();
        Render();
    }

    /// <summary>Follows a background change of the terminal's operators without interrupting the operator.</summary>
    public void ReloadTiles()
    {
        var before = (Model.Mode, Ids: string.Join(',', Model.Tiles.Select(tile => tile.UserId)));
        Model.ReloadTiles();
        var after = (Model.Mode, Ids: string.Join(',', Model.Tiles.Select(tile => tile.UserId)));
        if (before != after)
        {
            Render();
        }
    }

    private void Render()
    {
        var mode = Model.Mode;
        BranchText.Text = _branchName();
        TilesPanel.Visibility = Vis(mode == LockScreenMode.Tiles);
        PinPanel.Visibility = Vis(mode == LockScreenMode.PinEntry);
        CredentialsPanel.Visibility = Vis(mode == LockScreenMode.Credentials);
        CreatePinPanel.Visibility = Vis(mode == LockScreenMode.CreatePin);
        BackButton.Visibility = Vis(mode != LockScreenMode.Tiles && (mode != LockScreenMode.Credentials || Model.CanGoBack));
        TilesItemsControl.ItemsSource = Model.Tiles;
        ResetPinCheckBox.IsChecked = Model.ResetPin;
        ResetPinCheckBox.Visibility = Vis(Model.CanGoBack);

        (TitleText.Text, SubtitleText.Text) = mode switch
        {
            LockScreenMode.Tiles => ("Ingresar", "Elegí tu usuario para empezar."),
            LockScreenMode.PinEntry => (Model.SelectedTile?.Email ?? "Ingresar", "Ingresá tu PIN de 6 dígitos."),
            LockScreenMode.CreatePin => ("Creá tu PIN",
                "Elegí 6 dígitos que no sean un patrón simple. Con este PIN vas a entrar la próxima vez."),
            _ => ("Ingresar", Model.CanGoBack
                ? "Ingresá con tu correo y contraseña. Hace falta conexión a internet."
                : "Es la primera vez en esta terminal. Ingresá con tu correo y contraseña y después vas a crear un PIN."),
        };

        ShowStatus(Model.Status ?? _unexpected);
        FocusForMode();
    }

    private static Visibility Vis(bool visible) => visible ? Visibility.Visible : Visibility.Collapsed;

    private void FocusForMode()
    {
        // Focus after layout, so the freshly shown panel can take it.
        Dispatcher.BeginInvoke(() =>
        {
            switch (Model.Mode)
            {
                case LockScreenMode.PinEntry:
                    PinBox.Focus();
                    break;
                case LockScreenMode.Credentials:
                    (EmailTextBox.Text.Length == 0 ? (Control)EmailTextBox : PasswordBox).Focus();
                    break;
                case LockScreenMode.CreatePin:
                    NewPinBox.Focus();
                    break;
            }
        }, System.Windows.Threading.DispatcherPriority.Input);
    }

    private void ShowStatus(string? message)
    {
        StatusText.Text = message ?? string.Empty;
        StatusBorder.Visibility = string.IsNullOrEmpty(message) ? Visibility.Collapsed : Visibility.Visible;
    }

    private void ClearSecrets()
    {
        PinBox.Clear();
        PasswordBox.Clear();
        NewPinBox.Clear();
        ConfirmPinBox.Clear();
    }

    private void ApplyBusy(bool busy, string? text)
    {
        FormPanel.IsEnabled = !busy;
        BusyPanel.Visibility = busy ? Visibility.Visible : Visibility.Collapsed;
        BusyText.Text = text ?? string.Empty;
        Cursor = busy ? Cursors.Wait : null;
    }

    // ---- tiles and navigation ---------------------------------------------------------

    private void TileButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { Tag: Guid userId })
        {
            PinBox.Clear();
            Model.SelectTile(userId);
            Render();
        }
    }

    private void CredentialsLinkButton_Click(object sender, RoutedEventArgs e)
    {
        Model.ChooseCredentials();
        Render();
    }

    private void BackButton_Click(object sender, RoutedEventArgs e)
    {
        ClearSecrets();
        Model.Back();
        Render();
    }

    // ---- PIN entry ---------------------------------------------------------------------

    private void KeypadButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { Tag: string digit } && PinBox.Password.Length < OperatorPinCredential.PinLength)
        {
            PinBox.Password += digit;
        }

        PinBox.Focus();
    }

    private void KeypadClearButton_Click(object sender, RoutedEventArgs e)
    {
        PinBox.Clear();
        PinBox.Focus();
    }

    private void KeypadBackspaceButton_Click(object sender, RoutedEventArgs e)
    {
        if (PinBox.Password.Length > 0)
        {
            PinBox.Password = PinBox.Password[..^1];
        }

        PinBox.Focus();
    }

    private void PinBox_PasswordChanged(object sender, RoutedEventArgs e)
    {
        if (PinBox.Password.Length == OperatorPinCredential.PinLength)
        {
            SubmitPin();
        }
    }

    private void PinBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
        {
            SubmitPin();
            e.Handled = true;
        }
    }

    private void SubmitPin()
    {
        var accepted = false;
        try
        {
            accepted = Model.SubmitPin(PinBox.Password);
        }
        finally
        {
            // A PIN never stays in the box, right or wrong.
            PinBox.Clear();
        }

        if (!accepted)
        {
            Render();
        }
    }

    // ---- email + password --------------------------------------------------------------

    private void ResetPinCheckBox_Changed(object sender, RoutedEventArgs e) =>
        Model.ResetPin = ResetPinCheckBox.IsChecked == true;

    private void CredentialsBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
        {
            SignInButton_Click(sender, e);
            e.Handled = true;
        }
    }

    private async void SignInButton_Click(object sender, RoutedEventArgs e)
    {
        _unexpected = null;
        var email = EmailTextBox.Text;
        var password = PasswordBox.Password;
        await _busy.RunAsync(PosMessages.Verifying, async () =>
        {
            try
            {
                await Model.SubmitCredentialsAsync(email, password);
            }
            finally
            {
                PasswordBox.Clear();
            }
        });
        Render();
    }

    // ---- create PIN --------------------------------------------------------------------

    private void CreatePinBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
        {
            SavePinButton_Click(sender, e);
            e.Handled = true;
        }
    }

    private void SavePinButton_Click(object sender, RoutedEventArgs e)
    {
        var saved = false;
        try
        {
            saved = Model.SubmitNewPin(NewPinBox.Password, ConfirmPinBox.Password);
        }
        finally
        {
            NewPinBox.Clear();
            ConfirmPinBox.Clear();
        }

        if (!saved)
        {
            Render();
        }
    }
}
