using System.Text.RegularExpressions;
using Commerce.Domain.Identity;
using Commerce.Pos.Windows;

namespace Commerce.Integration;

/// <summary>
/// Personal on the reusable entity list (price-editing-and-desktop-polish T1): the columns, the search over the email,
/// the Rol / Estado filters, the row actions (Editar rol, Dar de baja / Reactivar with confirmation, Restablecer contraseña),
/// which form the editor shows (create, role, password), the requests the forms send, and the structure of the view
/// (list at full width, the form hidden until "Nuevo" or a row asks for it).
/// </summary>
public sealed class PosStaffListTests
{
    private static readonly Guid TerminalBranch = Guid.NewGuid();
    private static readonly Guid Me = Guid.NewGuid();

    private static UserAdminRecordDto User(string email, string[] roles, bool revoked = false, Guid? id = null, Guid[]? branches = null) =>
        new(id ?? Guid.NewGuid(), email, roles, revoked, branches);

    private static readonly UserAdminRecordDto Admin = User("me@x.test", [RoleCatalog.BusinessAdmin], id: Me, branches: [TerminalBranch]);
    private static readonly UserAdminRecordDto Bruno = User("bruno@x.test", [RoleCatalog.Seller, RoleCatalog.Cashier], branches: [TerminalBranch]);
    private static readonly UserAdminRecordDto Carla = User("carla@x.test", [], revoked: true, branches: [Guid.NewGuid()]);
    private static readonly UserAdminRecordDto Dario = User("dario@x.test", [RoleCatalog.Cashier], branches: [TerminalBranch, Guid.NewGuid()]);

    private static StaffList List(Func<UserAdminRecordDto, Task>? toggle = null)
    {
        var list = new StaffList(Me, TerminalBranch, toggle ?? (_ => Task.CompletedTask));
        list.Model.SetItems([Dario, Carla, Bruno, Admin]);
        return list;
    }

    private static void ShowAll(StaffList list) => list.Model.SelectFilterOption("state", 2);

    // ---- columns, search, filters ---------------------------------------------------------

    [Fact]
    public void Columns_AreEmailRolSucursalEstado_AllSortable_StartingByEmail()
    {
        var list = List();
        ShowAll(list);
        var columns = ((IEntityListModel)list.Model).Columns;

        Assert.Equal(["Email", "Rol", "Sucursal", "Estado"], columns.Select(c => c.Header));
        Assert.All(columns, c => Assert.True(c.Sortable, c.Header));
        Assert.Equal("▲", columns[0].SortIndicator);
        Assert.Equal(["bruno@x.test", "carla@x.test", "dario@x.test", "me@x.test"], list.Model.Visible.Select(u => u.Email));
    }

    [Fact]
    public void Cells_ShowSpanishRolesBranchRelativeToThisTerminalAndStatus()
    {
        var list = List();
        ShowAll(list);
        var rows = ((IEntityListModel)list.Model).Rows;

        Assert.Equal(["bruno@x.test", "Vendedor, Cajero", "Esta sucursal", "Activo"], rows[0].Cells);
        Assert.Equal(["carla@x.test", "Sin rol", "Otras sucursales", "Dado de baja"], rows[1].Cells);
        Assert.Equal(["dario@x.test", "Cajero", "Esta sucursal y otras", "Activo"], rows[2].Cells);
        Assert.Equal(string.Empty, StaffList.BranchText(null, TerminalBranch));
    }

    [Fact]
    public void Search_CoversTheEmail()
    {
        var list = List();

        list.Model.SearchText = "DARIO";

        Assert.Equal(["dario@x.test"], list.Model.Visible.Select(u => u.Email));
    }

    [Fact]
    public void Filters_AreRolAndEstado_EstadoStartsOnActivos()
    {
        var list = List();
        var filters = ((IEntityListModel)list.Model).Filters;

        Assert.Equal(["Rol", "Estado"], filters.Select(f => f.Label));
        Assert.Equal(["Todos", "Cajero", "Vendedor", "Administrador"], filters[0].Options);
        Assert.Equal(["Activos", "Dados de baja", "Todos"], filters[1].Options);
        Assert.Equal(["bruno@x.test", "dario@x.test", "me@x.test"], list.Model.Visible.Select(u => u.Email));

        list.Model.SelectFilterOption("state", 1);
        Assert.Equal(["carla@x.test"], list.Model.Visible.Select(u => u.Email));

        ShowAll(list);
        list.Model.SelectFilterOption("role", 1);
        Assert.Equal(["bruno@x.test", "dario@x.test"], list.Model.Visible.Select(u => u.Email));
    }

