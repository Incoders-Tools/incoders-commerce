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

        Assert.Equal(CustomerAdminMutationKind.Modified, outcome.Kind);
        Assert.Equal(PosMessages.CustomerModified, outcome.ErrorMessage);
    }

    // ---- T7 (b): only a 409 customer-modified means "someone saved it meanwhile" -------------------------------

    private static async Task<CustomerAdminMutationOutcome> UpdateAnsweredWith(HttpResponseMessage answer) =>
        await AdminClient(new CustomersServer((_, _) => answer)).UpdateCustomerAsync(Ana.Id, CustomerList.ToggleEnabledRequest(Ana));

    [Fact]
    public async Task A409CustomerModified_IsModified_WithTheCustomerModifiedMessage()
    {
        var outcome = await UpdateAnsweredWith(Json(new { error = "customer-modified" }, System.Net.HttpStatusCode.Conflict));

        Assert.Equal((CustomerAdminMutationKind.Modified, PosMessages.CustomerModified), (outcome.Kind, outcome.ErrorMessage));
    }

    [Fact]
    public async Task AnyOther409_ShowsTheServersMessage_OrItsError_OrAGenericConflict()
    {
        var withMessage = await UpdateAnsweredWith(
            Json(new { error = "tax-id-taken", message = "Ya existe un cliente con ese CUIT." }, System.Net.HttpStatusCode.Conflict));
        var withError = await UpdateAnsweredWith(Json(new { error = "tax-id-taken" }, System.Net.HttpStatusCode.Conflict));
        var empty = await UpdateAnsweredWith(new HttpResponseMessage(System.Net.HttpStatusCode.Conflict));
        var notJson = await UpdateAnsweredWith(new HttpResponseMessage(System.Net.HttpStatusCode.Conflict)
        {
            Content = new StringContent("<html>conflict</html>", System.Text.Encoding.UTF8, "text/html"),
        });

        Assert.Equal((CustomerAdminMutationKind.Failed, "Ya existe un cliente con ese CUIT."), (withMessage.Kind, withMessage.ErrorMessage));
        Assert.Equal((CustomerAdminMutationKind.Failed, "tax-id-taken"), (withError.Kind, withError.ErrorMessage));
        Assert.Equal((CustomerAdminMutationKind.Failed, PosMessages.Conflict), (empty.Kind, empty.ErrorMessage));
        Assert.Equal((CustomerAdminMutationKind.Failed, PosMessages.Conflict), (notJson.Kind, notJson.ErrorMessage));
        Assert.NotEqual(PosMessages.CustomerModified, PosMessages.Conflict);
    }

    [Fact]
    public async Task ASuccessfulUpdate_CarriesTheCustomerAsTheServerSavedIt()
    {
        var saved = AnaChangedElsewhere with { IsEnabled = false };

        var outcome = await UpdateAnsweredWith(Json(saved));

        Assert.Equal(CustomerAdminMutationKind.Succeeded, outcome.Kind);
        Assert.Equal(saved, outcome.Customer);
    }

    // ---- T7 (c): the form's Save sends the loaded version and reloads the customer on a 409 -------------------

    private static readonly DateTimeOffset AnaLoadedAt = new(2026, 10, 4, 12, 0, 0, TimeSpan.Zero);

    private static UpdateCustomerAdminRequestDto FormRequest => CustomerList.ToggleEnabledRequest(Ana) with { Phone = "2901 000000" };

    [Fact]
    public async Task SavingAnEdit_SendsTheLoadedRecordsUpdatedAtUtc_AndReturnsTheSavedCustomer()
    {
        var saved = Ana with { Phone = "2901 000000", UpdatedAtUtc = AnaLoadedAt.AddMinutes(5) };
        var server = new CustomersServer((_, _) => Json(saved));

        var result = await CustomerFormSave.UpdateAsync(AdminClient(server), Ana.Id, AnaLoadedAt, FormRequest);

        Assert.Equal(CustomerAdminMutationKind.Succeeded, result.Outcome.Kind);
        Assert.Equal([(HttpMethod.Put, $"/customers/{Ana.Id}")], server.Requests.Select(r => (r.Method, r.Path)));
        var sent = System.Text.Json.JsonSerializer.Deserialize<UpdateCustomerAdminRequestDto>(server.Requests[0].Body!, Web)!;
        Assert.Equal(FormRequest with { ExpectedUpdatedAtUtc = AnaLoadedAt }, sent);
        // The next Save of the still open form must carry the version this save produced.
        Assert.Equal(saved, result.Current);
    }

    [Fact]
    public async Task SavingAnEdit_ThatSomeoneElseSavedMeanwhile_ReloadsTheCustomer_AndSaysSo()
    {
        var server = new CustomersServer((request, _) => request.Method == HttpMethod.Put
            ? Json(new { error = "customer-modified" }, System.Net.HttpStatusCode.Conflict)
            : Json(AnaChangedElsewhere));

        var result = await CustomerFormSave.UpdateAsync(AdminClient(server), Ana.Id, AnaLoadedAt, FormRequest);

        Assert.Equal([(HttpMethod.Put, $"/customers/{Ana.Id}"), (HttpMethod.Get, $"/customers/{Ana.Id}")],
            server.Requests.Select(r => (r.Method, r.Path)));
        Assert.Equal(CustomerAdminMutationKind.Modified, result.Outcome.Kind);
        Assert.Equal(PosMessages.CustomerReloadedAfterConflict, result.Outcome.ErrorMessage);
        Assert.Equal(AnaChangedElsewhere, result.Current);
    }

    [Fact]
    public async Task SavingAnEdit_ThatConflicts_WhenTheReloadFails_KeepsTheFormAsIs_WithTheModifiedMessage()
    {
        var server = new CustomersServer((request, _) => request.Method == HttpMethod.Put
            ? Json(new { error = "customer-modified" }, System.Net.HttpStatusCode.Conflict)
            : new HttpResponseMessage(System.Net.HttpStatusCode.InternalServerError));

        var result = await CustomerFormSave.UpdateAsync(AdminClient(server), Ana.Id, AnaLoadedAt, FormRequest);

        Assert.Equal((CustomerAdminMutationKind.Modified, PosMessages.CustomerModified), (result.Outcome.Kind, result.Outcome.ErrorMessage));
        Assert.Null(result.Current);
    }

    [Fact]
    public async Task SavingAnEdit_ThatFailsOtherwise_DoesNotReload()
    {
        var server = new CustomersServer((_, _) => new HttpResponseMessage(System.Net.HttpStatusCode.BadRequest));

        var result = await CustomerFormSave.UpdateAsync(AdminClient(server), Ana.Id, AnaLoadedAt, FormRequest);

        Assert.Equal((CustomerAdminMutationKind.Failed, PosMessages.InvalidData), (result.Outcome.Kind, result.Outcome.ErrorMessage));
        Assert.Equal([HttpMethod.Put], server.Requests.Select(r => r.Method));
        Assert.Null(result.Current);
    }

    [Theory]
    [InlineData(true, true, "saved")] // the form was current when the toggle wrote: it follows the toggle's version
    [InlineData(false, true, "loaded")] // the form was older than the toggled row: a later Save must still be refused
    [InlineData(true, false, "loaded")] // the toggle wrote nothing
    public void AToggleOnTheOpenCustomer_MovesTheFormsVersion_OnlyWhenTheFormWasCurrent(bool formCurrent, bool wrote, string expected)
    {
        var row = Ana with { UpdatedAtUtc = AnaLoadedAt };
        var loaded = formCurrent ? AnaLoadedAt : AnaLoadedAt.AddMinutes(-10);
        var saved = wrote ? row with { IsEnabled = false, UpdatedAtUtc = AnaLoadedAt.AddMinutes(1) } : null;

        var version = CustomerFormSave.VersionAfterToggle(loaded, row, saved);

        Assert.Equal(expected == "saved" ? saved!.UpdatedAtUtc : loaded, version);
    }

    // ---- T7 (d): an unreachable server is not a server answer: no list reload ---------------------------------

    private sealed class UnreachableServer : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            throw new HttpRequestException("no route to host");
    }

    [Fact]
    public async Task Toggle_WhenTheServerIsUnreachable_IsUnreachable_WithTheOfflineMessage()
    {
        var client = new CustomerAdminClient(new HttpClient(new UnreachableServer()) { BaseAddress = new Uri("https://cloud.invalid") });

        var outcome = await CustomerList.ToggleEnabledAsync(client, Ana);

        Assert.Equal((CustomerAdminMutationKind.Unreachable, PosMessages.ServerUnreachable), (outcome.Kind, outcome.ErrorMessage));
        Assert.False(CustomerList.ReloadAfterFailedToggle(outcome));
    }

    [Fact]
    public void AFailedToggle_ReloadsTheList_OnlyAfterAServerAnswer()
    {
        Assert.True(CustomerList.ReloadAfterFailedToggle(CustomerAdminMutationOutcome.NotFound()));
        Assert.True(CustomerList.ReloadAfterFailedToggle(CustomerAdminMutationOutcome.Modified()));
        Assert.True(CustomerList.ReloadAfterFailedToggle(CustomerAdminMutationOutcome.Failed(PosMessages.Conflict)));
        Assert.False(CustomerList.ReloadAfterFailedToggle(CustomerAdminMutationOutcome.Unreachable()));
    }
}
