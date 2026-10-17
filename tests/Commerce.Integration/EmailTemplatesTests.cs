using Commerce.Cloud.Api.Email;

namespace Commerce.Integration;

/// <summary>
/// Pins the Spanish, organization-branded transactional emails
/// (railway-deploy-pipeline S14 / T13): every system email carries an HTML
/// body and a plain-text alternative, the brand comes from the organization,
/// and nothing caller-controlled reaches the HTML unescaped.
/// </summary>
public sealed class EmailTemplatesTests
{
    private static readonly EmailBrand VacaVerde = new("Vaca Verde", "https://cdn.example.com/logo.png", "#2f5d3a");
    private const string ResetLink = "https://app.vaca-verde.com.ar/reset-password/abc_DEF-123";

    [Fact]
    public void PasswordReset_IsSpanish_Branded_AndCarriesTheLinkInBothBodies()
    {
        var message = EmailTemplates.PasswordReset("ana@example.com", VacaVerde, ResetLink);

        Assert.Equal("ana@example.com", message.To);
        Assert.Equal("Restablezca su contraseña de Vaca Verde", message.Subject);

        Assert.Contains(ResetLink, message.TextBody);
        Assert.Contains("vence en 1 hora", message.TextBody);
        Assert.Contains("se puede usar una sola vez", message.TextBody);
        Assert.Contains("Si usted no lo pidió, ignore este correo", message.TextBody);

        Assert.Contains("<html lang=\"es\">", message.HtmlBody);
        Assert.Contains($"href=\"{ResetLink}\"", message.HtmlBody);
        Assert.Contains("Restablecer contraseña", message.HtmlBody);
        Assert.Contains("vence en 1 hora", message.HtmlBody);
        Assert.Contains("Vaca Verde", message.HtmlBody);
        Assert.Contains("#2f5d3a", message.HtmlBody);
        Assert.Contains("<img src=\"https://cdn.example.com/logo.png\"", message.HtmlBody);
    }

    [Fact]
    public void GuestVerificationCode_IsSpanish_Branded_AndStartsWithTheCode()
    {
        var message = EmailTemplates.GuestVerificationCode("guest@example.com", VacaVerde, "042917");

        Assert.Equal("guest@example.com", message.To);
        Assert.Equal("Su código de verificación de Vaca Verde", message.Subject);

        // The code is the only number before the first period, so a reader
        // (and the tests' ExtractCode helpers) finds it unambiguously.
        Assert.StartsWith("Su código de verificación es 042917.", message.TextBody);
        Assert.Contains("vence en 10 minutos", message.TextBody);
        Assert.Contains("se puede usar una sola vez", message.TextBody);

        Assert.Contains("<html lang=\"es\">", message.HtmlBody);
        Assert.Contains(">042917<", message.HtmlBody);
        Assert.Contains("vence en 10 minutos", message.HtmlBody);
        Assert.Contains("#2f5d3a", message.HtmlBody);
    }

    [Fact]
    public void Templates_HtmlEncodeTheBrandName_AndTheLink()
    {
        var hostile = new EmailBrand("A & B <script>alert(1)</script>", null, null);
        var link = "https://app.example.com/reset-password/x\"onmouseover=\"y";

        var reset = EmailTemplates.PasswordReset("a@example.com", hostile, link);
        var code = EmailTemplates.GuestVerificationCode("a@example.com", hostile, "123456");

        foreach (var html in new[] { reset.HtmlBody, code.HtmlBody })
        {
            Assert.DoesNotContain("<script>", html);
            Assert.Contains("A &amp; B &lt;script&gt;", html);
        }
        Assert.DoesNotContain("x\"onmouseover", reset.HtmlBody);
        Assert.Contains("x&quot;onmouseover=&quot;y", reset.HtmlBody);

        // Plain text is not HTML: the name stays as written there.
        Assert.Equal("Restablezca su contraseña de A & B <script>alert(1)</script>", reset.Subject);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("red")]
    [InlineData("#abc")]
    [InlineData("#2f5d3a;background:url(x)")]
    public void Templates_FallBackToTheDefaultColor_WhenThePrimaryColorIsMissingOrInvalid(string? color)
    {
        var message = EmailTemplates.PasswordReset("a@example.com", new EmailBrand("Org", null, color), ResetLink);

        Assert.Contains(EmailTemplates.DefaultPrimaryColor, message.HtmlBody);
        if (color is not null)
        {
            Assert.DoesNotContain(color, message.HtmlBody);
        }
    }

    [Theory]
    [InlineData(null)]
    [InlineData("http://cdn.example.com/logo.png")]
    [InlineData("javascript:alert(1)")]
    [InlineData("not a url")]
    public void Templates_ShowNoLogo_UnlessItIsAnAbsoluteHttpsUrl(string? logoUrl)
    {
        var message = EmailTemplates.GuestVerificationCode("a@example.com", new EmailBrand("Org", logoUrl, null), "123456");

        Assert.DoesNotContain("<img", message.HtmlBody);
        Assert.Contains("Org", message.HtmlBody);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void Templates_UseTheDefaultBrandName_WhenTheOrganizationHasNone(string? name)
    {
        var message = EmailTemplates.PasswordReset("a@example.com", new EmailBrand(name!, null, null), ResetLink);

        Assert.Equal($"Restablezca su contraseña de {EmailBrand.Default.Name}", message.Subject);
    }
}
