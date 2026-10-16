namespace Commerce.Domain.CashSessions;

/// <summary>
/// Money taken out of or put into the drawer of an open cash session outside a sale ("movimiento de caja"): a
/// <see cref="Withdrawal"/> (to the branch safe, to deposit in the bank, to pay an expense...) or a
/// <see cref="Deposit"/> (change brought from the safe...). Both change the cash the drawer should hold at close, and
/// both reach the company's treasury when they sync. The counterpart says where the money went or came from.
/// </summary>
public static class CashMovement
{
    public const string Withdrawal = "Withdrawal";
    public const string Deposit = "Deposit";

    /// <summary>To or from the branch safe: in the treasury it is a transfer between the drawer and the safe.</summary>
    public const string Safe = "Safe";

    /// <summary>A withdrawal to deposit in the bank (the administration records the deposit slip in the bank account).</summary>
    public const string Bank = "Bank";

    /// <summary>A withdrawal that paid an expense from the drawer.</summary>
    public const string Expense = "Expense";

    public const string Other = "Other";

    /// <summary>The longest reason accepted (it travels in the envelope and the cloud audit).</summary>
    public const int MaxReasonLength = 200;

    public static bool IsValidKind(string? kind) => kind is Withdrawal or Deposit;

    /// <summary>Where a withdrawal can go, or where a deposit can come from.</summary>
    public static bool IsValidCounterpart(string kind, string? counterpart) => kind switch
    {
        Withdrawal => counterpart is Safe or Bank or Expense or Other,
        Deposit => counterpart is Safe or Other,
        _ => false,
    };

    /// <summary>"Retiro a caja fuerte", "Ingreso desde caja fuerte"... as the POS and the treasury name it.</summary>
    public static string Describe(string kind, string counterpart) => (kind, counterpart) switch
    {
        (Withdrawal, Safe) => "Retiro a caja fuerte",
        (Withdrawal, Bank) => "Retiro para depositar en banco",
        (Withdrawal, Expense) => "Retiro para pago de gasto",
        (Withdrawal, _) => "Retiro de caja",
        (Deposit, Safe) => "Ingreso desde caja fuerte",
        _ => "Ingreso de caja",
    };
}

/// <summary>One cash movement of a session as the totals need it.</summary>
public readonly record struct CashSessionCashMovement(string Kind, decimal Amount);