    // ---- row actions ----------------------------------------------------------------------

    [Fact]
    public void RowActions_AreEditarRol_DarDeBajaOrReactivar_AndRestablecerContrasena()
    {
        var list = List();
        ShowAll(list);
        var rows = ((IEntityListModel)list.Model).Rows;

        Assert.Equal(["Editar rol", "Dar de baja", "Restablecer contraseña"], rows[0].Actions.Select(a => a.Label));
        Assert.Equal(["Editar rol", "Reactivar", "Restablecer contraseña"], rows[1].Actions.Select(a => a.Label));
    }

    [Fact]
    public void TheSignedInAdminsOwnRow_NeverOffersRevokeNorRoleChange_ButOffersPasswordReset()
    {
        var list = List();
        var own = ((IEntityListModel)list.Model).Rows.Single(r => ReferenceEquals(r.Item, Admin));
        var other = ((IEntityListModel)list.Model).Rows.Single(r => ReferenceEquals(r.Item, Bruno));

        Assert.False(own.Actions.Single(a => a.Key == StaffList.ToggleStatusAction).IsEnabled);
        Assert.False(own.Actions.Single(a => a.Key == StaffList.EditRoleAction).IsEnabled);
        Assert.True(own.Actions.Single(a => a.Key == StaffList.ResetPasswordAction).IsEnabled);
        Assert.All(other.Actions, a => Assert.True(a.IsEnabled, a.Label));
        Assert.False(list.CanEditRole(Admin));
        Assert.True(list.CanEditRole(Bruno));
    }

    [Fact]
    public async Task DarDeBajaAndReactivar_AskFirst_ThenRunOnce()
    {
        var toggled = new List<UserAdminRecordDto>();
        var list = List(user => { toggled.Add(user); return Task.CompletedTask; });

        await list.Model.InvokeAsync(Bruno, StaffList.ToggleStatusAction);
        Assert.Empty(toggled);
        Assert.Equal("¿Dar de baja a bruno@x.test?", list.Model.PendingConfirmation!.Question);
        Assert.Equal("Dar de baja", list.Model.PendingConfirmation.ConfirmLabel);

        await list.Model.ConfirmAsync();
        Assert.Equal([Bruno], toggled);

        await list.Model.InvokeAsync(Carla, StaffList.ToggleStatusAction);
        Assert.Equal("¿Reactivar a carla@x.test?", list.Model.PendingConfirmation!.Question);
        list.Model.CancelConfirmation();
        Assert.Equal([Bruno], toggled);
    }

    [Fact]
    public async Task DarDeBaja_OnTheOwnRow_DoesNothing()
    {
        var toggled = 0;
        var list = List(_ => { toggled++; return Task.CompletedTask; });

        await list.Model.InvokeAsync(Admin, StaffList.ToggleStatusAction);

        Assert.Null(list.Model.PendingConfirmation);
        Assert.Equal(0, toggled);
    }

    // ---- the editor: hidden, Nuevo, Editar rol, Restablecer contraseña---------------------------

    [Fact]
    public void TheEditor_StartsClosed_AndNuevoOpensTheCreateForm()
    {
        var list = List();
        var seen = new List<StaffEditorPurpose>();
        list.EditorChanged += (purpose, _) => seen.Add(purpose);

        Assert.Equal(StaffEditorPurpose.None, list.Purpose);
        Assert.False(EntityEditorPanel.IsShown(hidesClosedEditor: true, list.Model.EditorMode));

        list.Model.BeginNew();

        Assert.Equal(StaffEditorPurpose.Create, list.Purpose);
        Assert.Equal([StaffEditorPurpose.Create], seen);
        Assert.Equal("Nuevo usuario", list.Model.EditorTitle);
        Assert.True(EntityEditorPanel.IsShown(hidesClosedEditor: true, list.Model.EditorMode));

        list.Model.CloseEditor();
        Assert.Equal(StaffEditorPurpose.None, list.Purpose);
        Assert.False(EntityEditorPanel.IsShown(hidesClosedEditor: true, list.Model.EditorMode));
    }

    [Fact]
    public void AnEditorThatIsNotHidden_StaysShownWhileClosed()
    {
        Assert.True(EntityEditorPanel.IsShown(hidesClosedEditor: false, EntityEditorMode.None));
        Assert.True(EntityEditorPanel.IsShown(hidesClosedEditor: false, EntityEditorMode.Edit));
    }

