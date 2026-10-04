using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Commerce.Domain.Identity;
using Commerce.Pos.Windows;
using Microsoft.Extensions.DependencyInjection;

namespace Commerce.Integration;

/// <summary>
/// admin-console-field-fixes T5 (POS) and T4: Clientes and Personal call the server with the paired device credential
/// plus the current operator through ONE shared management connection, with no password prompt; a server refusal of
/// the operator sends the shell back to the sale. The desktop customer form has one name field driven by Persona /
/// Empresa, Province -> City -> Postal code instead of Locality / free-text Province, and live email validation.
/// </summary>
[Collection("PosLog")]
public sealed class PosDeviceManagementTests : IDisposable
{
    private readonly string _dataDirectory = Path.Combine(Path.GetTempPath(), "commerce-pos-mgmt", Guid.NewGuid().ToString());

    public void Dispose()
    {
        if (Directory.Exists(_dataDirectory)) Directory.Delete(_dataDirectory, recursive: true);
    }

    private sealed class RecordingHandler(Func<HttpRequestMessage, HttpResponseMessage>? respond = null) : HttpMessageHandler
    {
        public List<HttpRequestMessage> Requests { get; } = [];
        public List<string?> Bodies { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests.Add(request);
            Bodies.Add(request.Content is null ? null : await request.Content.ReadAsStringAsync(cancellationToken));
            return respond?.Invoke(request) ?? Json(HttpStatusCode.OK, "[]");
        }
    }

    private static HttpResponseMessage Json(HttpStatusCode status, string body) =>
        new(status) { Content = new StringContent(body, Encoding.UTF8, "application/json") };

