namespace Commerce.Pos.Windows;

/// <summary>Which form the Personal editor shows: none (closed), the new user, a user's role, or a new password.</summary>
public enum StaffEditorPurpose
{
    None,
    Create,
    EditRole,
    ResetPassword,
}

/// <summary>
/// The Personal list on the reusable entity list (price-editing-and-desktop-polish T1): the Email / Rol / Sucursal /
/// Estado columns, the search over the email, the Rol and Estado filters, the Editar rol / Revocar-Restaurar /
/// Resetear contraseña row actions, and which form the editor shows. UI-free; the screen supplies the status change
/// (it needs the connection and its busy controller) and fills its forms on <see cref="EditorChanged"/>.
///
/// The rules the server enforces too are mirrored here so the list never offers them: the signed-in administrator
/// cannot revoke nor change the role of their own account (the password reset is offered on every row, the API allows
/// it), new staff is created in THIS terminal's branch, and only the roles of <see cref="StaffRoleOptions"/> are sent
/// (the server still applies the grant caps).
/// </summary>
public sealed class StaffList
{
    public const string EditRoleAction = "edit-role";
    public const string ToggleStatusAction = "toggle-status";
    public const string ResetPasswordAction = "reset-password";

    private readonly Guid _callerUserId;
    private readonly Guid _terminalBranchId;

    // The form the next opening of the editor on a row shows; a plain row selection opens the role form.
    private StaffEditorPurpose? _requested;

    /// <param name="callerUserId">The signed-in operator: their own row never offers revoke nor a role change.</param>
    /// <param name="terminalBranchId">This terminal's branch: new staff is created in it, the Sucursal column is relative to it.</param>
    /// <param name="toggleStatus">Revocar / Restaurar, after the inline confirmation.</param>
    public StaffList(Guid callerUserId, Guid terminalBranchId, Func<UserAdminRecordDto, Task> toggleStatus)
    {
        _callerUserId = callerUserId;
        _terminalBranchId = terminalBranchId;
        Model = new EntityListModel<UserAdminRecordDto>(Definition(toggleStatus));
        Model.EditorChanged += OnModelEditorChanged;
    }

    public EntityListModel<UserAdminRecordDto> Model { get; }

    public StaffEditorPurpose Purpose { get; private set; }

    /// <summary>The editor opened on a form (with the user it acts on, null while creating) or closed (None).</summary>
    public event Action<StaffEditorPurpose, UserAdminRecordDto?>? EditorChanged;

    public bool CanEditRole(UserAdminRecordDto user) => user.UserId != _callerUserId;

    public bool CanChangeStatus(UserAdminRecordDto user) => user.UserId != _callerUserId;

    /// <summary>Opens the role form on <paramref name="user"/> (Editar rol).</summary>
    public void BeginEditRole(UserAdminRecordDto user) => Open(user, StaffEditorPurpose.EditRole);

    /// <summary>Opens the new password form on <paramref name="user"/> (Resetear contraseña).</summary>
    public void BeginResetPassword(UserAdminRecordDto user) => Open(user, StaffEditorPurpose.ResetPassword);

    /// <summary>
    /// The create request of the "Nuevo" form: the trimmed email, the initial password, one offered role (the default
    /// when none or another was chosen) and THIS terminal's branch. Null when the email or the password is missing.
    /// </summary>
    public CreateUserAdminRequestDto? CreateRequest(string email, string password, string? role)
    {
        var trimmed = email.Trim();
        if (trimmed.Length == 0 || password.Length == 0)
        {
            return null;
        }

        return new CreateUserAdminRequestDto(trimmed, password, [OfferedOrDefault(role)], [_terminalBranchId]);
    }

    /// <summary>The role the role form starts on: the user's first offered role, or the default one.</summary>
    public static string InitialRole(UserAdminRecordDto user) =>
        user.RoleNames
            .Select(role => StaffRoleOptions.All.FirstOrDefault(option => string.Equals(option.Name, role, StringComparison.OrdinalIgnoreCase)))
            .FirstOrDefault(option => option is not null)?.Name ?? StaffRoleOptions.Default.Name;

    /// <summary>
    /// The roles <c>PUT /account/users/{id}/roles</c> receives: the offered roles are replaced by the chosen one, the
    /// roles the POS does not offer (and so cannot show) are kept as they are.
    /// </summary>
    public static string[] RolesAfterEdit(UserAdminRecordDto user, string? chosenRole) =>
        user.RoleNames
            .Where(role => !StaffRoleOptions.All.Any(option => string.Equals(option.Name, role, StringComparison.OrdinalIgnoreCase)))
            .Append(OfferedOrDefault(chosenRole))
            .ToArray();