    [Fact]
    public async Task EditarRol_AndSelectingARow_OpenTheRoleForm()
    {
        var list = List();

        await list.Model.InvokeAsync(Bruno, StaffList.EditRoleAction);
        Assert.Equal(StaffEditorPurpose.EditRole, list.Purpose);
        Assert.Same(Bruno, list.Model.Editing);
        Assert.Equal("Rol de bruno@x.test", list.Model.EditorTitle);

        ((IEntityListModel)list.Model).Select(Dario);
        Assert.Equal(StaffEditorPurpose.EditRole, list.Purpose);
        Assert.Same(Dario, list.Model.Editing);
    }

    [Fact]
    public async Task RestablecerContrasena_OpensThePasswordForm_EvenOnTheRowOpenForItsRole_AndBack()
    {
        var list = List();
        var seen = new List<(StaffEditorPurpose Purpose, UserAdminRecordDto? User)>();
        list.EditorChanged += (purpose, user) => seen.Add((purpose, user));

        await list.Model.InvokeAsync(Bruno, StaffList.EditRoleAction);
        await list.Model.InvokeAsync(Bruno, StaffList.ResetPasswordAction);

        Assert.Equal(StaffEditorPurpose.ResetPassword, list.Purpose);
        Assert.Same(Bruno, list.Model.Editing);
        Assert.Equal("Nueva contraseña para bruno@x.test", list.Model.EditorTitle);
        Assert.Equal((StaffEditorPurpose.ResetPassword, Bruno), seen[^1]);

        // Pressing it again keeps the open form as it is (nothing typed is lost).
        var count = seen.Count;
        await list.Model.InvokeAsync(Bruno, StaffList.ResetPasswordAction);
        Assert.Equal(count, seen.Count);

        await list.Model.InvokeAsync(Bruno, StaffList.EditRoleAction);
        Assert.Equal(StaffEditorPurpose.EditRole, list.Purpose);
        Assert.Equal((StaffEditorPurpose.EditRole, Bruno), seen[^1]);
    }

    [Fact]
    public async Task RestablecerContrasena_IsOfferedOnTheOwnRowToo()
    {
        var list = List();

        await list.Model.InvokeAsync(Admin, StaffList.ResetPasswordAction);

        Assert.Equal(StaffEditorPurpose.ResetPassword, list.Purpose);
        Assert.Same(Admin, list.Model.Editing);
    }

    // ---- requests ---------------------------------------------------------------------------

    [Fact]
    public void CreateRequest_PutsTheNewStaffInThisTerminalsBranch_WithTheChosenRole()
    {
        var list = List();

        var request = list.CreateRequest("  nuevo@x.test ", "secreta", RoleCatalog.Seller);

        Assert.NotNull(request);
        Assert.Equal("nuevo@x.test", request!.Email);
        Assert.Equal("secreta", request.Password);
        Assert.Equal([RoleCatalog.Seller], request.RoleNames);
        Assert.Equal([TerminalBranch], request.BranchIds);
    }

    [Theory]
    [InlineData("", "secreta")]
    [InlineData("   ", "secreta")]
    [InlineData("nuevo@x.test", "")]
    public void CreateRequest_NeedsEmailAndPassword(string email, string password)
    {
        Assert.Null(List().CreateRequest(email, password, RoleCatalog.Cashier));
    }

    [Fact]
    public void CreateRequest_FallsBackToTheDefaultRole_AndNeverOffersARoleOutsideTheOptions()
    {
        var list = List();

        Assert.Equal([StaffRoleOptions.Default.Name], list.CreateRequest("a@x.test", "pw", null)!.RoleNames);
        Assert.Equal([StaffRoleOptions.Default.Name], list.CreateRequest("a@x.test", "pw", RoleCatalog.PlatformAdmin)!.RoleNames);
    }

    [Fact]
    public void RoleForm_StartsOnTheUsersOfferedRole_OrTheDefault()
    {
        Assert.Equal(RoleCatalog.Seller, StaffList.InitialRole(Bruno));
        Assert.Equal(RoleCatalog.BusinessAdmin, StaffList.InitialRole(Admin));
        Assert.Equal(StaffRoleOptions.Default.Name, StaffList.InitialRole(Carla));
    }

    [Fact]
    public void RolesAfterEdit_ReplacesTheOfferedRoles_AndKeepsTheOnesThePosDoesNotOffer()
    {
        var mixed = User("m@x.test", [RoleCatalog.Seller, RoleCatalog.Cashier, RoleCatalog.Provider]);

        Assert.Equal([RoleCatalog.BusinessAdmin], StaffList.RolesAfterEdit(Bruno, RoleCatalog.BusinessAdmin));
        Assert.Equal([RoleCatalog.Provider, RoleCatalog.Cashier], StaffList.RolesAfterEdit(mixed, RoleCatalog.Cashier));
        Assert.Equal([StaffRoleOptions.Default.Name], StaffList.RolesAfterEdit(Carla, RoleCatalog.PlatformAdmin));
    }

