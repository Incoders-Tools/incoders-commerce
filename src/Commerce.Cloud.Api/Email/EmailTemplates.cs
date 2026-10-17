using System.Net;
using System.Text.RegularExpressions;

namespace Commerce.Cloud.Api.Email;

/// <summary>
/// The organization a system email speaks for (one shared deployment serves
/// every client, so the brand is per organization, never per build). Values
/// come from the <c>organizations</c> row; <see cref="EmailTemplates"/>
/// treats every field as untrusted.
/// </summary>
public sealed record EmailBrand(string Name, string? LogoUrl, string? PrimaryColor)
{
    public static EmailBrand Default { get; } = new("Commerce", null, null);
}

/// <summary>
/// Spanish transactional emails (railway-deploy-pipeline S14). Each message
/// carries an HTML body for mail clients and a plain-text alternative for
/// clients that block HTML. The HTML uses tables and inline styles only,
/// which is what mail clients render reliably. Every value that is not a
/// literal of this class is HTML-encoded.
/// </summary>
public static partial class EmailTemplates
{
    public const string DefaultPrimaryColor = "#1f2937";

    public static EmailMessage PasswordReset(string to, EmailBrand brand, string link)
    {
        var name = BrandName(brand);
        var textBody =
            "Hola:\n\n" +
            $"Recibimos un pedido para restablecer su contraseña de {name}. Para elegir una nueva, abra este enlace:\n\n" +
            $"{link}\n\n" +
            "El enlace vence en 1 hora y se puede usar una sola vez. " +
            "Si usted no lo pidió, ignore este correo: su contraseña no cambia.\n\n" +
            name;

        var content =
            Paragraph("Hola:") +
            Paragraph($"Recibimos un pedido para restablecer su contraseña de {Encode(name)}. Para elegir una nueva, use este botón:") +
            Button(link, "Restablecer contraseña", brand) +
            Paragraph("El enlace vence en 1 hora y se puede usar una sola vez.") +
            Paragraph("Si el botón no funciona, copie y pegue esta dirección en su navegador:") +
            $"<p style=\"margin:0 0 16px;font-size:13px;line-height:20px;word-break:break-all;color:#4b5563;\">{Encode(link)}</p>" +
            Paragraph("Si usted no lo pidió, ignore este correo: su contraseña no cambia.");

        return new EmailMessage(
            to, $"Restablezca su contraseña de {name}", Layout(brand, "Restablecer contraseña", content), textBody);
    }

    public static EmailMessage GuestVerificationCode(string to, EmailBrand brand, string code)
    {
        var name = BrandName(brand);
        // The code stays the only number in the first sentence, so a reader
        // finds it at a glance.
        var textBody =
            $"Su código de verificación es {code}.\n\n" +
            $"Ingréselo en {name} para confirmar su pedido. " +
            "El código vence en 10 minutos y se puede usar una sola vez.\n\n" +
            "Si usted no pidió este código, ignore este correo.\n\n" +
            name;

        var content =
            Paragraph("Su código de verificación es:") +
            $"<p style=\"margin:0 0 16px;font-size:32px;line-height:40px;font-weight:700;letter-spacing:6px;color:{Color(brand)};\">{Encode(code)}</p>" +
            Paragraph($"Ingréselo en {Encode(name)} para confirmar su pedido. El código vence en 10 minutos y se puede usar una sola vez.") +
            Paragraph("Si usted no pidió este código, ignore este correo.");

        return new EmailMessage(
            to, $"Su código de verificación de {name}", Layout(brand, "Código de verificación", content), textBody);
    }

    private static string Layout(EmailBrand brand, string title, string content)
    {
        var name = Encode(BrandName(brand));
        var header = LogoUrl(brand) is { } logo
            ? $"<img src=\"{Encode(logo)}\" alt=\"{name}\" height=\"48\" style=\"display:block;height:48px;max-width:240px;border:0;\">"
            : $"<span style=\"font-size:20px;line-height:28px;font-weight:700;color:#ffffff;\">{name}</span>";
        var headerBackground = LogoUrl(brand) is null ? Color(brand) : "#ffffff";

        return
            "<!DOCTYPE html>" +
            "<html lang=\"es\">" +
            "<head><meta charset=\"utf-8\"><meta name=\"viewport\" content=\"width=device-width,initial-scale=1\">" +
            $"<title>{title}</title></head>" +
            "<body style=\"margin:0;padding:0;background-color:#f3f4f6;\">" +
            "<table role=\"presentation\" width=\"100%\" cellpadding=\"0\" cellspacing=\"0\" style=\"background-color:#f3f4f6;\">" +
            "<tr><td align=\"center\" style=\"padding:24px 12px;\">" +
            "<table role=\"presentation\" width=\"100%\" cellpadding=\"0\" cellspacing=\"0\" style=\"max-width:560px;background-color:#ffffff;border-radius:8px;font-family:Arial,Helvetica,sans-serif;\">" +
            $"<tr><td style=\"padding:20px 32px;background-color:{headerBackground};border-radius:8px 8px 0 0;border-bottom:4px solid {Color(brand)};\">{header}</td></tr>" +
            $"<tr><td style=\"padding:32px;\">{content}</td></tr>" +
            "<tr><td style=\"padding:16px 32px;border-top:1px solid #e5e7eb;font-size:12px;line-height:18px;color:#6b7280;\">" +
            $"Este correo lo envió {name} de forma automática. No responda a este mensaje." +
            "</td></tr>" +
            "</table></td></tr></table></body></html>";
    }

    // `html`, `title` and `label` are literals of this class or already
    // encoded; only the helpers below encode.
    private static string Paragraph(string html) =>
        $"<p style=\"margin:0 0 16px;font-size:15px;line-height:22px;color:#111827;\">{html}</p>";

    private static string Button(string link, string label, EmailBrand brand) =>
        "<table role=\"presentation\" cellpadding=\"0\" cellspacing=\"0\" style=\"margin:8px 0 24px;\"><tr>" +
        $"<td style=\"border-radius:6px;background-color:{Color(brand)};\">" +
        $"<a href=\"{Encode(link)}\" style=\"display:inline-block;padding:12px 24px;font-size:15px;font-weight:700;color:#ffffff;text-decoration:none;border-radius:6px;\">{label}</a>" +
        "</td></tr></table>";

    private static string BrandName(EmailBrand brand) =>
        string.IsNullOrWhiteSpace(brand.Name) ? EmailBrand.Default.Name : brand.Name.Trim();

    // Only `#rrggbb` reaches a style attribute (the same rule the branding
    // endpoint enforces on write); anything else falls back.
    private static string Color(EmailBrand brand) =>
        brand.PrimaryColor is { } color && HexColor().IsMatch(color) ? color : DefaultPrimaryColor;

    // Mail clients block or warn on non-HTTPS images, and any other scheme
    // (javascript:, data:) has no place in an email.
    private static string? LogoUrl(EmailBrand brand) =>
        Uri.TryCreate(brand.LogoUrl, UriKind.Absolute, out var uri) && uri.Scheme == Uri.UriSchemeHttps
            ? brand.LogoUrl
            : null;

    private static string Encode(string value) => WebUtility.HtmlEncode(value);

    [GeneratedRegex("^#[0-9a-fA-F]{6}$")]
    private static partial Regex HexColor();
}
