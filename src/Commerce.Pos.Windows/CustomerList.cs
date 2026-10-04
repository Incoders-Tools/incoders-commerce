namespace Commerce.Pos.Windows;

/// <summary>
/// The Clientes list on the reusable entity list (operator-ux-adjustments T4): the columns, the search over name, tax
/// id, phone and city, the Estado and Tipo filters, and the Editar / Habilitar-Deshabilitar row actions. UI-free; the
/// screen supplies the toggle (it needs the connection and its busy controller).
/// </summary>
public static class CustomerList
{
    public const string ToggleEnabledAction = "toggle-enabled";

    public static EntityListDefinition<CustomerAdminRecordDto> Definition(Func<CustomerAdminRecordDto, Task> toggleEnabled) => new(
        customer => customer.Id,
        [
            new("name", "Nombre", customer => customer.DisplayName),
            new("partyType", "Tipo", PartyTypeLabel) { Width = 80 },
            new("taxId", "CUIT/DNI", customer => customer.TaxId) { Width = 116 },
            new("phone", "Teléfono", customer => customer.Phone) { Width = 108 },
            new("city", "Ciudad", customer => customer.CityName),
            new("priceList", "Lista de precios", customer => customer.PriceListName) { Width = 116 },
            new("state", "Estado", StateLabel) { Width = 104 },
        ],
        customer => [customer.DisplayName, customer.TaxId, customer.Phone, customer.CityName])
    {
        Filters =
        [
            new("state", "Estado",
            [
                new("Todos"),
                new("Habilitados", customer => customer.IsEnabled),
                new("Deshabilitados", customer => !customer.IsEnabled),
            ]),
            new("partyType", "Tipo",
            [
                new("Todos"),
                new("Persona", customer => customer.PartyType != "Company"),
                new("Empresa", customer => customer.PartyType == "Company"),
            ]),
        ],
        RowActions =
        [
            EntityRowAction<CustomerAdminRecordDto>.Edit(),
            new(ToggleEnabledAction, ToggleLabel, customer => customer.IsEnabled ? EntityIcons.Disable : EntityIcons.Enable)
            {
                Run = toggleEnabled,
                Confirmation = customer => $"¿{ToggleLabel(customer)} a {customer.DisplayName}?",
            },
        ],
        InitialSortKey = "name",
        SearchPlaceholder = "Buscar por nombre, CUIT/DNI, teléfono o ciudad…",
        NewTitle = "Nuevo cliente",
        EditTitle = customer => customer.DisplayName,
        EmptyEditorHint = "Elegí un cliente de la lista o tocá «Nuevo».",
    };

    /// <summary>
    /// The update Habilitar / Deshabilitar sends through the existing <c>PUT /customers/{id}</c>: the stored fields as
    /// they are, <c>IsEnabled</c> flipped, and a null city (which keeps the stored one, see
    /// <see cref="CustomerFormRules.CityChange"/>).
    /// </summary>
    public static UpdateCustomerAdminRequestDto ToggleEnabledRequest(CustomerAdminRecordDto customer) => new(
        customer.DisplayName, customer.TaxIdType, customer.TaxId, customer.TaxCondition, customer.Phone, customer.Email,
        customer.AddressStreet, customer.AddressNumber, customer.Neighborhood, customer.PostalCode,
        customer.DeliveryNotes, customer.DiscountPercentage, customer.PaymentTerms, customer.Notes,
        !customer.IsEnabled, CityId: null, customer.PartyType);

    private static string PartyTypeLabel(CustomerAdminRecordDto customer) =>
        CustomerFormChoices.PartyTypes.FirstOrDefault(c => c.Value == customer.PartyType)?.Label ?? customer.PartyType;

    private static string StateLabel(CustomerAdminRecordDto customer) => customer.IsEnabled ? "Habilitado" : "Deshabilitado";

    private static string ToggleLabel(CustomerAdminRecordDto customer) => customer.IsEnabled ? "Deshabilitar" : "Habilitar";
}
