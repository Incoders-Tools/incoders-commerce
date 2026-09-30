using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

namespace Commerce.Pos.Windows;

/// <summary>
/// Clientes section of the main window (design.md "Desktop customer
/// management"): an inline admin confirmation, then the customer list and
/// create/edit form against the SAME <c>/customers</c> endpoints
/// <c>Commerce.Web</c> uses. The server re-checks <c>ManageUsers</c> on every
/// call; the shell only hides the entry (UX). Connectivity is required
/// end-to-end: a network failure is shown inline, never a local write.
///
/// The view owns a FRESH <see cref="CustomerAdminClient"/> whose cookie lives
/// only while the section is open (<see cref="Dispose"/>); the typed password is
/// never stored. Status and errors render above the scroll area so they stay
/// visible however far the form is scrolled.
/// </summary>
public partial class CustomersView : UserControl, ISectionView
{
    private readonly CustomerAdminClient _adminClient;
    private readonly BusyController _busy;
    private List<CustomerAdminRecordDto> _customers = new();
    private Guid? _selectedCustomerId;

    /// <param name="operatorEmail">The signed-in operator, used as the admin to confirm; null asks for the email too.</param>
    public CustomersView(CustomerAdminClient adminClient, string? operatorEmail)
    {
        InitializeComponent();
        _adminClient = adminClient;
        _busy = new BusyController(ApplyBusy, nameof(CustomersView), message => ShowStatus(message, isError: true));

        CustomerKindComboBox.ItemsSource = CustomerFormChoices.Kinds;
        TaxIdTypeComboBox.ItemsSource = CustomerFormChoices.TaxIdTypes;
        TaxConditionComboBox.ItemsSource = CustomerFormChoices.TaxConditions;

        SignInPanel.Initialize(
            PosMessages.ConfirmPasswordTitle,
            PosMessages.ConfirmPasswordForCustomers,
            operatorEmail);
        SignInPanel.SignInRequested += async (_, _) => await SignInAsync();
        Loaded += (_, _) => SignInPanel.FocusPassword();
    }

    public bool IsBusy => _busy.IsBusy;

    public void Dispose() => _adminClient.Dispose();

    private void ApplyBusy(bool busy, string? text)
    {
        FormPanel.IsEnabled = !busy;
        BusyPanel.Visibility = busy ? Visibility.Visible : Visibility.Collapsed;
        BusyText.Text = text ?? string.Empty;
        Cursor = busy ? Cursors.Wait : null;
    }

    private void ShowStatus(string message, bool isError)
    {
        StatusText.Text = message;
        StatusText.SetResourceReference(TextBlock.ForegroundProperty, isError ? "DangerBrush" : "SuccessBrush");
        StatusText.Visibility = string.IsNullOrEmpty(message) ? Visibility.Collapsed : Visibility.Visible;
    }

    private async Task SignInAsync()
    {
        var email = SignInPanel.Email;
        var password = SignInPanel.Password;
        await _busy.RunAsync(PosMessages.SigningIn, async () =>
        {
            ShowStatus(string.Empty, isError: false);
            var outcome = await _adminClient.SignInAsync(email, password);
            SignInPanel.ClearPassword();
            if (outcome.Kind != AdminSignInOutcomeKind.SignedIn)
            {
                ShowStatus(outcome.ErrorMessage ?? PosMessages.SignInFailed, isError: true);
                return;
            }

            SignInScroll.Visibility = Visibility.Collapsed;
            ManagementPanel.Visibility = Visibility.Visible;
            ResetForm();
            await LoadCustomersAsync();
        });
    }

    private async Task LoadCustomersAsync()
    {
        var customers = await _adminClient.ListCustomersAsync();
        if (customers is null)
        {
            ShowStatus(PosMessages.CustomersLoadFailed, isError: true);
            return;
        }

        _customers = customers.ToList();
        CustomersListBox.ItemsSource = null;
        CustomersListBox.ItemsSource = _customers;
    }

    private void NewCustomerButton_Click(object sender, RoutedEventArgs e)
    {
        CustomersListBox.SelectedItem = null;
        ResetForm();
        ShowStatus(string.Empty, isError: false);
    }

    private void CustomersListBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (CustomersListBox.SelectedItem is not CustomerAdminRecordDto selected)
        {
            return;
        }

        _selectedCustomerId = selected.Id;
        ShowStatus(string.Empty, isError: false);

        CustomerKindComboBox.SelectedValue = selected.CustomerKind;
        DisplayNameTextBox.Text = selected.DisplayName;
        LegalNameTextBox.Text = selected.LegalName ?? string.Empty;
        TaxIdTypeComboBox.SelectedValue = selected.TaxIdType;
        TaxIdTextBox.Text = selected.TaxId ?? string.Empty;
        TaxConditionComboBox.SelectedValue = selected.TaxCondition;
        PhoneTextBox.Text = selected.Phone ?? string.Empty;
        EmailTextBox.Text = selected.Email ?? string.Empty;
        AddressStreetTextBox.Text = selected.AddressStreet ?? string.Empty;
        AddressNumberTextBox.Text = selected.AddressNumber ?? string.Empty;
        NeighborhoodTextBox.Text = selected.Neighborhood ?? string.Empty;
        LocalityTextBox.Text = selected.Locality ?? string.Empty;
        ProvinceTextBox.Text = selected.Province ?? string.Empty;
        PostalCodeTextBox.Text = selected.PostalCode ?? string.Empty;
        DeliveryNotesTextBox.Text = selected.DeliveryNotes ?? string.Empty;
        DiscountPercentageTextBox.Text = selected.DiscountPercentage?.ToString(CultureInfo.InvariantCulture) ?? string.Empty;
        PaymentTermsTextBox.Text = selected.PaymentTerms ?? string.Empty;
        NotesTextBox.Text = selected.Notes ?? string.Empty;
        IsEnabledCheckBox.Visibility = Visibility.Visible;
        IsEnabledCheckBox.IsChecked = selected.IsEnabled;

