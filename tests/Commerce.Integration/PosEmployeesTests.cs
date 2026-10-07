using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Commerce.Pos.Windows;

namespace Commerce.Integration;

/// <summary>
/// Personal → Empleados at the POS, aligned with the web's Personal (docs/architecture/cross-layer-parity.md): the
/// requests of <see cref="EmployeeAdminClient"/> (the same <c>/employees</c> endpoints), the Spanish answers, the list's
/// columns / filters / row actions in the web's words, the form rules the web applies too, and the section's structure
/// (two tabs: Empleados and Usuarios y acceso).
/// </summary>
[Collection("PosLog")]
public sealed class PosEmployeesTests
{
    private sealed class RecordingHandler(HttpStatusCode status, string? body = null) : HttpMessageHandler
    {
        public HttpMethod? Method { get; private set; }
        public string? PathAndQuery { get; private set; }
        public string? Body { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Method = request.Method;
            PathAndQuery = request.RequestUri!.PathAndQuery;
            Body = request.Content is null ? null : await request.Content.ReadAsStringAsync(cancellationToken);
            var response = new HttpResponseMessage(status);
            if (body is not null)
            {
                response.Content = new StringContent(body, Encoding.UTF8, "application/json");
            }

            return response;
        }
    }

    private static EmployeeAdminClient ClientFor(RecordingHandler handler) =>
        new(new HttpClient(handler) { BaseAddress = new Uri("https://cloud.invalid") });

    private static string Src(params string[] path) =>
        File.ReadAllText(Path.Combine([RepoRoot(), "src", "Commerce.Pos.Windows", .. path]));

    private static readonly Guid Branch = Guid.NewGuid();

    private static EmployeeRecordDto Employee(
        string last, string first, int file = 1, bool active = true, decimal balance = 0m, Guid? customerId = null,
        decimal purchases = 0m, string frequency = "Monthly", decimal salary = 650_000m, string? role = "Carnicero") =>
        new(Guid.NewGuid(), Branch, "Centro", file, first, last, $"{last}, {first}", "30111222", "27301112223", null, role,
            null, null, null, new DateOnly(2024, 3, 15), null, frequency, salary, customerId, null, active,
            DateTimeOffset.UnixEpoch, DateTimeOffset.UnixEpoch, balance, purchases);

    // ---- client: the same endpoints as the web -------------------------------------------

    [Fact]
    public async Task List_AsksForThisBranchsStaff_TheOnesDadosDeBajaIncluded()
    {
        var handler = new RecordingHandler(HttpStatusCode.OK, "[]");

        var employees = await ClientFor(handler).ListEmployeesAsync(Branch);

        Assert.Empty(employees!);
        Assert.Equal(HttpMethod.Get, handler.Method);
        Assert.Equal($"/employees?branchId={Branch}&includeInactive=true", handler.PathAndQuery);
    }

    [Fact]
    public async Task List_ReadsTheServersWireShape()
    {
        var id = Guid.NewGuid();
        var handler = new RecordingHandler(HttpStatusCode.OK, $$"""
            [{"id":"{{id}}","branchId":"{{Branch}}","branchName":"Centro","fileNumber":7,"firstName":"Ana","lastName":"Pérez",
              "fullName":"Pérez, Ana","documentNumber":null,"cuil":null,"roleId":null,"roleName":null,"phone":null,"email":null,
              "address":null,"hireDate":"2024-03-15","terminationDate":null,"payFrequency":"Biweekly","baseSalary":320000.5,
              "customerId":null,"notes":null,"isActive":true,"createdAtUtc":"2026-10-01T00:00:00Z","updatedAtUtc":"2026-10-01T00:00:00Z",
              "balance":-50000,"purchasesOwed":0}]
            """);

        var employee = Assert.Single((await ClientFor(handler).ListEmployeesAsync(Branch))!);

        Assert.Equal((id, 7, "Pérez, Ana", "Biweekly", 320_000.5m, -50_000m), (employee.Id, employee.FileNumber, employee.FullName,
            employee.PayFrequency, employee.BaseSalary, employee.Balance));
        Assert.Equal(new DateOnly(2024, 3, 15), employee.HireDate);
    }

