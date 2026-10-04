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

    /// <summary>A section change was refused because the previous section is still finishing a request.</summary>
    public const string PreviousOperationRunning =
        "Esperá a que termine la operación anterior para abrir otra sección.";

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

    /// <summary>Shown inside the sale result while the terminal does not know its register yet.</summary>
    public const string SaleNumberPending = "número pendiente";

    public const string RegisterNumbersExhausted =
        "Esta sucursal ya usó todos los números de caja disponibles. Avisá a soporte técnico.";

    // ---- Sync summary (never names an operation; the log keeps the ids) -------

    public const string SyncNothingPending = "No hay nada pendiente de sincronizar.";

    public static string SyncSucceeded(int count) =>
        count == 1 ? "Se sincronizó 1 operación." : $"Se sincronizaron {count} operaciones.";

    public static string SyncPartiallyFailed(int succeeded, int failed)
    {
        var failure = failed == 1
            ? "1 no se pudo enviar y se reintentará automáticamente"
            : $"{failed} no se pudieron enviar y se reintentarán automáticamente";
        return succeeded == 0
            ? failed == 1
                ? "No se pudo enviar la operación; se reintentará automáticamente."
                : "No se pudo enviar ninguna operación; se reintentará automáticamente."
            : $"{SyncSucceededLead(succeeded)}; {failure}.";
    }

    private static string SyncSucceededLead(int count) =>
        count == 1 ? "Se sincronizó 1 operación" : $"Se sincronizaron {count} operaciones";

    // ---- Pairing window -----------------------------------------------------

    public const string SelectBranchFirst = "Seleccioná una sucursal primero.";

    public const string MultipleBranchesFound =
        "Se encontraron varias sucursales. Seleccioná una para continuar.";

    public const string PairingFailed = "No se pudo configurar la terminal.";

    public const string Pairing = "Emparejando…";

    // ---- Lock screen (operator sign-in) -------------------------------------

    public const string SelectOperatorFirst = "Seleccioná un operador primero.";

    public const string IncorrectPin = "PIN incorrecto.";

    public const string InvalidPinFormat =
        "El PIN debe tener exactamente 6 dígitos y no ser un patrón simple (por ejemplo 123456 o 111111).";

    public const string PinMismatch = "El PIN y su confirmación no coinciden.";

    public const string ProvisioningFailed = "No se pudo guardar el operador.";

    public const string Verifying = "Verificando…";

    public const string EmailAndPasswordToSignIn = "Ingresá tu correo y tu contraseña.";

    public const string PinExpired =
        "Tu PIN venció en esta terminal. Ingresá con tu correo y contraseña para renovarlo.";

    // ---- Staff / customer management windows --------------------------------

    public const string NoPermissionToManageStaff = "No tenés permiso para administrar el personal.";

    public const string StaffUserNotFound = "No se encontró el usuario.";

    public const string NoPermissionToManageCustomers = "No tenés permiso para administrar clientes.";

    public const string CustomerNotFound = "No se encontró el cliente.";

    public const string CustomerModified = "Otra persona modificó el cliente mientras tanto. Revisá la lista actualizada y volvé a intentarlo.";

    public const string SaveFailed = "No se pudieron guardar los cambios.";

    public const string Saved = "Cambios guardados.";

    public const string PasswordResetFailed = "No se pudo restablecer la contraseña.";

    public const string PasswordResetDone = "Contraseña restablecida.";

    public const string SelectStaffUserFirst = "Seleccioná un usuario primero.";

    public const string Loading = "Cargando…";

    public const string Saving = "Guardando…";

    public const string ResettingPassword = "Restableciendo contraseña…";

    public const string CustomersLoadFailed = "No se pudo cargar la lista de clientes. Intentá de nuevo.";

    public const string UsersLoadFailed = "No se pudo cargar la lista de personal. Intentá de nuevo.";

    public const string BranchRequired = "Elegí al menos una sucursal para el usuario.";

    public const string BranchNotInOrganization = "Alguna de las sucursales elegidas no pertenece a esta organización.";

    public const string InvalidData = "Los datos ingresados no son válidos. Revisalos e intentá de nuevo.";

    // ---- Shell sections (Clientes, Personal) --------------------------------

    /// <summary>The server refused the signed-in operator for a management call (not revoked/admin/branch: it never says which).</summary>
    public const string OperatorNotAuthorized =
        "El servidor no autorizó a tu usuario para administrar desde esta terminal.";

    /// <summary>Shown on the sale after the shell left Clientes or Personal because the server refused the operator.</summary>
    public const string ManagementAccessRefused =
        "Tu usuario no puede administrar clientes ni personal en esta terminal (puede estar dado de baja, sin permiso de administrador o sin esta sucursal asignada). Volviste a la venta.";

    public const string ProvincesLoadFailed = "No se pudo cargar la lista de provincias. Intentá de nuevo.";

    public const string CitiesLoadFailed = "No se pudo cargar la lista de ciudades de la provincia. Intentá de nuevo.";

    public const string InvalidEmail = "El correo no es válido. Revisalo (por ejemplo, ana@correo.com).";

    public const string CannotDeactivateSelf = "No podés darte de baja a vos mismo.";

    public const string NotAStaffUser = "Esta cuenta es de un cliente, no de personal, y no se puede dar de baja desde acá.";

    public const string PermissionsExceedCaller =
        "No tenés permiso para hacer eso con este usuario: tiene más permisos que vos.";

    public const string StaffBranchNotInScope =
        "No tenés permiso para gestionar a este usuario: pertenece a una sucursal que no administrás.";

    public const string StaffCreated = "Usuario creado. Entregale el correo y la contraseña: creará su PIN la primera vez que ingrese en una terminal.";

    public const string StaffDeactivated = "Usuario dado de baja.";

    public const string StaffReactivated = "Usuario reactivado.";

    public const string StaffDeactivateFailed = "No se pudo cambiar el estado del usuario.";

    public const string EmailAndPasswordRequired = "Ingresá el correo y la contraseña inicial del nuevo usuario.";

    public const string NewPasswordRequired = "Ingresá la nueva contraseña.";

    public const string UpdatingStatus = "Actualizando…";

    public const string OperatorRemoved = "Operador quitado de esta terminal.";

    public const string DisplayNameRequired = "El nombre para mostrar es obligatorio.";

    public const string DiscountMustBeNumber = "El porcentaje de descuento debe ser un número.";
}