        // CustomerKind is read-only at edit (design.md "Web form shape"): it
        // drives price-list selection, so flipping it retroactively changes
        // commercial meaning. The same rule applies on the desktop.
        CustomerKindComboBox.IsEnabled = false;
    }

    private async void SaveButton_Click(object sender, RoutedEventArgs e)
    {
        ShowStatus(string.Empty, isError: false);

        if (string.IsNullOrWhiteSpace(DisplayNameTextBox.Text))
        {
            ShowStatus(PosMessages.DisplayNameRequired, isError: true);
            return;
        }

        decimal? discountPercentage = null;
        if (!string.IsNullOrWhiteSpace(DiscountPercentageTextBox.Text))
        {
            if (!decimal.TryParse(DiscountPercentageTextBox.Text, NumberStyles.Number, CultureInfo.InvariantCulture, out var parsed))
            {
                ShowStatus(PosMessages.DiscountMustBeNumber, isError: true);
                return;
            }

            discountPercentage = parsed;
        }

        var taxIdType = TaxIdTypeComboBox.SelectedValue as string ?? "None";
        var taxCondition = TaxConditionComboBox.SelectedValue as string ?? "NoAplica";
        var customerKind = CustomerKindComboBox.SelectedValue as string ?? "Retail";
        var selectedCustomerId = _selectedCustomerId;
        var isEnabled = IsEnabledCheckBox.IsChecked ?? true;
        var form = ReadForm();

        await _busy.RunAsync(PosMessages.Saving, async () =>
        {
            CustomerAdminMutationOutcome outcome;
            if (selectedCustomerId is { } id)
            {
                outcome = await _adminClient.UpdateCustomerAsync(id, new UpdateCustomerAdminRequestDto(
                    form.DisplayName, form.LegalName, taxIdType, form.TaxId, taxCondition, form.Phone, form.Email,
                    form.AddressStreet, form.AddressNumber, form.Neighborhood, form.Locality, form.Province, form.PostalCode,
                    form.DeliveryNotes, discountPercentage, form.PaymentTerms, form.Notes, isEnabled));
            }
            else
            {
                outcome = await _adminClient.CreateCustomerAsync(new CreateCustomerAdminRequestDto(
                    customerKind, form.DisplayName, form.LegalName, taxIdType, form.TaxId, taxCondition, form.Phone, form.Email,
                    form.AddressStreet, form.AddressNumber, form.Neighborhood, form.Locality, form.Province, form.PostalCode,
                    form.DeliveryNotes, discountPercentage, form.PaymentTerms, form.Notes));
            }

            if (outcome.Kind == CustomerAdminMutationKind.Succeeded)
            {
                ShowStatus(PosMessages.Saved, isError: false);
                CustomerKindComboBox.IsEnabled = true;
                await LoadCustomersAsync();
                return;
            }

            ShowStatus(outcome.ErrorMessage ?? PosMessages.SaveFailed, isError: true);
        });
    }

    /// <summary>The text fields as read when the operator pressed Guardar (blank becomes null).</summary>
    private FormFields ReadForm() => new(
        DisplayNameTextBox.Text, NullIfBlank(LegalNameTextBox.Text), NullIfBlank(TaxIdTextBox.Text), NullIfBlank(PhoneTextBox.Text),
        NullIfBlank(EmailTextBox.Text), NullIfBlank(AddressStreetTextBox.Text), NullIfBlank(AddressNumberTextBox.Text),
        NullIfBlank(NeighborhoodTextBox.Text), NullIfBlank(LocalityTextBox.Text), NullIfBlank(ProvinceTextBox.Text),
        NullIfBlank(PostalCodeTextBox.Text), NullIfBlank(DeliveryNotesTextBox.Text), NullIfBlank(PaymentTermsTextBox.Text),
        NullIfBlank(NotesTextBox.Text));

    private sealed record FormFields(
        string DisplayName, string? LegalName, string? TaxId, string? Phone, string? Email, string? AddressStreet,
        string? AddressNumber, string? Neighborhood, string? Locality, string? Province, string? PostalCode,
        string? DeliveryNotes, string? PaymentTerms, string? Notes);

    private void ResetForm()
    {
        _selectedCustomerId = null;
        IsEnabledCheckBox.Visibility = Visibility.Collapsed;
        CustomerKindComboBox.IsEnabled = true;
        CustomerKindComboBox.SelectedValue = "Retail";
        TaxIdTypeComboBox.SelectedValue = "None";
        TaxConditionComboBox.SelectedValue = "NoAplica";
        DisplayNameTextBox.Text = string.Empty;
        LegalNameTextBox.Text = string.Empty;
        TaxIdTextBox.Text = string.Empty;
        PhoneTextBox.Text = string.Empty;
        EmailTextBox.Text = string.Empty;
        AddressStreetTextBox.Text = string.Empty;
        AddressNumberTextBox.Text = string.Empty;
        NeighborhoodTextBox.Text = string.Empty;
        LocalityTextBox.Text = string.Empty;
        ProvinceTextBox.Text = string.Empty;
        PostalCodeTextBox.Text = string.Empty;
        DeliveryNotesTextBox.Text = string.Empty;
        DiscountPercentageTextBox.Text = string.Empty;
        PaymentTermsTextBox.Text = string.Empty;
        NotesTextBox.Text = string.Empty;
    }

    private static string? NullIfBlank(string value) => string.IsNullOrWhiteSpace(value) ? null : value;
}