    [Fact]
    public async Task Create_PostsTheStaffFile_AndReturnsTheSavedEmployee()
    {
        var saved = Employee("Pérez", "Ana", file: 12);
        var handler = new RecordingHandler(HttpStatusCode.Created, JsonSerializer.Serialize(saved, JsonSerializerOptions.Web));
        var request = new EmployeeRequestDto(Branch, null, "Ana", "Pérez", null, null, null, null, null, null,
            new DateOnly(2024, 3, 15), "Monthly", 650_000m, null, TakesGoods: true);

        var outcome = await ClientFor(handler).CreateEmployeeAsync(request);

        Assert.Equal(EmployeeAdminMutationKind.Succeeded, outcome.Kind);
        Assert.Equal(12, outcome.Employee!.FileNumber);
        Assert.Equal(HttpMethod.Post, handler.Method);
        Assert.Equal("/employees", handler.PathAndQuery);
        using var body = JsonDocument.Parse(handler.Body!);
        Assert.Equal(Branch, body.RootElement.GetProperty("branchId").GetGuid());
        Assert.Equal(JsonValueKind.Null, body.RootElement.GetProperty("fileNumber").ValueKind);
        Assert.Equal("2024-03-15", body.RootElement.GetProperty("hireDate").GetString());
        Assert.True(body.RootElement.GetProperty("takesGoods").GetBoolean());
    }

