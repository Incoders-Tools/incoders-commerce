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

    // ---- R3-toggle-stale-snapshot-overwrite: the toggle never sends the cached row -------------------------------

    private sealed class CustomersServer(Func<HttpRequestMessage, string?, HttpResponseMessage> respond) : HttpMessageHandler
    {
        public List<(HttpMethod Method, string Path, string? Body)> Requests { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var body = request.Content is null ? null : await request.Content.ReadAsStringAsync(cancellationToken);
            Requests.Add((request.Method, request.RequestUri!.AbsolutePath, body));
            return respond(request, body);
        }
    }

    private static readonly System.Text.Json.JsonSerializerOptions Web = new(System.Text.Json.JsonSerializerDefaults.Web);

    private static HttpResponseMessage Json(object body, System.Net.HttpStatusCode status = System.Net.HttpStatusCode.OK) =>
        new(status) { Content = new StringContent(System.Text.Json.JsonSerializer.Serialize(body, Web), System.Text.Encoding.UTF8, "application/json") };

    private static CustomerAdminClient AdminClient(CustomersServer server) =>
        new(new HttpClient(server) { BaseAddress = new Uri("https://cloud.invalid") });

    /// <summary>Ana as another user left her meanwhile: a new phone and notes, saved later than the cached row.</summary>
    private static readonly CustomerAdminRecordDto AnaChangedElsewhere = Ana with
    {
        Phone = "2901 999999", Notes = "Cambiado en la web", UpdatedAtUtc = new DateTimeOffset(2026, 10, 4, 15, 0, 0, TimeSpan.Zero),
    };

    [Fact]
    public async Task Toggle_RereadsTheCustomer_AndSendsTheFreshFields_WithOnlyIsEnabledChanged_AndTheReadVersion()
    {
        var server = new CustomersServer((request, _) => request.Method == HttpMethod.Get
            ? Json(AnaChangedElsewhere)
            : Json(AnaChangedElsewhere with { IsEnabled = false }));

        var outcome = await CustomerList.ToggleEnabledAsync(AdminClient(server), Ana);

        Assert.Equal(CustomerAdminMutationKind.Succeeded, outcome.Kind);
        Assert.Equal([(HttpMethod.Get, $"/customers/{Ana.Id}"), (HttpMethod.Put, $"/customers/{Ana.Id}")],
            server.Requests.Select(r => (r.Method, r.Path)));
        var sent = System.Text.Json.JsonSerializer.Deserialize<UpdateCustomerAdminRequestDto>(server.Requests[1].Body!, Web)!;
        Assert.Equal(
            CustomerList.ToggleEnabledRequest(AnaChangedElsewhere) with { ExpectedUpdatedAtUtc = AnaChangedElsewhere.UpdatedAtUtc },
            sent);
        Assert.Equal(("2901 999999", "Cambiado en la web", false), (sent.Phone, sent.Notes, sent.IsEnabled));
    }

    [Fact]
    public async Task Toggle_WhenSomeoneAlreadyLeftItInTheAskedState_WritesNothing()
    {
        var server = new CustomersServer((_, _) => Json(AnaChangedElsewhere with { IsEnabled = false }));

        var outcome = await CustomerList.ToggleEnabledAsync(AdminClient(server), Ana);

        Assert.Equal(CustomerAdminMutationKind.Succeeded, outcome.Kind);
        Assert.Equal([HttpMethod.Get], server.Requests.Select(r => r.Method));
    }

    [Fact]
    public async Task Toggle_ACustomerThatNoLongerExists_IsNotFound_AndNothingIsSent()
    {
        var server = new CustomersServer((_, _) => new HttpResponseMessage(System.Net.HttpStatusCode.NotFound));

        var outcome = await CustomerList.ToggleEnabledAsync(AdminClient(server), Ana);

        Assert.Equal(CustomerAdminMutationKind.NotFound, outcome.Kind);
        Assert.Equal([HttpMethod.Get], server.Requests.Select(r => r.Method));
    }

    [Fact]
    public async Task Toggle_AChangeBetweenTheReadAndTheWrite_IsRefusedByTheServer_WithAClearMessage()
    {
        var server = new CustomersServer((request, _) => request.Method == HttpMethod.Get
            ? Json(AnaChangedElsewhere)
            : Json(new { error = "customer-modified" }, System.Net.HttpStatusCode.Conflict));

        var outcome = await CustomerList.ToggleEnabledAsync(AdminClient(server), Ana);

        Assert.Equal(CustomerAdminMutationKind.Failed, outcome.Kind);
        Assert.Equal(PosMessages.CustomerModified, outcome.ErrorMessage);
    }
}
