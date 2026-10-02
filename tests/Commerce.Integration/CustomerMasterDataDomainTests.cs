using Commerce.Domain.Customers;

namespace Commerce.Integration;

/// <summary>
/// Pure domain rules of the customer master data: catalog key derivation and
/// the tax id shape per type (Dni 7-8 digits, Cuit/Cuil 11 digits). No I/O.
/// </summary>
public sealed class CustomerMasterDataDomainTests
{
    [Theory]
    [InlineData("San Miguel", "san_miguel")]
    [InlineData("  Año Nuevo  Ñandú ", "ano_nuevo_nandu")]
    [InlineData("Bar / Resto", "bar_resto")]
    [InlineData("José C. Paz", "jose_c_paz")]
    [InlineData("Ya_normalizado", "ya_normalizado")]
    public void MasterDataKey_DerivesLowercaseUnaccentedUnderscoredKeys(string name, string expected)
    {
        Assert.Equal(expected, MasterDataKey.FromName(name));
    }

    [Theory]
    [InlineData("!!!")]
    [InlineData("   ")]
    public void MasterDataKey_WithNothingKeyable_ReturnsEmpty(string name)
    {
        Assert.Equal(string.Empty, MasterDataKey.FromName(name));
    }

    [Theory]
    [InlineData(TaxIdType.Dni, "12345678", "12345678")]
    [InlineData(TaxIdType.Dni, "1234567", "1234567")]
    [InlineData(TaxIdType.Dni, "12.345.678", "12345678")]
    [InlineData(TaxIdType.Cuit, "30-12345678-9", "30123456789")]
    [InlineData(TaxIdType.Cuil, "20 12345678 9", "20123456789")]
    [InlineData(TaxIdType.Cuit, "30123456789", "30123456789")]
    public void TaxIdRules_NormalizesToDigitsOnly(TaxIdType type, string raw, string expected)
    {
        Assert.True(TaxIdRules.TryNormalize(type, raw, out var normalized, out var error));
        Assert.Equal(expected, normalized);
        Assert.Null(error);
    }

    [Theory]
    [InlineData(TaxIdType.Dni, "123456")]
    [InlineData(TaxIdType.Dni, "123456789")]
    [InlineData(TaxIdType.Dni, "12A45678")]
    [InlineData(TaxIdType.Cuit, "3012345678")]
    [InlineData(TaxIdType.Cuil, "201234567890")]
    [InlineData(TaxIdType.Cuit, null)]
    [InlineData(TaxIdType.Dni, "  ")]
    public void TaxIdRules_RejectsWrongShapeOrMissingValue(TaxIdType type, string? raw)
    {
        Assert.False(TaxIdRules.TryNormalize(type, raw, out var normalized, out var error));
        Assert.Null(normalized);
        Assert.False(string.IsNullOrWhiteSpace(error));
    }

    [Fact]
    public void TaxIdRules_NoneCarriesNoTaxId()
    {
        Assert.True(TaxIdRules.TryNormalize(TaxIdType.None, null, out var normalized, out _));
        Assert.Null(normalized);
        Assert.False(TaxIdRules.TryNormalize(TaxIdType.None, "12345678", out _, out _));
    }

    private static Customer NewCustomer(TaxIdType type, string? taxId) => new(
        Guid.NewGuid(), Guid.NewGuid(), CustomerKind.Retail, "Jane Doe", null, type, taxId,
        TaxCondition.ConsumidorFinal, null, null, null, null, null, null, null, null, null, null, null, null,
        Guid.NewGuid());

    [Fact]
    public void Customer_AcceptsDniOfSevenOrEightDigits()
    {
        Assert.Equal("12345678", NewCustomer(TaxIdType.Dni, "12345678").TaxId);
        Assert.Equal("1234567", NewCustomer(TaxIdType.Dni, "1234567").TaxId);
    }

    [Theory]
    [InlineData("123456")]
    [InlineData("123456789")]
    [InlineData("12.345.678")]
    [InlineData("ABCDEFGH")]
    public void Customer_RejectsMalformedDni(string dni)
    {
        Assert.Throws<ArgumentException>(() => NewCustomer(TaxIdType.Dni, dni));
    }

    [Fact]
    public void Customer_CarriesCityBusinessTypeAndContactName()
    {
        var cityId = Guid.NewGuid();
        var businessTypeId = Guid.NewGuid();
        var customer = new Customer(
            Guid.NewGuid(), Guid.NewGuid(), CustomerKind.Retail, "Bar Pepe", null, TaxIdType.None, null,
            TaxCondition.ConsumidorFinal, null, null, null, null, null, null, null, null, null, null, null, null,
            Guid.NewGuid(), cityId: cityId, businessTypeId: businessTypeId, contactName: "Pepe");

        Assert.Equal(cityId, customer.CityId);
        Assert.Equal(businessTypeId, customer.BusinessTypeId);
        Assert.Equal("Pepe", customer.ContactName);
    }
}
