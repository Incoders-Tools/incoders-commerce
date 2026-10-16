using Commerce.Domain.Validation;

namespace Commerce.Integration;

/// <summary>
/// admin-console-field-fixes T3b: the one email format rule every email field shares (customer, customer contact,
/// staff user, supplier). Trimmed, case-insensitive, at most 254 characters.
/// </summary>
public sealed class EmailAddressRulesTests
{
    [Theory]
    [InlineData("ana@mail.com")]
    [InlineData("a.b+c@mail.com.ar")]
    [InlineData("ANA@MAIL.COM")]
    [InlineData("  ana@mail.com  ")]
    [InlineData("o'brien_99@sub-domain.example.org")]
    [InlineData("admin@vacaverde.local")]
    [InlineData("x@a1.io")]
    public void Valid_addresses_are_accepted(string value) => Assert.True(EmailAddressRules.IsValid(value));

    [Theory]
    [InlineData("ana@")]
    [InlineData("ana@mail")]
    [InlineData("ana@@mail.com")]
    [InlineData("ana@-mail.com")]
    [InlineData("ana@mail-.com")]
    [InlineData("ana@mail.c")]
    [InlineData("ana@mail.c0m")]
    [InlineData("ana@mail..com")]
    [InlineData("@mail.com")]
    [InlineData("ana")]
    [InlineData(".ana@mail.com")]
    [InlineData("ana.@mail.com")]
    [InlineData("an..a@mail.com")]
    [InlineData("ana maria@mail.com")]
    [InlineData("ana@mail .com")]
    [InlineData("ana@mail.com.")]
    [InlineData("ana@.mail.com")]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(null)]
    public void Invalid_addresses_are_refused(string? value) => Assert.False(EmailAddressRules.IsValid(value));

    [Fact]
    public void Addresses_longer_than_254_characters_are_refused()
    {
        var label = new string('a', 60);
        var domain = string.Join('.', Enumerable.Repeat(label, 4)) + ".com"; // 4 * 61 + 3 = 247
        Assert.True(EmailAddressRules.IsValid("ab@" + domain));        // 250
        Assert.False(EmailAddressRules.IsValid("abcdefg@" + domain));  // 255
    }

    [Fact]
    public void A_local_part_longer_than_64_characters_is_refused()
    {
        Assert.True(EmailAddressRules.IsValid(new string('a', 64) + "@mail.com"));
        Assert.False(EmailAddressRules.IsValid(new string('a', 65) + "@mail.com"));
    }

    [Fact]
    public void TryNormalize_trims_keeps_blank_as_absent_and_reports_invalid_values()
    {
        Assert.True(EmailAddressRules.TryNormalize("  Ana@Mail.com ", out var trimmed));
        Assert.Equal("Ana@Mail.com", trimmed);

        Assert.True(EmailAddressRules.TryNormalize("   ", out var blank));
        Assert.Null(blank);
        Assert.True(EmailAddressRules.TryNormalize(null, out var absent));
        Assert.Null(absent);

        Assert.False(EmailAddressRules.TryNormalize("ana@mail", out var invalid));
        Assert.Null(invalid);
    }
}
