using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

namespace Commerce.Pos.Windows;

/// <summary>
/// Clientes section of the main window (design.md "Desktop customer
/// management"): the customer list and create/edit form against the SAME
/// <c>/customers</c> endpoints <c>Commerce.Web</c> uses, over the shared
/// <see cref="ManagementConnection"/> (device credential + the signed-in
/// operator, no password prompt). The server re-checks the operator and
/// <c>ManageUsers</c> on every call; the shell only hides the entry (UX).
/// Connectivity is required end-to-end: a network failure is shown inline,
/// never a local write.
///
/// The form (admin-console-field-fixes T4) has one name field whose label
/// follows Persona / Empresa, the address as Province -> City (that province's
/// cities, by name) -> Postal code (prefilled from the city when known, still
/// editable), and an email box checked live with the shared email rule; saving
/// is blocked while it is invalid. Status and errors render above the scroll
/// area so they stay visible however far the form is scrolled.
/// </summary>
public partial class CustomersView : UserControl, ISectionView
{
    private readonly CustomerAdminClient _adminClient;
    private readonly BusyController _busy;
    private readonly Dictionary<string, IReadOnlyList<CityOptionDto>> _citiesByProvince = new();
    private List<CustomerAdminRecordDto> _customers = new();
    private Guid? _selectedCustomerId;

    // The postal code the current city filled in, so a later city replaces it but never a hand-typed one.
    private string? _cityPostalCode;

    // Set while the form fills the address from a stored customer: no city reload and no postal code prefill.
    private bool _fillingAddress;

    public CustomersView(CustomerAdminClient adminClient)
    {
        InitializeComponent();
        _adminClient = adminClient;
        _busy = new BusyController(ApplyBusy, nameof(CustomersView), message => ShowStatus(message, isError: true));

        CustomerKindComboBox.ItemsSource = CustomerFormChoices.Kinds;
        PartyTypeComboBox.ItemsSource = CustomerFormChoices.PartyTypes;
        TaxIdTypeComboBox.ItemsSource = CustomerFormChoices.TaxIdTypes;
        TaxConditionComboBox.ItemsSource = CustomerFormChoices.TaxConditions;

        ResetForm();
        Loaded += async (_, _) => await _busy.RunAsync(PosMessages.Loading, async () =>
        {
            var provinces = await _adminClient.ListProvincesAsync(_busy.Token);
            if (provinces is null)
            {
                ShowStatus(PosMessages.ProvincesLoadFailed, isError: true);
            }

            ProvinceComboBox.ItemsSource = provinces;
            await LoadCustomersAsync();
        });
    }

    public bool IsBusy => _busy.IsBusy;

    public event Action? Idle
    {
        add => _busy.Idle += value;
        remove => _busy.Idle -= value;
    }

    public void CancelPending() => _busy.Cancel();

    /// <summary>The client is the shared management connection's: only the request in flight is cancelled.</summary>
    public void Dispose() => _busy.Cancel();

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

    private async Task LoadCustomersAsync()
    {
        var customers = await _adminClient.ListCustomersAsync(_busy.Token);
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

    private async void CustomersListBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (CustomersListBox.SelectedItem is not CustomerAdminRecordDto selected)
        {
            return;
        }

        _selectedCustomerId = selected.Id;
        ShowStatus(string.Empty, isError: false);

        CustomerKindComboBox.SelectedValue = selected.CustomerKind;
        PartyTypeComboBox.SelectedValue = selected.PartyType;
        DisplayNameTextBox.Text = selected.DisplayName;
        TaxIdTypeComboBox.SelectedValue = selected.TaxIdType;
        TaxIdTextBox.Text = selected.TaxId ?? string.Empty;
        TaxConditionComboBox.SelectedValue = selected.TaxCondition;
        PhoneTextBox.Text = selected.Phone ?? string.Empty;
        EmailTextBox.Text = selected.Email ?? string.Empty;
        AddressStreetTextBox.Text = selected.AddressStreet ?? string.Empty;
        AddressNumberTextBox.Text = selected.AddressNumber ?? string.Empty;
        NeighborhoodTextBox.Text = selected.Neighborhood ?? string.Empty;
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

        // The stored city preselects its province; the stored postal code is kept as it is.
        await _busy.RunAsync(PosMessages.Loading, () => FillAddressAsync(selected.ProvinceId, selected.CityId));
    }

