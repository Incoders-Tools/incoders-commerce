namespace Commerce.Pos.Windows;

/// <summary>
/// The single catalog of operator-facing texts for failures and progress in
/// the POS (neutral Rioplatense voseo). Clients and windows take their
/// messages from here so the wording stays consistent and reviewable; the
/// technical detail of every failure goes to the log (<see cref="PosLog"/>),
/// never to the screen.
/// </summary>
public static class PosMessages
{
    // ---- Server / transport -------------------------------------------------

    public const string ServerUnreachable =
        "No se pudo conectar con el servidor. Revisá la conexión a internet e intentá de nuevo.";

    public const string ServerError =
        "El servidor no pudo procesar la solicitud. Intentá de nuevo en unos minutos.";

    public const string UnexpectedResponse =
        "El servidor respondió de una forma inesperada. Intentá de nuevo en unos minutos; si sigue pasando, avisá a soporte técnico.";

    public const string AccessDenied =
        "El servidor rechazó la solicitud por falta de permisos.";

    /// <summary>The device credential is unknown to the server: the terminal must be paired again.</summary>
    public const string TerminalNotRecognized =
        "El servidor no reconoce esta terminal. Abrí Configuración y volvé a configurarla para seguir sincronizando.";

    public const string SessionExpired =
        "La sesión de administrador venció. Cerrá esta ventana y volvé a ingresar.";

    /// <summary>Last resort for an exception nobody anticipated (global handler and busy guard).</summary>
    public const string Unexpected =
        "Ocurrió un error inesperado. El detalle quedó registrado para soporte técnico.";

    // ---- Credentials and access ---------------------------------------------

    public const string InvalidCredentials = "Correo o contraseña incorrectos.";

    public const string OperatorNotPermitted =
        "Este usuario no tiene permiso para operar el punto de venta. Pedí a un administrador que le asigne el rol Cajero.";

    public const string BranchNotInScope =
        "Este usuario no tiene asignada la sucursal de esta terminal. Pedí a un administrador que se la asigne.";

    public const string NoBranchesAssigned =
        "Este usuario no tiene sucursales asignadas. Pedí a un administrador que le asigne una.";

    public const string SelectedBranchNotInScope =
        "La sucursal elegida no está asignada a este usuario.";

    // ---- Pairing window -----------------------------------------------------

    public const string SelectBranchFirst = "Seleccioná una sucursal primero.";

    public const string MultipleBranchesFound =
        "Se encontraron varias sucursales. Seleccioná una para continuar.";

    public const string PairingFailed = "No se pudo configurar la terminal.";

    public const string Pairing = "Emparejando…";

    // ---- Operator login window ----------------------------------------------

    public const string SelectOperatorFirst = "Seleccioná un operador primero.";

    public const string IncorrectPin = "PIN incorrecto.";

    public const string InvalidPinFormat =
        "El PIN debe tener exactamente 6 dígitos y no ser un patrón simple (por ejemplo 123456 o 111111).";

    public const string PinMismatch = "El PIN y su confirmación no coinciden.";

    public const string ProvisioningFailed = "No se pudo guardar el operador.";

    public const string Verifying = "Verificando…";

    // ---- Staff / customer management windows --------------------------------

    public const string NoPermissionToManageStaff = "No tenés permiso para administrar el personal.";

    public const string StaffUserNotFound = "No se encontró el usuario.";

    public const string NoPermissionToManageCustomers = "No tenés permiso para administrar clientes.";

    public const string CustomerNotFound = "No se encontró el cliente.";

    public const string SignInFailed = "No se pudo iniciar sesión.";

    public const string SaveFailed = "No se pudieron guardar los cambios.";

    public const string Saved = "Cambios guardados.";

    public const string PasswordResetFailed = "No se pudo restablecer la contraseña.";

    public const string PasswordResetDone = "Contraseña restablecida.";

    public const string SelectStaffUserFirst = "Seleccioná un usuario primero.";

    public const string SigningIn = "Ingresando…";

    public const string Saving = "Guardando…";

    public const string ResettingPassword = "Restableciendo contraseña…";

    public const string CustomersLoadFailed = "No se pudo cargar la lista de clientes. Intentá de nuevo.";

    public const string UsersLoadFailed = "No se pudo cargar la lista de personal. Intentá de nuevo.";

    public const string BranchRequired = "Elegí al menos una sucursal para el usuario.";

    public const string BranchNotInOrganization = "Alguna de las sucursales elegidas no pertenece a esta organización.";

    public const string InvalidData = "Los datos ingresados no son válidos. Revisalos e intentá de nuevo.";
}