    // ---- view structure ----------------------------------------------------------------------

    private static string Src(params string[] path)
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "Commerce.sln"))) dir = dir.Parent;
        return File.ReadAllText(Path.Combine([dir!.FullName, "src", "Commerce.Pos.Windows", .. path]));
    }

    [Fact]
    public void StaffView_IsBuiltOnTheEntityList_WithTheEditorHiddenUntilAsked()
    {
        var xaml = Src("StaffView.xaml");
        var code = Src("StaffView.xaml.cs");

        Assert.Contains("<controls:EntityListView", xaml);
        Assert.Contains("<controls:EntityListView.EditorContent>", xaml);
        Assert.Matches(@"<controls:EntityListView[^>]*HidesClosedEditor=""True""", xaml);
        Assert.DoesNotContain("StaffItemsControl", xaml);
        Assert.Contains("new StaffList(", code);
        Assert.Contains("EditorChanged", code);
        Assert.Contains(".CreateRequest(", code);
        Assert.Contains("ReplaceRolesAsync", code);
        Assert.Contains("StaffList.RolesAfterEdit(", code);
        Assert.Contains("ResetPasswordAsync", code);
        Assert.Contains("SetStatusAsync", code);
    }

    [Fact]
    public void TheStaffScreen_FollowsTheExistingWording_DarDeBajaReactivarRestablecer()
    {
        // T5 (a): the messages already say "Usuario dado de baja." / "Usuario reactivado." and the form button says
        // "Restablecer contraseña"; the list, its filter and its actions must not say anything else.
        foreach (var source in new[] { Src("StaffList.cs"), Src("StaffView.xaml"), Src("StaffView.xaml.cs") })
        {
            Assert.DoesNotMatch(@"Revoca|Restaura|Resetea", source);
        }

        Assert.Equal("Usuario dado de baja.", PosMessages.StaffDeactivated);
        Assert.Equal("Usuario reactivado.", PosMessages.StaffReactivated);
    }

    [Fact]
    public void StaffView_ClosesTheFormAfterASuccessfulSave_ReturningToTheList()
    {
        var code = Src("StaffView.xaml.cs");

        var create = Regex.Match(code, @"private async void CreateButton_Click\([\s\S]*?\n    }");
        Assert.True(create.Success);
        Assert.Contains(".CloseEditor()", create.Value);
        Assert.Contains("PosMessages.StaffCreated", create.Value);
        Assert.Contains("PosMessages.EmailAndPasswordRequired", create.Value);
    }

    [Fact]
    public void StaffView_ShowsOneFormAtATime_InTheEditor()
    {
        var xaml = Src("StaffView.xaml");
        var editor = xaml[xaml.IndexOf("<controls:EntityListView.EditorContent>", StringComparison.Ordinal)..xaml.IndexOf("</controls:EntityListView.EditorContent>", StringComparison.Ordinal)];

        foreach (var name in new[] { "CreatePanel", "NewEmailTextBox", "NewPasswordBox", "RoleComboBox", "CreateButton",
                     "EditRolePanel", "EditRoleComboBox", "SaveRoleButton", "ResetPanel", "ResetPasswordBox", "ResetSaveButton" })
        {
            Assert.Contains($"x:Name=\"{name}\"", editor);
        }
    }

    [Fact]
    public void StaffView_FieldsUseTheThemesStandardHeight()
    {
        var xaml = Src("StaffView.xaml");

        // The theme's TextBox / PasswordBox / ComboBox styles already give the standard 38 px field; no oversized boxes.
        foreach (Match field in Regex.Matches(xaml, @"<(TextBox|PasswordBox|ComboBox)\b[^>]*>"))
        {
            Assert.DoesNotMatch(@"\b(Min)?Height=""", field.Value);
            Assert.DoesNotMatch(@"\bPadding=""", field.Value);
            Assert.DoesNotMatch(@"\bFontSize=""", field.Value);
        }
    }

    [Fact]
    public void EntityListView_CanHideTheClosedEditor_SoTheListUsesTheFullWidth()
    {
        var code = Src("Controls", "EntityListView.xaml.cs");
        var xaml = Src("Controls", "EntityListView.xaml");

        Assert.Contains("HidesClosedEditorProperty", code);
        Assert.Contains("EntityEditorPanel.IsShown(", code);
        Assert.Contains("x:Name=\"PART_EditorPanel\"", xaml);
    }
}