    // ---- address: Province -> City -> Postal code ------------------------------------

    private async Task FillAddressAsync(string? provinceId, Guid? cityId)
    {
        _fillingAddress = true;
        try
        {
            ProvinceComboBox.SelectedValue = provinceId;
            var cities = provinceId is null ? null : await CitiesOfAsync(provinceId);
            CityComboBox.ItemsSource = cities;
            CityComboBox.SelectedValue = cityId;
            _cityPostalCode = (CityComboBox.SelectedItem as CityOptionDto)?.PostalCode;
        }
        finally
        {
            _fillingAddress = false;
        }
    }

    /// <summary>The cities of a province, read once per visit; null (and an inline error) when they could not be read.</summary>
    private async Task<IReadOnlyList<CityOptionDto>?> CitiesOfAsync(string provinceId)
    {
        if (_citiesByProvince.TryGetValue(provinceId, out var cached))
        {
            return cached;
        }

        var cities = await _adminClient.ListCitiesAsync(provinceId, _busy.Token);
        if (cities is null)
        {
            ShowStatus(PosMessages.CitiesLoadFailed, isError: true);
            return null;
        }

        _citiesByProvince[provinceId] = cities;
        return cities;
    }

    private async void ProvinceComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_fillingAddress)
        {
            return;
        }

        CityComboBox.ItemsSource = null;
        if (ProvinceComboBox.SelectedValue is not string provinceId)
        {
            return;
        }

        await _busy.RunAsync(PosMessages.Loading, async () =>
        {
            var cities = await CitiesOfAsync(provinceId);
            // The operator may have picked another province while this one loaded.
            if (Equals(ProvinceComboBox.SelectedValue, provinceId))
            {
                CityComboBox.ItemsSource = cities;
            }
        });
    }

    private void CityComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_fillingAddress || CityComboBox.SelectedItem is not CityOptionDto city)
        {
            return;
        }

        PostalCodeTextBox.Text = CustomerFormRules.PostalCodeAfterCityChange(PostalCodeTextBox.Text, _cityPostalCode, city.PostalCode);
        _cityPostalCode = city.PostalCode;
    }

    // ---- name and email -------------------------------------------------------------------

    private void PartyTypeComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e) =>
        NameLabel.Text = CustomerFormRules.NameLabel(PartyTypeComboBox.SelectedValue as string);

    private void EmailTextBox_TextChanged(object sender, TextChangedEventArgs e) => RenderEmailState();

    /// <summary>Green check when valid, an inline error when invalid (saving is blocked), nothing when empty.</summary>
    private EmailFieldState RenderEmailState()
    {
        var state = CustomerFormRules.Email(EmailTextBox.Text);
        EmailValidIcon.Visibility = state == EmailFieldState.Valid ? Visibility.Visible : Visibility.Collapsed;
        EmailErrorText.Visibility = state == EmailFieldState.Invalid ? Visibility.Visible : Visibility.Collapsed;
        SaveButton.IsEnabled = state != EmailFieldState.Invalid;
        return state;
    }

    // ---- save -----------------------------------------------------------------------------

    private async void SaveButton_Click(object sender, RoutedEventArgs e)
    {
        ShowStatus(string.Empty, isError: false);

        if (string.IsNullOrWhiteSpace(DisplayNameTextBox.Text))
        {
            ShowStatus(PosMessages.DisplayNameRequired, isError: true);
            return;
        }

        if (RenderEmailState() == EmailFieldState.Invalid)
        {
            ShowStatus(PosMessages.InvalidEmail, isError: true);
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
        var partyType = PartyTypeComboBox.SelectedValue as string ?? "Person";
        var cityId = CityComboBox.SelectedValue as Guid?;
        var selectedCustomerId = _selectedCustomerId;
        var isEnabled = IsEnabledCheckBox.IsChecked ?? true;
        var form = ReadForm();

        await _busy.RunAsync(PosMessages.Saving, async () =>
        {
            CustomerAdminMutationOutcome outcome;
            if (selectedCustomerId is { } id)
            {
                outcome = await _adminClient.UpdateCustomerAsync(id, new UpdateCustomerAdminRequestDto(
                    form.DisplayName, taxIdType, form.TaxId, taxCondition, form.Phone, form.Email,
                    form.AddressStreet, form.AddressNumber, form.Neighborhood, form.PostalCode,
                    form.DeliveryNotes, discountPercentage, form.PaymentTerms, form.Notes, isEnabled,
                    cityId ?? Guid.Empty, partyType), _busy.Token);
            }
            else
            {
                outcome = await _adminClient.CreateCustomerAsync(new CreateCustomerAdminRequestDto(
                    customerKind, form.DisplayName, taxIdType, form.TaxId, taxCondition, form.Phone, form.Email,
                    form.AddressStreet, form.AddressNumber, form.Neighborhood, form.PostalCode,
                    form.DeliveryNotes, discountPercentage, form.PaymentTerms, form.Notes, cityId, partyType), _busy.Token);
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
        DisplayNameTextBox.Text.Trim(), NullIfBlank(TaxIdTextBox.Text), NullIfBlank(PhoneTextBox.Text),
        NullIfBlank(EmailTextBox.Text)?.Trim(), NullIfBlank(AddressStreetTextBox.Text), NullIfBlank(AddressNumberTextBox.Text),
        NullIfBlank(NeighborhoodTextBox.Text), NullIfBlank(PostalCodeTextBox.Text), NullIfBlank(DeliveryNotesTextBox.Text),
        NullIfBlank(PaymentTermsTextBox.Text), NullIfBlank(NotesTextBox.Text));

    private sealed record FormFields(
        string DisplayName, string? TaxId, string? Phone, string? Email, string? AddressStreet,
        string? AddressNumber, string? Neighborhood, string? PostalCode,
        string? DeliveryNotes, string? PaymentTerms, string? Notes);

    private void ResetForm()
    {
        _selectedCustomerId = null;
        IsEnabledCheckBox.Visibility = Visibility.Collapsed;
        CustomerKindComboBox.IsEnabled = true;
        CustomerKindComboBox.SelectedValue = "Retail";
        PartyTypeComboBox.SelectedValue = "Person";
        TaxIdTypeComboBox.SelectedValue = "None";
        TaxConditionComboBox.SelectedValue = "NoAplica";
        DisplayNameTextBox.Text = string.Empty;
        TaxIdTextBox.Text = string.Empty;
        PhoneTextBox.Text = string.Empty;
        EmailTextBox.Text = string.Empty;
        AddressStreetTextBox.Text = string.Empty;
        AddressNumberTextBox.Text = string.Empty;
        NeighborhoodTextBox.Text = string.Empty;
        PostalCodeTextBox.Text = string.Empty;
        DeliveryNotesTextBox.Text = string.Empty;
        DiscountPercentageTextBox.Text = string.Empty;
        PaymentTermsTextBox.Text = string.Empty;
        NotesTextBox.Text = string.Empty;

        _fillingAddress = true;
        ProvinceComboBox.SelectedItem = null;
        CityComboBox.ItemsSource = null;
        _fillingAddress = false;
        _cityPostalCode = null;
        RenderEmailState();
    }

    private static string? NullIfBlank(string value) => string.IsNullOrWhiteSpace(value) ? null : value;
}