    public static string RolesText(UserAdminRecordDto user) =>
        user.RoleNames.Count == 0 ? "Sin rol" : string.Join(", ", user.RoleNames.Select(StaffRoleOptions.LabelFor));

    /// <summary>Where the person works, relative to THIS terminal's branch.</summary>
    public static string BranchText(IReadOnlyList<Guid>? branchIds, Guid terminalBranchId)
    {
        if (branchIds is null || branchIds.Count == 0)
        {
            return string.Empty;
        }

        if (!branchIds.Contains(terminalBranchId))
        {
            return "Otras sucursales";
        }

        return branchIds.Count == 1 ? "Esta sucursal" : "Esta sucursal y otras";
    }

    public static string StatusText(UserAdminRecordDto user) => user.IsRevoked ? "Revocado" : "Activo";

    private static string ToggleLabel(UserAdminRecordDto user) => user.IsRevoked ? "Restaurar" : "Revocar";

    private static bool HasRole(UserAdminRecordDto user, string role) =>
        user.RoleNames.Any(name => string.Equals(name, role, StringComparison.OrdinalIgnoreCase));

    private static string OfferedOrDefault(string? role) =>
        StaffRoleOptions.All.FirstOrDefault(option => option.Name == role)?.Name ?? StaffRoleOptions.Default.Name;

    private EntityListDefinition<UserAdminRecordDto> Definition(Func<UserAdminRecordDto, Task> toggleStatus) => new(
        user => user.UserId,
        [
            new("email", "Email", user => user.Email),
            new("role", "Rol", RolesText) { Width = 170 },
            new("branch", "Sucursal", user => BranchText(user.BranchIds, _terminalBranchId)) { Width = 170 },
            new("state", "Estado", StatusText) { Width = 110 },
        ],
        user => [user.Email])
    {
        Filters =
        [
            new("role", "Rol",
            [
                new EntityFilterOption<UserAdminRecordDto>("Todos"),
                .. StaffRoleOptions.All.Select(option =>
                    new EntityFilterOption<UserAdminRecordDto>(option.Label, user => HasRole(user, option.Name))),
            ]),
            new("state", "Estado",
            [
                new("Activos", user => !user.IsRevoked),
                new("Revocados", user => user.IsRevoked),
                new("Todos"),
            ]),
        ],
        RowActions =
        [
            new(EditRoleAction, _ => "Editar rol", _ => EntityIcons.Edit)
            {
                IsEnabled = CanEditRole,
                Run = user => { BeginEditRole(user); return Task.CompletedTask; },
            },
            new(ToggleStatusAction, ToggleLabel, user => user.IsRevoked ? EntityIcons.Enable : EntityIcons.Disable)
            {
                IsEnabled = CanChangeStatus,
                Run = toggleStatus,
                Confirmation = user => $"¿{ToggleLabel(user)} a {user.Email}?",
            },
            new(ResetPasswordAction, _ => "Resetear contraseña", _ => EntityIcons.Password)
            {
                Run = user => { BeginResetPassword(user); return Task.CompletedTask; },
            },
        ],
        InitialSortKey = "email",
        SearchPlaceholder = "Buscar por email…",
        NewTitle = "Nuevo usuario",
        EditTitle = user => Purpose == StaffEditorPurpose.ResetPassword
            ? $"Nueva contraseña para {user.Email}"
            : $"Rol de {user.Email}",
    };

    /// <summary>
    /// Opens <paramref name="purpose"/> on <paramref name="user"/>. The same form already open on that user stays as it
    /// is; another form on it closes first, so the editor opens again on the asked one.
    /// </summary>
    private void Open(UserAdminRecordDto user, StaffEditorPurpose purpose)
    {
        if (Model.EditorMode == EntityEditorMode.Edit && Model.Editing?.UserId == user.UserId)
        {
            if (Purpose == purpose)
            {
                return;
            }

            Model.CloseEditor();
        }

        _requested = purpose;
        Model.BeginEdit(user);
    }

    private void OnModelEditorChanged(EntityEditorMode mode, UserAdminRecordDto? user)
    {
        Purpose = mode switch
        {
            EntityEditorMode.New => StaffEditorPurpose.Create,
            EntityEditorMode.Edit => _requested ?? StaffEditorPurpose.EditRole,
            _ => StaffEditorPurpose.None,
        };
        _requested = null;
        EditorChanged?.Invoke(Purpose, user);
    }
}
