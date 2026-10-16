using System.Globalization;
using Commerce.Domain.Sync;

namespace Commerce.Pos.Windows;

/// <summary>
/// What the operator reads after a sale (neutral Rioplatense voseo; UI-free so it is tested
/// without a window). The sale is named by its human number (`V01-C1-125`), never by its
/// GUID, and the text never mentions where it was stored. A sale committed before the terminal
/// knew its register has no number yet and says so; the number is never invented later.
/// </summary>
public static class SaleResultMessage
{
    /// <summary>`Venta V01-C1-125 registrada por $855,00 (Efectivo).`</summary>
    public static string Registered(SaleEffect effect, string tenderText, IFormatProvider? culture = null)
    {
        var total = effect.TotalAmount.ToString("C", culture ?? CultureInfo.CurrentCulture);
        return effect.Number is { } number
            ? $"Venta {number.Format()} registrada por {total} ({tenderText})."
            : $"Venta registrada por {total} ({tenderText}, {PosMessages.SaleNumberPending}).";
    }

    public static string AlreadyRegistered(SaleEffect effect) =>
        effect.Number is { } number
            ? $"La venta {number.Format()} ya estaba registrada (reintento idempotente)."
            : "Esta venta ya estaba registrada (reintento idempotente).";

    /// <summary>
    /// The tooltip that explains how the number is composed, or null when the sale has none:
    /// `V = Venta · 01 = Sucursal · C1 = Caja 1 · 125 = número de venta de esta caja`.
    /// </summary>
    public static string? Composition(SaleEffect effect) =>
        effect.Number is { } number
            ? $"{SaleNumberTypeLetter} = Venta · {number.Branch.Format()} = Sucursal · {number.Register.Format()} = Caja {number.Register.Value} · " +
              $"{number.Sequence.ToString(CultureInfo.InvariantCulture)} = número de venta de esta caja"
            : null;

    private static string SaleNumberTypeLetter => Commerce.Domain.Sales.SaleNumber.TypeLetter.ToString();
}
