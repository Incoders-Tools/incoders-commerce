namespace Commerce.Cloud.Api.Persistence;

/// <summary>
/// Input to <see cref="PostgresOrganizationStore.TryCreateBootstrapAsync"/> —
/// the organization row to insert (design.md "Interfaces / Contracts").
/// </summary>
public sealed record NewOrganization(Guid Id, string Name);

/// <summary>
/// Input to <see cref="PostgresOrganizationStore.TryCreateBootstrapAsync"/> —
/// the one default branch created alongside the organization at bootstrap.
/// </summary>
public sealed record NewBranch(Guid Id, string Name);

/// <summary>
/// Result of a bootstrap-creation attempt (design.md "Interfaces /
/// Contracts"). Every non-<see cref="Created"/> value means the transaction
/// rolled back with zero rows written.
/// </summary>
public enum BootstrapOutcome
{
    Created,
    OrganizationAlreadyExists,
    OrganizationAlreadyHasUsers,
    EmailAlreadyRegistered,
}

/// <summary>
/// One selectable branch option surfaced by `POST /device/pair` when an
/// operator's branch scope has more than one branch (design.md "Pairing
/// flow").
/// </summary>
public sealed record BranchOption(Guid Id, string Name, int Code);

/// <summary>
/// One organization's web branding (T5, organization-persistence spec
/// "Organization Branding Fields"). Either field is <c>null</c> when unset —
/// there is no separate "has branding" flag, an unset field IS the value.
/// </summary>
public sealed record OrganizationBranding(string? LogoUrl, string? PrimaryColor);

/// <summary>
/// One organization's settings (purchases-receptions-and-stock T7). The first field is the decimal separator the
/// business types quantities with: <c>Comma</c> ("1,5", the default) or <c>Dot</c> ("1.5"). The second is the price list a
/// customer without one of its own is priced from, and that a new customer starts on (customer-price-lists; <c>null</c> = none).
/// The third is the organization's country (`countries.code`, admin-console-field-fixes; Argentina by default): the
/// customer form lists its provinces.
/// </summary>
public sealed record OrganizationSettings(string QuantityDecimalSeparator, Guid? DefaultCustomerPriceListId = null, string CountryCode = OrganizationSettings.DefaultCountryCode)
{
    public const string Comma = "Comma";
    public const string Dot = "Dot";
    public const string DefaultCountryCode = "AR";

    public static bool IsValidQuantityDecimalSeparator(string? value) => value is Comma or Dot;

    /// <summary>A country code is two letters (ISO 3166-1 alpha-2); trimmed and upper-cased. Whether it is a loaded country is the foreign key's call.</summary>
    public static bool TryNormalizeCountryCode(string? raw, out string? normalized)
    {
        normalized = null;
        var candidate = raw?.Trim().ToUpperInvariant();
        if (candidate is not { Length: 2 } || !candidate.All(char.IsAsciiLetterUpper))
        {
            return false;
        }

        normalized = candidate;
        return true;
    }
}
