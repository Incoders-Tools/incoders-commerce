namespace Commerce.Domain.Customers;

/// <summary>
/// Whether a customer is a person or a company (admin-console-field-fixes T3b), independent of the commercial
/// <see cref="CustomerKind"/>. It decides what the single customer name means: a person's full name or a company's
/// legal name ("Razón social"); the person at a company goes in the contact persons.
/// </summary>
public enum PartyType
{
    Person,
    Company,
}

public static class PartyTypeRules
{
    public const string Message = "partyType must be one of: Person, Company.";

    /// <summary>A customer that does not say which is a Company when its tax id is a CUIT, otherwise a Person (0040 backfill).</summary>
    public static PartyType DefaultFor(TaxIdType taxIdType) => taxIdType == TaxIdType.Cuit ? PartyType.Company : PartyType.Person;

    /// <summary>Exact member names only ("Person", "Company"), never numbers or other casing.</summary>
    public static bool TryParse(string? raw, out PartyType partyType)
    {
        partyType = default;
        return raw is nameof(PartyType.Person) or nameof(PartyType.Company) && Enum.TryParse(raw, out partyType);
    }
}
