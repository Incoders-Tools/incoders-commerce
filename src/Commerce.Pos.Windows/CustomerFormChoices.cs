namespace Commerce.Pos.Windows;

/// <summary>A stored value and the Spanish label the operator sees for it.</summary>
public sealed record FormChoice(string Value, string Label);

/// <summary>
/// The fixed choices of the customer form. The wire values are the API's; only
/// the labels are Spanish.
/// </summary>
public static class CustomerFormChoices
{
    public static IReadOnlyList<FormChoice> Kinds { get; } =
        [new("Retail", "Minorista"), new("Wholesale", "Mayorista")];

    public static IReadOnlyList<FormChoice> TaxIdTypes { get; } =
        [new("None", "Ninguno"), new("Cuit", "CUIT"), new("Cuil", "CUIL")];

    public static IReadOnlyList<FormChoice> TaxConditions { get; } =
    [
        new("ConsumidorFinal", "Consumidor final"),
        new("ResponsableInscripto", "Responsable inscripto"),
        new("Monotributo", "Monotributo"),
        new("Exento", "Exento"),
        new("NoAplica", "No aplica"),
    ];
}
