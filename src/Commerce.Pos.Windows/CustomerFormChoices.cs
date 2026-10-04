using Commerce.Domain.Validation;

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

    /// <summary>Persona / Empresa: decides what the one name field holds (independent of Minorista / Mayorista).</summary>
    public static IReadOnlyList<FormChoice> PartyTypes { get; } =
        [new("Person", "Persona"), new("Company", "Empresa")];

    public static IReadOnlyList<FormChoice> TaxIdTypes { get; } =
        [new("None", "Ninguno"), new("Cuit", "CUIT"), new("Cuil", "CUIL"), new("Dni", "DNI")];

    public static IReadOnlyList<FormChoice> TaxConditions { get; } =
    [
        new("ConsumidorFinal", "Consumidor final"),
        new("ResponsableInscripto", "Responsable inscripto"),
        new("Monotributo", "Monotributo"),
        new("Exento", "Exento"),
        new("NoAplica", "No aplica"),
    ];
}

/// <summary>What an email box shows while typing: nothing, a green check, or an inline error.</summary>
public enum EmailFieldState
{
    Empty,
    Valid,
    Invalid,
}

/// <summary>
/// The UI-free rules of the desktop customer form (admin-console-field-fixes T4): the label of the one name field,
/// the live email check (the shared <see cref="EmailAddressRules"/>, the same rule the server applies) and the
/// postal code prefill from the chosen city.
/// </summary>
public static class CustomerFormRules
{
    /// <summary>A person stores "Nombre y apellido", a company its "Razón social", always in the one name field.</summary>
    public static string NameLabel(string? partyType) =>
        partyType == "Company" ? "Razón social" : "Nombre y apellido";

    /// <summary>Blank is no email (optional field); otherwise valid or invalid under the shared rule.</summary>
    public static EmailFieldState Email(string? text) =>
        string.IsNullOrWhiteSpace(text) ? EmailFieldState.Empty
        : EmailAddressRules.IsValid(text) ? EmailFieldState.Valid
        : EmailFieldState.Invalid;

    /// <summary>
    /// The postal code after the city changed: the new city's known postal code fills an empty box or replaces the
    /// one the previous city filled in; a code typed by hand is kept, and an unknown postal code never clears the box.
    /// </summary>
    public static string PostalCodeAfterCityChange(string current, string? previousCityPostalCode, string? newCityPostalCode)
    {
        if (string.IsNullOrWhiteSpace(newCityPostalCode))
        {
            return current;
        }

        var untouched = string.IsNullOrWhiteSpace(current)
            || (previousCityPostalCode is not null && string.Equals(current.Trim(), previousCityPostalCode, StringComparison.OrdinalIgnoreCase));
        return untouched ? newCityPostalCode : current;
    }

    /// <summary>
    /// The city an update sends: the selected city; <see cref="Guid.Empty"/> (clear) only when the operator changed the
    /// province or city and left none selected; otherwise null, which keeps the stored city. A stored city the combo
    /// cannot show (deactivated, or the city list failed to load) must survive an unrelated edit.
    /// </summary>
    public static Guid? CityChange(Guid? selectedCityId, bool operatorChangedCity) =>
        selectedCityId ?? (operatorChangedCity ? Guid.Empty : null);
}
