using Commerce.Domain.Pricing;

namespace Commerce.Domain.Sales;

/// <summary>
/// How the customer paid at the point of sale (pos-scan-sale "Tender Recorded
/// at the Moment of Sale"): exactly one tender per sale. The POS only RECORDS it:
/// card and QR are charged on the merchant's own terminal or app, so no provider
/// is involved. This is deliberately NOT <c>Commerce.Domain.Payments.PaymentMethod</c>,
/// which types order payment attempts (persisted with a database CHECK and served
/// by cloud endpoints); a sale tender is a POS record carried in the sale payload.
/// <see cref="AmountReceived"/> and <see cref="ChangeGiven"/> exist only for cash.
/// </summary>
public sealed record SaleTender(string Method, decimal? AmountReceived = null, decimal? ChangeGiven = null)
{
    public const string Cash = "cash";
    public const string Card = "card";
    public const string Qr = "qr";

    public static bool IsKnownMethod(string? method) => method is Cash or Card or Qr;
}

/// <summary>Builds the tender of a sale and enforces the cash rules.</summary>
public static class SaleTenderRules
{
    /// <summary>
    /// Cash: the amount received must be at least the total and have at most two
    /// decimals. The change is received minus total.
    /// </summary>
    public static bool TryCash(decimal total, decimal received, out SaleTender tender)
    {
        if (received < total || Money.Round2(received) != received)
        {
            tender = null!;
            return false;
        }

        tender = new SaleTender(SaleTender.Cash, received, received - total);
        return true;
    }

    public static SaleTender Card() => new(SaleTender.Card);

    public static SaleTender Qr() => new(SaleTender.Qr);
}
