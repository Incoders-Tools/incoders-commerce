using Commerce.Domain.Customers;

namespace Commerce.Integration;

/// <summary>
/// admin-console-field-fixes T3b: a customer is a Person or a Company (independent of Retail / Wholesale). A customer
/// that does not say which is a Company when its tax id is a CUIT, otherwise a Person - the same rule the migration
/// backfills existing customers with.
/// </summary>
public sealed class PartyTypeRulesTests
{
    [Theory]
    [InlineData(TaxIdType.Cuit, PartyType.Company)]
    [InlineData(TaxIdType.Cuil, PartyType.Person)]
    [InlineData(TaxIdType.Dni, PartyType.Person)]
    [InlineData(TaxIdType.None, PartyType.Person)]
    public void The_default_is_company_only_for_a_cuit(TaxIdType taxIdType, PartyType expected) =>
        Assert.Equal(expected, PartyTypeRules.DefaultFor(taxIdType));

    [Theory]
    [InlineData("Person", PartyType.Person)]
    [InlineData("Company", PartyType.Company)]
    public void Known_names_parse(string raw, PartyType expected)
    {
        Assert.True(PartyTypeRules.TryParse(raw, out var parsed));
        Assert.Equal(expected, parsed);
    }

    [Theory]
    [InlineData("person")]
    [InlineData("1")]
    [InlineData("Empresa")]
    [InlineData("")]
    public void Anything_else_is_refused(string raw) => Assert.False(PartyTypeRules.TryParse(raw, out _));
}
