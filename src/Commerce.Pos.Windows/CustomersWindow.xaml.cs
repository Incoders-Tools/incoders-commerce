using System.Globalization;
using System.Windows;

namespace Commerce.Pos.Windows;

/// <summary>
/// Modal admin sign-in + customer list/create/edit (design.md "Desktop
/// customer management"): performs a real <c>POST /account/sign-in</c>
/// through <see cref="CustomerAdminClient"/>, then calls the SAME
/// <c>/customers</c> endpoints <c>Commerce.Web</c> uses. The server
/// re-checks <c>ManageUsers</c> on every call; this window adds no
/// authorization logic of its own — <see cref="MainWindow"/>'s button
/// visibility is UX-only. Connectivity is required end-to-end (the
/// pairing/bootstrap precedent): any network failure surfaces as a status
/// message, never a local write or a queued mutation.
/// </summary>
public partial class CustomersWindow : Window
{
    private static readonly string[] CustomerKinds = { "Retail", "Wholesale" };
    private static readonly string[] TaxIdTypes = { "None", "Cuit", "Cuil" };
    private static readonly string[] TaxConditions =
        { "ConsumidorFinal", "ResponsableInscripto", "Monotributo", "Exento", "NoAplica" };

    private readonly CustomerAdminClient _adminClient;
    private List<CustomerAdminRecordDto> _customers = new();
    private Guid? _selectedCustomerId;

    public CustomersWindow(CustomerAdminClient adminClient)
    {
        InitializeComponent();
        _adminClient = adminClient;

        CustomerKindComboBox.ItemsSource = CustomerKinds;
        TaxIdTypeComboBox.ItemsSource = TaxIdTypes;
        TaxConditionComboBox.ItemsSource = TaxConditions;
    }

    private async void SignInButton_Click(object sender, RoutedEventArgs e)
    {
        StatusText.Text = string.Empty;
        var outcome = await _adminClient.SignInAsync(AdminEmailTextBox.Text, AdminPasswordBox.Password);

        switch (outcome.Kind)
        {
            case AdminSignInOutcomeKind.SignedIn:
                SignInPanel.Visibility = Visibility.Collapsed;
                ManagementPanel.Visibility = Visibility.Visible;
                await LoadCustomersAsync();
                break;

            case AdminSignInOutcomeKind.InvalidCredentials:
                StatusText.Text = outcome.ErrorMessage ?? "Invalid email or password.";
                break;

            case AdminSignInOutcomeKind.Failed:
            default:
                StatusText.Text = outcome.ErrorMessage ?? "Sign in failed.";
                break;
        }
    }

    private async Task LoadCustomersAsync()
    {
        var customers = await _adminClient.ListCustomersAsync();
        if (customers is null)
        {
            FormStatusText.Text = "Customer management requires connectivity.";
            return;
        }

        _customers = customers.ToList();
        CustomersListBox.ItemsSource = null;
        CustomersListBox.ItemsSource = _customers;
    }

    private void NewCustomerButton_Click(object sender, RoutedEventArgs e)
    {
        CustomersListBox.SelectedItem = null;
        _selectedCustomerId = null;
        ClearForm();
        IsEnabledCheckBox.Visibility = Visibility.Collapsed;
        FormStatusText.Text = string.Empty;
    }

    private void CustomersListBox_SelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
    {
        if (CustomersListBox.SelectedItem is not CustomerAdminRecordDto selected)
        {
            return;
        }

        _selectedCustomerId = selected.Id;
        FormStatusText.Text = string.Empty;

        CustomerKindComboBox.SelectedItem = selected.CustomerKind;
        DisplayNameTextBox.Text = selected.DisplayName;
        LegalNameTextBox.Text = selected.LegalName ?? string.Empty;
        TaxIdTypeComboBox.SelectedItem = selected.TaxIdType;
        TaxIdTextBox.Text = selected.TaxId ?? string.Empty;
        TaxConditionComboBox.SelectedItem = selected.TaxCondition;
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
        // drives price-list selection in Phase C, so flipping it retroactively
        // changes commercial meaning. The same rule applies on the desktop.
        CustomerKindComboBox.IsEnabled = false;
    }

