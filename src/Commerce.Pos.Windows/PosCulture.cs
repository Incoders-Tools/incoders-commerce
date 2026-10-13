using System.Globalization;

namespace Commerce.Pos.Windows;

/// <summary>
/// owner feedback P3: the POS reads dates and money the Argentine way, whatever Windows' language: dd/mm/aaaa, the
/// day's name in Spanish, "$ 1.234,00" (as the web).
/// </summary>
public static class PosCulture
{
    public static readonly CultureInfo Spanish = CultureInfo.GetCultureInfo("es-AR");

    /// <summary>A local moment: "05/01/2026 09:07".</summary>
    public static string Moment(DateTime local) => local.ToString("dd'/'MM'/'yyyy HH':'mm", CultureInfo.InvariantCulture);

    /// <summary>A moment already in local time (<c>ToLocalTime()</c>): "05/01/2026 09:07".</summary>
    public static string Moment(DateTimeOffset local) => Moment(local.DateTime);

    /// <summary>A day: "05/01/2026".</summary>
    public static string Day(DateOnly day) => day.ToString("dd'/'MM'/'yyyy", CultureInfo.InvariantCulture);
}
