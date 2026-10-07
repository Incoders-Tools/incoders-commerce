namespace Commerce.Domain.CurrentAccounts;

/// <summary>Where the payment terms of a sale on account came from.</summary>
public enum PaymentTermsSource
{
    /// <summary>The customer's own payment terms.</summary>
    Customer,

    /// <summary>The organization's default, because the customer has none of its own.</summary>
    Organization,
}

/// <summary>The days a customer has to pay a sale on current account, and where they came from.</summary>
public sealed record PaymentTerms(int Days, PaymentTermsSource Source)
{
    public const int MinDays = 0;
    public const int MaxDays = 365;

    /// <summary>The organization default when nothing was configured.</summary>
    public const int DefaultDays = 30;

    /// <summary>
    /// The rule: the customer's own days when it has them, otherwise the organization's default. 0 days means the sale is
    /// due the same day.
    /// </summary>
    public static PaymentTerms For(int? customerDays, int organizationDefaultDays) =>
        customerDays is { } days
            ? new PaymentTerms(days, PaymentTermsSource.Customer)
            : new PaymentTerms(organizationDefaultDays, PaymentTermsSource.Organization);

    public static bool IsValidDays(int days) => days is >= MinDays and <= MaxDays;

    /// <summary>When a sale on account made on <paramref name="saleDate"/> is due.</summary>
    public DateOnly DueOn(DateOnly saleDate) => saleDate.AddDays(Days);
}