    private static string Src(params string[] path)
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "Commerce.sln"))) dir = dir.Parent;
        return File.ReadAllText(Path.Combine([dir!.FullName, "src", "Commerce.Pos.Windows", .. path]));
    }

    private static string? Header(HttpRequestMessage request, string name) =>
        request.Headers.TryGetValues(name, out var values) ? string.Join(",", values) : null;

    // ---- one management connection: device credential + current operator ------------------

    [Fact]
    public async Task Connection_SendsTheDeviceCredentialAndTheCurrentOperator_ReadAtSendTime()
    {
        var handler = new RecordingHandler();
        var token = "device-token-1";
        Guid? operatorId = Guid.NewGuid();
        using var connection = new ManagementConnection(handler, new Uri("https://cloud.invalid"), () => token, () => operatorId);

        await connection.Customers.ListCustomersAsync();
        var first = operatorId;
        operatorId = Guid.NewGuid();
        token = "device-token-2";
        await connection.Staff.ListUsersAsync();

        Assert.Equal("Bearer device-token-1", handler.Requests[0].Headers.Authorization!.ToString());
        Assert.Equal(first.ToString(), Header(handler.Requests[0], ManagementConnection.OperatorHeader));
        Assert.Equal("Bearer device-token-2", handler.Requests[1].Headers.Authorization!.ToString());
        Assert.Equal(operatorId.ToString(), Header(handler.Requests[1], ManagementConnection.OperatorHeader));
        Assert.Equal("X-Operator-Id", ManagementConnection.OperatorHeader);
    }

    [Fact]
    public async Task Connection_WithoutAnOperator_SendsNoOperatorHeader_AndNeverACookie()
    {
        var handler = new RecordingHandler();
        using var connection = new ManagementConnection(handler, new Uri("https://cloud.invalid"), () => "t", () => null);

        await connection.Customers.ListCustomersAsync();

        Assert.Null(Header(handler.Requests[0], ManagementConnection.OperatorHeader));
        Assert.Null(Header(handler.Requests[0], "Cookie"));
    }

    [Fact]
    public async Task Connection_RaisesOperatorRefused_OnlyForTheOperatorRefusal_NotForOtherForbiddenAnswers()
    {
        var answers = new Queue<HttpResponseMessage>(
        [
            Json(HttpStatusCode.Forbidden, """{"error":"permissions-exceed-caller"}"""),
            new HttpResponseMessage(HttpStatusCode.Forbidden),
            Json(HttpStatusCode.Forbidden, """{"error":"operator-not-authorized"}"""),
        ]);
        var handler = new RecordingHandler(_ => answers.Dequeue());
        using var connection = new ManagementConnection(handler, new Uri("https://cloud.invalid"), () => "t", () => Guid.NewGuid());
        var refused = 0;
        connection.OperatorRefused += () => refused++;
        var request = new CreateUserAdminRequestDto("a@b.c", "pw", ["cashier"], [Guid.NewGuid()]);

        var cap = await connection.Staff.CreateUserAsync(request);
        Assert.Equal(0, refused);
        Assert.Equal(PosMessages.PermissionsExceedCaller, cap.ErrorMessage);

        await connection.Staff.CreateUserAsync(request);
        Assert.Equal(0, refused);

        var operatorRefusal = await connection.Staff.CreateUserAsync(request);
        Assert.Equal(1, refused);
        Assert.Equal(PosMessages.OperatorNotAuthorized, operatorRefusal.ErrorMessage);
    }

    [Fact]
    public async Task Connection_OperatorRefusal_OnAList_IsRaisedToo()
    {
        var handler = new RecordingHandler(_ => Json(HttpStatusCode.Forbidden, """{"error":"operator-not-authorized"}"""));
        using var connection = new ManagementConnection(handler, new Uri("https://cloud.invalid"), () => "t", () => Guid.NewGuid());
        var refused = 0;
        connection.OperatorRefused += () => refused++;

        Assert.Null(await connection.Customers.ListCustomersAsync());
        Assert.Equal(1, refused);
    }

    [Fact]
    public void Composition_SharesOneManagementConnection_AcrossBothSections()
    {
        using var host = PosHostBuilder.Build(_dataDirectory);

        var first = host.Services.GetRequiredService<ManagementConnection>();
        Assert.Same(first, host.Services.GetRequiredService<ManagementConnection>());
        Assert.Same(first.Customers, host.Services.GetRequiredService<ManagementConnection>().Customers);
        Assert.Same(first.Staff, host.Services.GetRequiredService<ManagementConnection>().Staff);
    }

    // ---- no password prompt -----------------------------------------------------------------

    [Theory]
    [InlineData("CustomersView")]
    [InlineData("StaffView")]
    public void Sections_NoLongerAskForThePassword(string view)
    {
        var xaml = Src(view + ".xaml");
        var code = Src(view + ".xaml.cs");

        Assert.DoesNotContain("AdminSignInPanel", xaml);
        Assert.DoesNotContain("SignInPanel", code);
        Assert.DoesNotContain("SignInAsync", code);
        Assert.DoesNotContain("_adminClient.Dispose", code);
        Assert.DoesNotContain("_client.Dispose", code);
    }

    [Fact]
    public void Clients_HaveNoCookieSignIn_AndMessagesNoLongerMentionThePassword()
    {
        Assert.DoesNotContain("SignInAsync", Src("CustomerAdminClient.cs"));
        Assert.DoesNotContain("SignInAsync", Src("UserAdminClient.cs"));
        Assert.DoesNotContain("CookieContainer", Src("CustomerAdminClient.cs"));
        Assert.DoesNotContain("CookieContainer", Src("UserAdminClient.cs"));
        var messages = Src("PosMessages.cs");
        Assert.DoesNotContain("ConfirmPassword", messages);
        Assert.DoesNotContain("Confirmá tu contraseña", messages);
    }

    [Fact]
    public void MainWindow_LeavesTheSection_WhenTheServerRefusesTheOperator()
    {
        var code = Src("MainWindow.xaml.cs");
        var handler = Regex.Match(code, @"void OnManagementOperatorRefused\(\)[\s\S]*?\n    }");

        Assert.True(handler.Success);
        Assert.Contains("_shell.Leave(", handler.Value);
        Assert.Contains("PosMessages.ManagementAccessRefused", handler.Value);
        Assert.Contains("OperatorRefused += ", code);
        Assert.DoesNotContain("Func<CustomerAdminClient>", code);
        Assert.DoesNotContain("Func<UserAdminClient>", code);
    }

    [Fact]
    public void Leave_GoesBackToTheSale_AndDefersTheTeardownOfABusySection()
    {
        const int admin = (int)(Permission.OperatePos | Permission.ManageUsers);
        var shell = new ShellNavigation();
        Assert.Equal(ReconcileOutcome.Unchanged, shell.Leave(sectionBusy: false));

        Assert.True(shell.Navigate(ShellSection.Customers, admin));
        Assert.Equal(ReconcileOutcome.Switched, shell.Leave(sectionBusy: false));
        Assert.Equal(ShellSection.Sale, shell.Current);

        Assert.True(shell.Navigate(ShellSection.Staff, admin));
        Assert.Equal(ReconcileOutcome.Deferred, shell.Leave(sectionBusy: true));
        Assert.Equal(ShellSection.Sale, shell.Current);
        Assert.True(shell.TeardownPending);
        Assert.True(shell.CompleteTeardown());
    }

    // ---- T4: customer form ------------------------------------------------------------------

    [Fact]
    public async Task CreateAndUpdate_SendPartyTypeAndCity_AndNeverLegalNameLocalityOrProvince()
    {
        var handler = new RecordingHandler(_ => Json(HttpStatusCode.Created, """{"customerId":"00000000-0000-0000-0000-000000000001"}"""));
        using var connection = new ManagementConnection(handler, new Uri("https://cloud.invalid"), () => "t", () => Guid.NewGuid());
        var city = Guid.NewGuid();

        await connection.Customers.CreateCustomerAsync(new CreateCustomerAdminRequestDto(
            "Retail", "Acme SA", "Cuit", "20123456786", "ResponsableInscripto", null, "a@acme.com",
            "Mitre", "100", "Centro", "2000", null, null, null, null, city, "Company"));
        await connection.Customers.UpdateCustomerAsync(Guid.NewGuid(), new UpdateCustomerAdminRequestDto(
            "Ana Pérez", "None", null, "ConsumidorFinal", null, null, null, null, null, null, null, null, null, null,
            true, Guid.Empty, "Person"));

        foreach (var body in handler.Bodies)
        {
            using var json = JsonDocument.Parse(body!);
            var names = json.RootElement.EnumerateObject().Select(p => p.Name).ToArray();
            Assert.DoesNotContain("legalName", names);
            Assert.DoesNotContain("locality", names);
            Assert.DoesNotContain("province", names);
            Assert.Contains("partyType", names);
            Assert.Contains("cityId", names);
        }

        using var created = JsonDocument.Parse(handler.Bodies[0]!);
        Assert.Equal("Company", created.RootElement.GetProperty("partyType").GetString());
        Assert.Equal(city, created.RootElement.GetProperty("cityId").GetGuid());
    }

    [Fact]
    public async Task Provinces_AndCitiesOfAProvince_AreReadFromGeo_PagingThroughEveryCity()
    {
        var handler = new RecordingHandler(request =>
        {
            var uri = request.RequestUri!;
            if (uri.AbsolutePath == "/geo/provinces")
            {
                return Json(HttpStatusCode.OK, """[{"id":"82","isoCode":"AR-S","name":"Santa Fe","countryCode":"AR","countryName":"Argentina"}]""");
            }

            var offset = int.Parse(Regex.Match(uri.Query, @"offset=(\d+)").Groups[1].Value);
            var count = offset == 0 ? 200 : 3;
            var cities = Enumerable.Range(offset, count).Select(i =>
                $$"""{"id":"{{Guid.NewGuid()}}","name":"Ciudad {{i}}","provinceId":"82","provinceName":"Santa Fe","postalCode":{{(i == 0 ? "\"2000\"" : "null")}}}""");
            return Json(HttpStatusCode.OK, "[" + string.Join(",", cities) + "]");
        });
        using var connection = new ManagementConnection(handler, new Uri("https://cloud.invalid"), () => "t", () => Guid.NewGuid());

        var provinces = await connection.Customers.ListProvincesAsync();
        var cities = await connection.Customers.ListCitiesAsync("82");

        Assert.Equal("Santa Fe", Assert.Single(provinces!).Name);
        Assert.Equal(203, cities!.Count);
        Assert.Equal("2000", cities[0].PostalCode);
        Assert.All(handler.Requests.Skip(1), r => Assert.Contains("provinceId=82", r.RequestUri!.Query));
        Assert.Equal(3, handler.Requests.Count);
    }

    [Theory]
    [InlineData("Person", "Nombre y apellido")]
    [InlineData("Company", "Razón social")]
    public void NameLabel_FollowsThePartyType(string partyType, string label)
    {
        Assert.Equal(label, CustomerFormRules.NameLabel(partyType));
        Assert.Equal(["Person", "Company"], CustomerFormChoices.PartyTypes.Select(c => c.Value));
        Assert.Equal(["Persona", "Empresa"], CustomerFormChoices.PartyTypes.Select(c => c.Label));
    }

    [Theory]
    [InlineData("", EmailFieldState.Empty)]
    [InlineData("   ", EmailFieldState.Empty)]
    [InlineData("ana@", EmailFieldState.Invalid)]
    [InlineData("ana@mail", EmailFieldState.Invalid)]
    [InlineData("ana@mail.com", EmailFieldState.Valid)]
    [InlineData(" ana@mail.com ", EmailFieldState.Valid)]
    public void Email_IsCheckedWithTheSharedRule(string text, EmailFieldState expected)
    {
        Assert.Equal(expected, CustomerFormRules.Email(text));
    }

    [Theory]
    // A known postal code fills an empty box or replaces the one the previous city filled in.
    [InlineData("", null, "2000", "2000")]
    [InlineData("3000", "3000", "2000", "2000")]
    // A postal code typed by hand is kept; an unknown one never clears the box.
    [InlineData("2001", "3000", "2000", "2001")]
    [InlineData("2001", null, null, "2001")]
    [InlineData("3000", "3000", null, "3000")]
    public void PostalCode_IsPrefilledFromTheCity_WhenKnown_AndStaysEditable(
        string current, string? previousCityPostalCode, string? newCityPostalCode, string expected)
    {
        Assert.Equal(expected, CustomerFormRules.PostalCodeAfterCityChange(current, previousCityPostalCode, newCityPostalCode));
    }

    [Fact]
    public void CityChange_KeepsTheStoredCity_WhenTheOperatorNeverTouchedAnUnselectableOne()
    {
        var picked = Guid.NewGuid();

        // A stored city that cannot be reselected (deactivated, or the city list failed to load) is kept, not cleared.
        Assert.Null(CustomerFormRules.CityChange(selectedCityId: null, operatorChangedCity: false));
        // The operator cleared the city or changed the province: the city is cleared.
        Assert.Equal(Guid.Empty, CustomerFormRules.CityChange(selectedCityId: null, operatorChangedCity: true));
        // A selected city is always sent.
        Assert.Equal(picked, CustomerFormRules.CityChange(selectedCityId: picked, operatorChangedCity: false));
        Assert.Equal(picked, CustomerFormRules.CityChange(selectedCityId: picked, operatorChangedCity: true));
    }

    [Fact]
    public void CustomersView_SendsTheCityChange_NotAnUnconditionalClear()
    {
        var code = Src("CustomersView.xaml.cs");

        Assert.Contains("CustomerFormRules.CityChange(", code);
        Assert.DoesNotContain("cityId ?? Guid.Empty", code);
    }

    [Fact]
    public void CustomersView_HasTheNewNameAddressAndEmailFields()
    {
        var xaml = Src("CustomersView.xaml");
        var code = Src("CustomersView.xaml.cs");

        foreach (var removed in new[] { "LegalNameTextBox", "LocalityTextBox", "ProvinceTextBox", "Localidad" })
        {
            Assert.DoesNotContain(removed, xaml);
            Assert.DoesNotContain(removed, code);
        }

        foreach (var name in new[] { "PartyTypeComboBox", "NameLabel", "ProvinceComboBox", "CityComboBox", "PostalCodeTextBox", "EmailValidIcon", "EmailErrorText" })
        {
            Assert.Contains($"x:Name=\"{name}\"", xaml);
        }

        Assert.Contains("CustomerFormRules.Email(", code);
        Assert.Contains("CustomerFormRules.PostalCodeAfterCityChange(", code);
        Assert.Contains("ListProvincesAsync", code);
        Assert.Contains("ListCitiesAsync", code);
    }
}
