using Commerce.Pos.Windows;

namespace Commerce.Integration;

/// <summary>
/// The Clientes list on the reusable entity list (operator-ux-adjustments T4): its columns, the fields the search
/// covers, the Estado / Tipo filters, the row actions, and the update an Habilitar / Deshabilitar sends.
/// </summary>
public sealed class PosCustomerListTests
{
    private static CustomerAdminRecordDto Customer(
        string name, string partyType = "Person", bool enabled = true, string? taxId = null, string? phone = null,
        string? city = null, string? priceList = null) =>
        new(Guid.NewGuid(), Guid.NewGuid(), "Retail", name, taxId is null ? "None" : "Cuit", taxId, "ConsumidorFinal",
            phone, "ana@correo.com", "San Martín", "123", "Centro", "9410", "Timbre 2", 5m, "30 días", "Nota",
            enabled, DateTimeOffset.UnixEpoch, Guid.NewGuid(), DateTimeOffset.UnixEpoch,
            CityId: city is null ? null : Guid.NewGuid(), CityName: city, ProvinceId: "94", ProvinceName: "Tierra del Fuego",
            PartyType: partyType, PriceListName: priceList);

    private static readonly CustomerAdminRecordDto Ana =
        Customer("Ana Pérez", taxId: "27-11122233-4", phone: "2901 444555", city: "Ushuaia", priceList: "Minorista");

    private static readonly CustomerAdminRecordDto Frigorifico =
        Customer("Frigorífico Sur SA", partyType: "Company", enabled: false, taxId: "30-99887766-5", phone: "2964 111222", city: "Río Grande");

    private static EntityListModel<CustomerAdminRecordDto> Model(Func<CustomerAdminRecordDto, Task>? toggle = null)
    {
        var model = new EntityListModel<CustomerAdminRecordDto>(CustomerList.Definition(toggle ?? (_ => Task.CompletedTask)));
        model.SetItems([Frigorifico, Ana]);
        return model;
    }

    [Fact]
    public void Columns_AreTheAgreedOnes_AllSortable_StartingByName()
    {
        var model = Model();
        var columns = ((IEntityListModel)model).Columns;

        Assert.Equal(["Nombre", "Tipo", "CUIT/DNI", "Teléfono", "Ciudad", "Lista de precios", "Estado"], columns.Select(c => c.Header));
        Assert.All(columns, c => Assert.True(c.Sortable, c.Header));
        Assert.Equal("▲", columns[0].SortIndicator);
        Assert.Equal(["Ana Pérez", "Frigorífico Sur SA"], model.Visible.Select(c => c.DisplayName));
    }

    [Fact]
    public void Cells_ShowTheSpanishLabels()
    {
        var rows = ((IEntityListModel)Model()).Rows;

        Assert.Equal(["Ana Pérez", "Persona", "27-11122233-4", "2901 444555", "Ushuaia", "Minorista", "Habilitado"], rows[0].Cells);
        Assert.Equal(["Frigorífico Sur SA", "Empresa", "30-99887766-5", "2964 111222", "Río Grande", string.Empty, "Deshabilitado"], rows[1].Cells);
    }

    [Theory]
    [InlineData("frigorifico")]
    [InlineData("30998877665")]
    [InlineData("2964111222")]
    [InlineData("rio grande")]
    public void Search_CoversNameTaxIdPhoneAndCity(string search)
    {
        var model = Model();

        model.SearchText = search;

        Assert.Equal(["Frigorífico Sur SA"], model.Visible.Select(c => c.DisplayName));
    }

    [Fact]
    public void Search_DoesNotMatchFieldsOutsideTheList()
    {
        var model = Model();

        model.SearchText = "Timbre";

        Assert.Empty(model.Visible);
    }

    [Fact]
    public void Filters_AreEstadoAndTipo()
    {
        var model = Model();
        var filters = ((IEntityListModel)model).Filters;

        Assert.Equal(["Estado", "Tipo"], filters.Select(f => f.Label));
        Assert.Equal(["Todos", "Habilitados", "Deshabilitados"], filters[0].Options);
        Assert.Equal(["Todos", "Persona", "Empresa"], filters[1].Options);

        model.SelectFilterOption(filters[0].Key, 2);
        Assert.Equal(["Frigorífico Sur SA"], model.Visible.Select(c => c.DisplayName));
        model.SelectFilterOption(filters[0].Key, 0);

        model.SelectFilterOption(filters[1].Key, 1);
        Assert.Equal(["Ana Pérez"], model.Visible.Select(c => c.DisplayName));
        model.SelectFilterOption(filters[1].Key, 2);
        Assert.Equal(["Frigorífico Sur SA"], model.Visible.Select(c => c.DisplayName));
    }

    [Fact]
    public void RowActions_AreEditarAndHabilitarOrDeshabilitar()
    {
        var rows = ((IEntityListModel)Model()).Rows;

        Assert.Equal(["Editar", "Deshabilitar"], rows[0].Actions.Select(a => a.Label));
        Assert.Equal(["Editar", "Habilitar"], rows[1].Actions.Select(a => a.Label));
        Assert.All(rows.SelectMany(r => r.Actions), a => Assert.True(a.IsEnabled));
    }

    [Fact]
    public async Task Editar_OpensTheEditorOnTheCustomer()
    {
        var model = Model();

        await model.InvokeAsync(Frigorifico, "edit");

        Assert.Equal(EntityEditorMode.Edit, model.EditorMode);
        Assert.Same(Frigorifico, model.Editing);
        Assert.Equal("Frigorífico Sur SA", model.EditorTitle);
    }

    [Fact]
    public void Nuevo_OpensAnEmptyEditorTitledNuevoCliente()
    {
        var model = Model();

        model.BeginNew();

        Assert.Equal("Nuevo cliente", model.EditorTitle);
        Assert.Equal("Nuevo", ((IEntityListModel)model).NewLabel);
    }

    [Theory]
    [InlineData(true, "¿Deshabilitar a Ana Pérez?", "Deshabilitar")]
    [InlineData(false, "¿Habilitar a Ana Pérez?", "Habilitar")]
    public async Task HabilitarDeshabilitar_AsksFirst_ThenRunsTheToggle(bool enabled, string question, string confirmLabel)
    {
        var ran = new List<CustomerAdminRecordDto>();
        var model = Model(toggle: c => { ran.Add(c); return Task.CompletedTask; });
        var customer = Ana with { IsEnabled = enabled };

        await model.InvokeAsync(customer, CustomerList.ToggleEnabledAction);

        Assert.Empty(ran);
        Assert.Equal(new EntityConfirmation(question, confirmLabel), model.PendingConfirmation);
        await model.ConfirmAsync();
        Assert.Equal([customer], ran);
    }

    [Fact]
    public void ToggleEnabledRequest_FlipsOnlyIsEnabled_AndKeepsTheStoredCity()
    {
        var request = CustomerList.ToggleEnabledRequest(Ana);

        Assert.Equal(new UpdateCustomerAdminRequestDto(
            "Ana Pérez", "Cuit", "27-11122233-4", "ConsumidorFinal", "2901 444555", "ana@correo.com",
            "San Martín", "123", "Centro", "9410", "Timbre 2", 5m, "30 días", "Nota",
            IsEnabled: false, CityId: null, PartyType: "Person"), request);
        Assert.True(CustomerList.ToggleEnabledRequest(Frigorifico).IsEnabled);
        Assert.Equal("Company", CustomerList.ToggleEnabledRequest(Frigorifico).PartyType);
    }
}
