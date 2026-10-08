using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.RegularExpressions;
using Commerce.Domain.Identity;
using Commerce.Pos.Windows;

namespace Commerce.Integration;

/// <summary>
/// Personal, the admin staff section of the shell: the status PUT of the client,
/// the Spanish error mapping, the roles an admin may create, the staff rows and
/// the "Operadores de esta terminal" rows, plus the structure of the view
/// (scrolls, inline status, no operator provisioning).
/// </summary>
[Collection("PosLog")]
public sealed class PosStaffViewTests
{
    private sealed class RecordingHandler(HttpStatusCode status, string? body = null) : HttpMessageHandler
    {
        public HttpMethod? Method { get; private set; }
        public string? Path { get; private set; }
        public string? Body { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Method = request.Method;
            Path = request.RequestUri!.AbsolutePath;
            Body = request.Content is null ? null : await request.Content.ReadAsStringAsync(cancellationToken);
            var response = new HttpResponseMessage(status);
            if (body is not null)
            {
                response.Content = new StringContent(body, Encoding.UTF8, "application/json");
            }

            return response;
        }
    }

    private static UserAdminClient ClientFor(RecordingHandler handler) =>
        new(new HttpClient(handler) { BaseAddress = new Uri("https://cloud.invalid") });

    private static string Src(params string[] path)
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "Commerce.sln"))) dir = dir.Parent;
        return File.ReadAllText(Path.Combine([dir!.FullName, "src", "Commerce.Pos.Windows", .. path]));
    }

    // ---- client: PUT /account/users/{id}/status -----------------------------

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task SetStatus_PutsTheRevokedFlagToTheStatusEndpoint(bool revoked)
    {
        var handler = new RecordingHandler(HttpStatusCode.NoContent);
        var id = Guid.NewGuid();

        var outcome = await ClientFor(handler).SetStatusAsync(id, revoked);

        Assert.Equal(UserAdminMutationKind.Succeeded, outcome.Kind);
        Assert.Equal(HttpMethod.Put, handler.Method);
        Assert.Equal($"/account/users/{id}/status", handler.Path);
        Assert.Equal(revoked ? """{"revoked":true}""" : """{"revoked":false}""", handler.Body);
    }

    [Theory]
    [InlineData(HttpStatusCode.BadRequest, "cannot-revoke-self", PosMessages.CannotDeactivateSelf)]
    [InlineData(HttpStatusCode.BadRequest, "not-a-staff-user", PosMessages.NotAStaffUser)]
    [InlineData(HttpStatusCode.Forbidden, "permissions-exceed-caller", PosMessages.PermissionsExceedCaller)]
    [InlineData(HttpStatusCode.Forbidden, "branch-not-in-scope", PosMessages.StaffBranchNotInScope)]
    [InlineData(HttpStatusCode.Forbidden, null, PosMessages.NoPermissionToManageStaff)]
    [InlineData(HttpStatusCode.NotFound, null, PosMessages.StaffUserNotFound)]
    [InlineData(HttpStatusCode.Forbidden, "operator-not-authorized", PosMessages.OperatorNotAuthorized)]
    [InlineData(HttpStatusCode.Unauthorized, null, PosMessages.TerminalNotRecognized)]
    [InlineData(HttpStatusCode.BadRequest, "something-new", PosMessages.InvalidData)]
    public async Task SetStatus_MapsTheServerAnswerToSpanish(HttpStatusCode status, string? code, string expected)
    {
        var handler = new RecordingHandler(status, code is null ? null : $$"""{"error":"{{code}}"}""");

        var outcome = await ClientFor(handler).SetStatusAsync(Guid.NewGuid(), true);

        Assert.NotEqual(UserAdminMutationKind.Succeeded, outcome.Kind);
        Assert.Equal(expected, outcome.ErrorMessage);
    }

    [Fact]
    public async Task CreateUser_MapsTheTypedForbiddenCodesToo()
    {
        var handler = new RecordingHandler(HttpStatusCode.Forbidden, """{"error":"permissions-exceed-caller"}""");

        var outcome = await ClientFor(handler).CreateUserAsync(new CreateUserAdminRequestDto("a@b.c", "pw", ["business-admin"], [Guid.NewGuid()]));

        Assert.Equal(PosMessages.PermissionsExceedCaller, outcome.ErrorMessage);
    }

    [Fact]
    public async Task ListUsers_ReadsTheBranchIds()
    {
        var branch = Guid.NewGuid();
        var handler = new RecordingHandler(
            HttpStatusCode.OK,
            $$"""[{"userId":"{{Guid.NewGuid()}}","email":"a@b.c","roleNames":["cashier"],"isRevoked":false,"branchIds":["{{branch}}"]}]""");

        var users = await ClientFor(handler).ListUsersAsync();

        Assert.Equal([branch], users![0].BranchIds);
    }

    // ---- roles an admin creates ---------------------------------------------

    [Fact]
    public void RoleOptions_AreCashierSellerAdministrator_CashierFirstAndDefault_NeverPlatformOrProvider()
    {
        var options = StaffRoleOptions.All;

        Assert.Equal([RoleCatalog.Cashier, RoleCatalog.Seller, RoleCatalog.BusinessAdmin], options.Select(o => o.Name));
        Assert.Equal(["Cajero", "Vendedor", "Administrador"], options.Select(o => o.Label));
        Assert.Equal(RoleCatalog.Cashier, StaffRoleOptions.Default.Name);
        Assert.All(options, o => Assert.Contains(o.Name, RoleCatalog.OrgAssignable));
    }

    [Theory]
    [InlineData("business-admin", "Administrador")]
    [InlineData("seller", "Vendedor")]
    [InlineData("cashier", "Cajero")]
    [InlineData("provider", "Proveedor")]
    [InlineData("something-else", "something-else")]
    public void RoleLabel_IsSpanish(string role, string label)
    {
        Assert.Equal(label, StaffRoleOptions.LabelFor(role));
    }

    // ---- staff rows (the list itself: PosStaffListTests) -------------------------

    private static readonly Guid TerminalBranch = Guid.NewGuid();
    private static readonly Guid Me = Guid.NewGuid();

    private static UserAdminRecordDto User(string email, string[] roles, bool revoked = false, Guid? id = null) =>
        new(id ?? Guid.NewGuid(), email, roles, revoked, null);

    [Fact]
    public async Task Rows_OfferPasswordReset_OnEveryRow_EvenTheOwnOne_AndWhileConfirmingAStatusChange()
    {
        // The API has no self restriction on reset-password, and it does not depend on the status action.
        var target = User("a@x.test", ["cashier"]);
        var list = new StaffList(Me, TerminalBranch, _ => Task.CompletedTask);
        list.Model.SetItems([User("me@x.test", ["business-admin"], id: Me), target, User("b@x.test", [], revoked: true)]);
        list.Model.SelectFilterOption("state", 2);

        await list.Model.InvokeAsync(target, StaffList.ToggleStatusAction);

        Assert.NotNull(list.Model.PendingConfirmation);
        var rows = ((IEntityListModel)list.Model).Rows;
        Assert.Equal(3, rows.Count);
        Assert.All(rows, row => Assert.True(row.Actions.Single(a => a.Key == StaffList.ResetPasswordAction).IsEnabled));
        Assert.False(rows.Single(r => ((UserAdminRecordDto)r.Item).UserId == Me).Actions.Single(a => a.Key == StaffList.ToggleStatusAction).IsEnabled);
    }

    // ---- operators of this terminal --------------------------------------------

    private static CachedOperator Cached(string email, int permissions, DateTimeOffset verified) =>
        new(Guid.NewGuid(), email, Guid.NewGuid(), [1], [2], verified, permissions);

    [Fact]
    public void TerminalOperatorRows_ShowRole_AndMarkAnExpiredOneAsNeedingEmailAndPassword()
    {
        var now = DateTimeOffset.UtcNow;
        var rows = TerminalOperatorRowPresenter.Build(
            [
                Cached("z@x.test", (int)Permission.OperatePos, now),
                Cached("a@x.test", (int)(Permission.OperatePos | Permission.ManageUsers), now - CachedOperator.Ttl - TimeSpan.FromDays(1)),
            ],
            now);

        Assert.Equal(["a@x.test", "z@x.test"], rows.Select(r => r.Email));
        Assert.Contains("Administrador", rows[0].Detail);
        Assert.Contains("Vencido", rows[0].Detail);
        Assert.Contains("correo y contraseña", rows[0].Detail);
        Assert.Equal("Cajero", rows[1].Detail);
    }

    // ---- view structure ----------------------------------------------------------

    [Fact]
    public void StaffView_ScrollsShowsStatusInlineAndIsBusyGuarded()
    {
        var xaml = Src("StaffView.xaml");
        var code = Src("StaffView.xaml.cs");

        Assert.Contains("<UserControl", xaml);
        Assert.Contains("<ScrollViewer", xaml);
        foreach (var name in new[] { "StatusText", "BusyPanel", "BusyProgressBar", "BusyText", "FormPanel" })
        {
            Assert.Contains($"x:Name=\"{name}\"", xaml);
        }

        Assert.Contains("_busy.RunAsync", code);
        Assert.Contains("FormPanel.IsEnabled", code);
        Assert.DoesNotContain("MessageBox", code);
        Assert.Empty(Regex.Matches(xaml, @"\{StaticResource\s+\w*Brush\w*\}"));
    }

    [Fact]
    public void StaffView_CreatesStaffWithRole_ListsThem_AndRemovesTerminalOperators()
    {
        var xaml = Src("StaffView.xaml");
        var code = Src("StaffView.xaml.cs");

        Assert.Contains("x:Name=\"NewEmailTextBox\"", xaml);
        Assert.Contains("x:Name=\"NewPasswordBox\"", xaml);
        Assert.Contains("x:Name=\"RoleComboBox\"", xaml);
        Assert.Contains("<controls:EntityListView x:Name=\"StaffListView\"", xaml);
        Assert.Contains("x:Name=\"OperatorsItemsControl\"", xaml);
        Assert.Contains("Operadores de esta terminal", xaml);
        Assert.Contains("Quitar de esta terminal", xaml);
        Assert.DoesNotContain("Confirmá tu contraseña", Src("PosMessages.cs"));
        Assert.Contains("SetStatusAsync", code);
        Assert.Contains("_operatorStore.Remove", code);
        Assert.Contains("OperatorsChanged", code);
    }

    [Fact]
    public void StaffView_NeverOffersToAddOrProvisionAnOperator()
    {
        var xaml = Src("StaffView.xaml");
        var code = Src("StaffView.xaml.cs");

        Assert.DoesNotContain("Agregar operador", xaml);
        Assert.DoesNotContain("Provision", xaml);
        Assert.DoesNotContain("ProvisionOperatorWindow", code);
        Assert.DoesNotContain("OperatorProvisioningClient", code);
    }

    [Fact]
    public void MainWindow_HostsStaffAsASection_AndNoStaffWindowRemains()
    {
        var code = Src("MainWindow.xaml.cs");

        Assert.Contains("new StaffView(", code);
        Assert.DoesNotContain("new UsersWindow", code);
        Assert.DoesNotContain("TerminalOperatorsWindow", code);
        Assert.DoesNotContain("ProvisionOperatorWindow", code);
        Assert.Throws<FileNotFoundException>(() => Src("UsersWindow.xaml"));
        Assert.Throws<FileNotFoundException>(() => Src("TerminalOperatorsWindow.xaml"));
    }
}