    [Fact]
    public async Task Update_PutsToTheEmployee()
    {
        var id = Guid.NewGuid();
        var handler = new RecordingHandler(HttpStatusCode.OK, JsonSerializer.Serialize(Employee("Pérez", "Ana"), JsonSerializerOptions.Web));

        var outcome = await ClientFor(handler).UpdateEmployeeAsync(id, new EmployeeRequestDto(
            Branch, 3, "Ana", "Pérez", null, null, null, null, null, null, null, "Weekly", 1m, null, false));

        Assert.Equal(EmployeeAdminMutationKind.Succeeded, outcome.Kind);
        Assert.Equal(HttpMethod.Put, handler.Method);
        Assert.Equal($"/employees/{id}", handler.PathAndQuery);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task SetActive_PostsTheFlag_TheServerDatesTheTermination(bool isActive)
    {
        var id = Guid.NewGuid();
        var handler = new RecordingHandler(HttpStatusCode.NoContent);

        var outcome = await ClientFor(handler).SetActiveAsync(id, isActive);

        Assert.Equal(EmployeeAdminMutationKind.Succeeded, outcome.Kind);
        Assert.Equal(HttpMethod.Post, handler.Method);
        Assert.Equal($"/employees/{id}/active", handler.PathAndQuery);
        Assert.Equal(isActive ? """{"isActive":true}""" : """{"isActive":false}""", handler.Body);
    }

    [Fact]
    public async Task Advance_PostsAmountAccountDateAndConcept()
    {
        var (id, account) = (Guid.NewGuid(), Guid.NewGuid());
        var handler = new RecordingHandler(HttpStatusCode.OK, """{"advanceId":"00000000-0000-0000-0000-000000000001"}""");

        var outcome = await ClientFor(handler).GiveAdvanceAsync(id, new EmployeeAdvanceRequestDto(50_000m, new DateOnly(2026, 10, 7), account, "A cuenta"));

        Assert.Equal(EmployeeAdminMutationKind.Succeeded, outcome.Kind);
        Assert.Equal($"/employees/{id}/advances", handler.PathAndQuery);
        Assert.Equal($$"""{"amount":50000,"date":"2026-10-07","accountId":"{{account}}","concept":"A cuenta"}""", handler.Body);
    }

    [Fact]
    public async Task RolesAccountsAndStatement_ReadTheirEndpoints()
    {
        var roles = new RecordingHandler(HttpStatusCode.OK, """[{"id":"00000000-0000-0000-0000-000000000001","name":"Cajero","isActive":false}]""");
        Assert.False(Assert.Single((await ClientFor(roles).ListRolesAsync())!).IsActive);
        Assert.Equal("/employees/roles?includeInactive=true", roles.PathAndQuery);

        var accounts = new RecordingHandler(HttpStatusCode.OK, "[]");
        Assert.Empty((await ClientFor(accounts).ListTreasuryAccountsAsync())!);
        Assert.Equal("/treasury/accounts", accounts.PathAndQuery);

        var id = Guid.NewGuid();
        var statement = new RecordingHandler(HttpStatusCode.OK, """
            {"openingBalance":0,"movements":[{"id":"00000000-0000-0000-0000-000000000002","kind":"Payment","direction":"Debit",
              "amount":50000,"occurredOn":"2026-10-07","dueOn":null,"documentReference":null,"concept":"Adelanto",
              "reversesMovementId":null,"createdAtUtc":"2026-10-07T12:00:00Z","runningBalance":-50000,"reversed":false,
              "reversedByMovementId":null}],"closingBalance":-50000}
            """);
        var read = await ClientFor(statement).GetStatementAsync(id);
        Assert.Equal(-50_000m, read!.ClosingBalance);
        Assert.Equal($"/employees/{id}/account/statement", statement.PathAndQuery);
    }

    [Theory]
    [InlineData(HttpStatusCode.Conflict, "file-number-in-use", PosMessages.EmployeeFileNumberInUse)]
    [InlineData(HttpStatusCode.Conflict, "role-not-found", PosMessages.EmployeeRoleNotFound)]
    [InlineData(HttpStatusCode.Conflict, "branch-not-found", PosMessages.EmployeeBranchNotFound)]
    [InlineData(HttpStatusCode.Conflict, "account-not-found", PosMessages.TreasuryAccountNotFound)]
    [InlineData(HttpStatusCode.Conflict, "account-inactive", PosMessages.TreasuryAccountInactive)]
    [InlineData(HttpStatusCode.Conflict, "something-new", PosMessages.Conflict)]
    [InlineData(HttpStatusCode.NotFound, null, PosMessages.EmployeeNotFound)]
    [InlineData(HttpStatusCode.BadRequest, null, PosMessages.InvalidData)]
    [InlineData(HttpStatusCode.Forbidden, null, PosMessages.NoPermissionToManageStaff)]
    [InlineData(HttpStatusCode.Forbidden, "operator-not-authorized", PosMessages.OperatorNotAuthorized)]
    [InlineData(HttpStatusCode.Unauthorized, null, PosMessages.TerminalNotRecognized)]
    public async Task Writes_MapTheServerAnswerToTheWebsSpanish(HttpStatusCode status, string? code, string expected)
    {
        var handler = new RecordingHandler(status, code is null ? null : $$"""{"error":"{{code}}"}""");

        var outcome = await ClientFor(handler).SetActiveAsync(Guid.NewGuid(), false);

        Assert.NotEqual(EmployeeAdminMutationKind.Succeeded, outcome.Kind);
        Assert.Equal(expected, outcome.ErrorMessage);
    }

    [Fact]
    public async Task List_IsNull_WhenTheServerCannotBeRead()
    {
        Assert.Null(await ClientFor(new RecordingHandler(HttpStatusCode.InternalServerError)).ListEmployeesAsync(Branch));
    }

    [Fact]
    public void TheConflictTexts_AreTheWebsOnes()
    {
        using var web = JsonDocument.Parse(File.ReadAllText(Path.Combine(
            RepoRoot(), "src", "Commerce.Web", "src", "i18n", "locales", "es", "employees.json")));
        var codes = web.RootElement.GetProperty("errors").GetProperty("codes");
        foreach (var code in codes.EnumerateObject())
        {
            Assert.Equal(code.Value.GetString(), EmployeeAdminClient.ConflictMessage(code.Name));
        }

        Assert.Equal(PosMessages.EmployeeNameRequired, web.RootElement.GetProperty("form").GetProperty("errors").GetProperty("name").GetString());
        Assert.Equal(PosMessages.AdvanceAmountInvalid, web.RootElement.GetProperty("advance").GetProperty("errors").GetProperty("amount").GetString());
    }

    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "Commerce.sln"))) dir = dir.Parent;
        return dir!.FullName;
    }

    // ---- the list, in the web's words -----------------------------------------------------

    private static (EmployeeList List, EmployeeRecordDto Ana, EmployeeRecordDto Bruno, EmployeeRecordDto Carla) List()
    {
        var ana = Employee("Pérez", "Ana", file: 2, balance: 120_000m, customerId: Guid.NewGuid(), purchases: 15_500m);
        var bruno = Employee("Acosta", "Bruno", file: 10, balance: -50_000m, frequency: "Weekly", salary: 150_000m, role: null);
        var carla = Employee("Zárate", "Carla", file: 5, active: false);
        var list = new EmployeeList(_ => Task.CompletedTask);
        list.Model.SetItems([ana, bruno, carla]);
        return (list, ana, bruno, carla);
    }

    [Fact]
    public void Columns_AreTheWebsOnes_StartingByName_ActiveOnly()
    {
        var (list, _, _, _) = List();
        var model = list.Model;

        Assert.Equal(["Legajo", "Apellido y nombre", "Puesto", "Sueldo pactado", "Cuenta", "Mercadería a descontar", "Estado"],
            model.Columns.Select(c => c.Header));
        Assert.Equal(["Acosta, Bruno", "Pérez, Ana"], list.Model.Visible.Select(e => e.FullName));
    }

    [Fact]
    public void Cells_ReadLikeTheWeb()
    {
        var (list, _, _, _) = List();
        var rows = list.Model.Rows;

        Assert.Equal(["10", "Acosta, Bruno", string.Empty, "$ 150.000,00 por semana", "Nos debe $ 50.000,00", "No lleva", "Activo"],
            rows[0].Cells);
        Assert.Equal(["2", "Pérez, Ana", "Carnicero", "$ 650.000,00 por mes", "Le debemos $ 120.000,00", "$ 15.500,00", "Activo"],
            rows[1].Cells);
    }

    [Fact]
    public void EstadoFilter_ShowsTheOnesDadosDeBaja_OnRequest()
    {
        var (list, _, _, carla) = List();
        var filter = Assert.Single(list.Model.Filters);
        Assert.Equal(["Activos", "Dados de baja", "Todos"], filter.Options);

        list.Model.SelectFilterOption("state", 1);
        Assert.Equal([carla], list.Model.Visible);
        Assert.Equal("Baja", list.Model.Rows[0].Cells[^1]);
    }

    [Theory]
    [InlineData("perez")]
    [InlineData("10")]
    [InlineData("30111222")]
    [InlineData("27301112223")]
    public void Search_CoversNameFileNumberDniAndCuil(string text)
    {
        var (list, _, _, _) = List();
        list.Model.SearchText = text;
        Assert.NotEmpty(list.Model.Visible);
    }

    [Fact]
    public async Task RowActions_OpenTheFileTheAdvanceAndTheAccount_AndOfferNoAdvanceToADadoDeBaja()
    {
        var (list, ana, _, carla) = List();
        var opened = new List<EmployeeEditorPurpose>();
        list.EditorChanged += (purpose, _) => opened.Add(purpose);

        await list.Model.InvokeAsync(ana, EntityRowAction<EmployeeRecordDto>.EditKey);
        await list.Model.InvokeAsync(ana, EmployeeList.AdvanceAction);
        await list.Model.InvokeAsync(ana, EmployeeList.AccountAction);

        Assert.Equal(EmployeeEditorPurpose.Account, list.Purpose);
        Assert.Contains(EmployeeEditorPurpose.Edit, opened);
        Assert.Contains(EmployeeEditorPurpose.Advance, opened);
        Assert.False(EmployeeList.CanGiveAdvance(carla));
        Assert.Equal("Cuenta corriente: Pérez, Ana", list.Model.EditorTitle);
    }

    [Fact]
    public async Task DarDeBaja_AsksFirst_ThenRunsTheToggle()
    {
        EmployeeRecordDto? toggled = null;
        var ana = Employee("Pérez", "Ana");
        var list = new EmployeeList(employee => { toggled = employee; return Task.CompletedTask; });
        list.Model.SetItems([ana]);

        await list.Model.InvokeAsync(ana, EmployeeList.ToggleActiveAction);
        Assert.Null(toggled);
        Assert.Equal("¿Dar de baja a Pérez, Ana?", list.Model.PendingConfirmation?.Question);

        await list.Model.ConfirmAsync();
        Assert.Same(ana, toggled);
    }

    // ---- the form rules, the web's ---------------------------------------------------------

    [Theory]
    [InlineData("650000", 650_000)]
    [InlineData("650000,5", 650_000.5)]
    [InlineData("650000.55", 650_000.55)]
    [InlineData("$ 1200", 1_200)]
    public void ParseAmount_AcceptsWhatTheWebAccepts(string text, double expected) =>
        Assert.Equal((decimal)expected, EmployeeFormRules.ParseAmount(text));

    [Theory]
    [InlineData("0")]
    [InlineData("650.000,00")]
    [InlineData("1,234")]
    [InlineData("-5")]
    [InlineData("abc")]
    [InlineData("")]
    public void ParseAmount_RefusesWhatTheWebRefuses(string text) => Assert.Null(EmployeeFormRules.ParseAmount(text));

    [Fact]
    public void Salary_EmptyIsZero_FileNumberEmptyIsTheNextOne_DatesAreDayMonthYear()
    {
        Assert.Equal(0m, EmployeeFormRules.ParseSalary("  "));
        Assert.Null(EmployeeFormRules.ParseSalary("0"));
        Assert.Equal("650000,5", EmployeeFormRules.SalaryText(650_000.50m));

        Assert.True(EmployeeFormRules.TryParseFileNumber("", out var next));
        Assert.Null(next);
        Assert.True(EmployeeFormRules.TryParseFileNumber("12", out var twelve));
        Assert.Equal(12, twelve);
        Assert.False(EmployeeFormRules.TryParseFileNumber("0", out _));
        Assert.False(EmployeeFormRules.TryParseFileNumber("1.5", out _));

        Assert.True(EmployeeFormRules.TryParseDate("15/03/2024", out var hire));
        Assert.Equal(new DateOnly(2024, 3, 15), hire);
        Assert.Equal("15/03/2024", EmployeeFormRules.DateText(hire));
        Assert.False(EmployeeFormRules.TryParseDate("31/02/2024", out _));
    }

    [Fact]
    public void AdvanceAccounts_AreThisBranchsOrCompanyWide_Active_TheDrawerFirst()
    {
        var other = Guid.NewGuid();
        TreasuryAccountDto Account(string name, Guid? branch, string kind = "Safe", bool active = true) =>
            new(Guid.NewGuid(), branch, null, kind, name, active);

        var accounts = EmployeeFormRules.AdvanceAccounts(
        [
            Account("Banco Nación", null, "Bank"),
            Account("Caja fuerte", Branch),
            Account("Caja efectivo", Branch, "Cash"),
            Account("Caja de otra", other, "Cash"),
            Account("Vieja", Branch, active: false),
        ], Branch);

        Assert.Equal(["Caja efectivo", "Caja fuerte", "Banco Nación"], accounts.Select(a => a.Name));
        Assert.Equal("Banco Nación (Toda la empresa)", EmployeeFormRules.AccountLabel(accounts[2]));
    }

    [Fact]
    public void RoleOptions_AreTheActiveOnes_PlusTheEmployeesOwn_AfterSinPuesto()
    {
        var (active, inactive, own) = (new EmployeeRoleDto(Guid.NewGuid(), "Cajero"), new EmployeeRoleDto(Guid.NewGuid(), "Viejo", false),
            new EmployeeRoleDto(Guid.NewGuid(), "Repartidor", false));

        var options = EmployeeFormRules.RoleOptions([active, inactive, own], own.Id);

        Assert.Equal(["Sin puesto", "Cajero", "Repartidor"], options.Select(o => o.Name));
        Assert.Equal(Guid.Empty, options[0].Id);
    }

    [Fact]
    public void AccountRows_AreNewestFirst_SignedAsTheyMoveWhatWeOwe()
    {
        var statement = new EmployeeStatementDto(0m,
        [
            new(Guid.NewGuid(), "Invoice", "Credit", 650_000m, new DateOnly(2026, 9, 30), null, "Sueldo de septiembre", 650_000m, false),
            new(Guid.NewGuid(), "Payment", "Debit", 50_000m, new DateOnly(2026, 10, 7), null, "Adelanto", 600_000m, false),
        ], 600_000m);

        var rows = EmployeeAccountRow.From(statement);

        Assert.Equal(["Adelanto", "Sueldo de septiembre"], rows.Select(r => r.Concept));
        Assert.Equal("07/10/2026 · Pago", rows[0].Detail);
        Assert.Equal("−$ 50.000,00", rows[0].Amount);
        Assert.Equal("+$ 650.000,00", rows[1].Amount);
        Assert.Equal("Saldo: Le debemos $ 600.000,00", rows[0].Balance);
    }

    // ---- the section ------------------------------------------------------------------------

    [Fact]
    public void Personal_HasTwoTabs_EmployeesFirst_AndTheShellBuildsIt()
    {
        var personal = Src("PersonalView.xaml");
        var main = Src("MainWindow.xaml.cs");

        Assert.Contains("Content=\"Empleados\"", personal);
        Assert.Contains("Content=\"Usuarios y acceso\"", personal);
        Assert.True(personal.IndexOf("Empleados", StringComparison.Ordinal) < personal.IndexOf("Usuarios y acceso", StringComparison.Ordinal));
        Assert.Contains("new PersonalView(", main);
        Assert.Contains("new EmployeesView(_management.Employees", main);
        Assert.Contains("Usuarios y acceso", Src("StaffView.xaml"));
    }

    [Fact]
    public void EmployeesView_UsesThemeBrushesOnly_AndKeepsErrorsAboveTheForm()
    {
        var xaml = Src("EmployeesView.xaml");
        var code = Src("EmployeesView.xaml.cs");

        Assert.Empty(Regex.Matches(xaml, @"\{StaticResource\s+\w*Brush\w*\}"));
        Assert.Contains("x:Name=\"StatusText\"", xaml);
        Assert.Contains("<controls:EntityListView x:Name=\"EmployeesListView\"", xaml);
        Assert.Contains("FormPanel.IsEnabled", code);
        Assert.DoesNotContain("MessageBox", code);
        Assert.DoesNotContain("/account/movements", Src("EmployeeAdminClient.cs"));
    }
}