    private async void SaveButton_Click(object sender, RoutedEventArgs e)
    {
        FormStatusText.Text = string.Empty;

        if (string.IsNullOrWhiteSpace(DisplayNameTextBox.Text))
        {
            FormStatusText.Text = "Display name is required.";
            return;
        }

        decimal? discountPercentage = null;
        if (!string.IsNullOrWhiteSpace(DiscountPercentageTextBox.Text))
        {
            if (!decimal.TryParse(DiscountPercentageTextBox.Text, NumberStyles.Number, CultureInfo.InvariantCulture, out var parsed))
            {
                FormStatusText.Text = "Discount percentage must be a number.";
                return;
            }

            discountPercentage = parsed;
        }

        var taxIdType = TaxIdTypeComboBox.SelectedItem as string ?? "None";
        var taxCondition = TaxConditionComboBox.SelectedItem as string ?? "NoAplica";

        CustomerAdminMutationOutcome outcome;
        if (_selectedCustomerId is { } id)
        {
            outcome = await _adminClient.UpdateCustomerAsync(id, new UpdateCustomerAdminRequestDto(
                DisplayNameTextBox.Text,
                NullIfBlank(LegalNameTextBox.Text),
                taxIdType,
                NullIfBlank(TaxIdTextBox.Text),
                taxCondition,
                NullIfBlank(PhoneTextBox.Text),
                NullIfBlank(EmailTextBox.Text),
                NullIfBlank(AddressStreetTextBox.Text),
                NullIfBlank(AddressNumberTextBox.Text),
                NullIfBlank(NeighborhoodTextBox.Text),
                NullIfBlank(LocalityTextBox.Text),
                NullIfBlank(ProvinceTextBox.Text),
                NullIfBlank(PostalCodeTextBox.Text),
                NullIfBlank(DeliveryNotesTextBox.Text),
                discountPercentage,
                NullIfBlank(PaymentTermsTextBox.Text),
                NullIfBlank(NotesTextBox.Text),
                IsEnabledCheckBox.IsChecked ?? true));
        }
        else
        {
            var customerKind = CustomerKindComboBox.SelectedItem as string ?? "Retail";
            outcome = await _adminClient.CreateCustomerAsync(new CreateCustomerAdminRequestDto(
                customerKind,
                DisplayNameTextBox.Text,
                NullIfBlank(LegalNameTextBox.Text),
                taxIdType,
                NullIfBlank(TaxIdTextBox.Text),
                taxCondition,
                NullIfBlank(PhoneTextBox.Text),
                NullIfBlank(EmailTextBox.Text),
                NullIfBlank(AddressStreetTextBox.Text),
                NullIfBlank(AddressNumberTextBox.Text),
                NullIfBlank(NeighborhoodTextBox.Text),
                NullIfBlank(LocalityTextBox.Text),
                NullIfBlank(ProvinceTextBox.Text),
                NullIfBlank(PostalCodeTextBox.Text),
                NullIfBlank(DeliveryNotesTextBox.Text),
                discountPercentage,
                NullIfBlank(PaymentTermsTextBox.Text),
                NullIfBlank(NotesTextBox.Text)));
        }

        switch (outcome.Kind)
        {
            case CustomerAdminMutationKind.Succeeded:
                FormStatusText.Text = "Saved.";
                CustomerKindComboBox.IsEnabled = true;
                await LoadCustomersAsync();
                break;

            case CustomerAdminMutationKind.Forbidden:
            case CustomerAdminMutationKind.NotFound:
            case CustomerAdminMutationKind.Failed:
            default:
                FormStatusText.Text = outcome.ErrorMessage ?? "Save failed.";
                break;
        }
    }

    private void CloseButton_Click(object sender, RoutedEventArgs e) => Close();

    private void ClearForm()
    {
        CustomerKindComboBox.IsEnabled = true;
        CustomerKindComboBox.SelectedItem = null;
        DisplayNameTextBox.Text = string.Empty;
        LegalNameTextBox.Text = string.Empty;
        TaxIdTypeComboBox.SelectedItem = null;
        TaxIdTextBox.Text = string.Empty;
        TaxConditionComboBox.SelectedItem = null;
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
