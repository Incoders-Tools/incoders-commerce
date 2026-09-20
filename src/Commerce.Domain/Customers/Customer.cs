using System.Text.RegularExpressions;

namespace Commerce.Domain.Customers;

/// <summary>
/// The commercial-party aggregate (ADR-008; customer-registry spec
/// "Organization-Scoped Customer Persistence"). Organization-bound, usable
/// with zero linked logins, independent of any <c>UserAccount</c>.
/// Deliberately carries NO <c>UserId</c> field (ADR-008) — the optional link
/// runs the other direction, via <c>UserAccount.CustomerId</c>.
/// </summary>
public sealed class Customer
{
    public Guid Id { get; }
    public Guid OrganizationId { get; }
    public CustomerKind CustomerKind { get; }
    public string DisplayName { get; }
    public string? LegalName { get; }
    public TaxIdType TaxIdType { get; }
    public string? TaxId { get; }
    public TaxCondition TaxCondition { get; }
    public string? Phone { get; }
    public string? Email { get; }
    public string? AddressStreet { get; }
    public string? AddressNumber { get; }
    public string? Neighborhood { get; }
    public string? Locality { get; }
    public string? Province { get; }
    public string? PostalCode { get; }
    public string? DeliveryNotes { get; }
    public decimal? DiscountPercentage { get; }
    public string? PaymentTerms { get; }

    /// <summary>
    /// Opaque provider token/alias (commerce-payments design.md "Customer
    /// instrument reference", Decision 4) — a NEW, separately-named field.
    /// <see cref="PaymentTerms"/> is free-text commercial terms and is
    /// NEVER touched by this field: not its type, not its position, not its
    /// guard. Rejects any 13-19 digit (PAN-shaped) value; the platform
    /// stores a reference, never a card number.
    /// </summary>
    public string? BillingInstrumentReference { get; }
    public string? Notes { get; }
    public bool IsEnabled { get; private set; }
    public DateTimeOffset CreatedAtUtc { get; }
    public Guid CreatedByUserId { get; }

    public Customer(
        Guid id,
        Guid organizationId,
        CustomerKind customerKind,
        string displayName,
        string? legalName,
        TaxIdType taxIdType,
        string? taxId,
        TaxCondition taxCondition,
        string? phone,
        string? email,
        string? addressStreet,
        string? addressNumber,
        string? neighborhood,
        string? locality,
        string? province,
        string? postalCode,
        string? deliveryNotes,
        decimal? discountPercentage,
        string? paymentTerms,
        string? notes,
        Guid createdByUserId,
        bool isEnabled = true,
        DateTimeOffset? createdAtUtc = null,
        string? billingInstrumentReference = null)
    {
        if (string.IsNullOrWhiteSpace(displayName))
        {
            throw new ArgumentException("DisplayName must not be blank.", nameof(displayName));
        }

        // tax_id invariant, mirrored by the database CHECK
        // customers_tax_id_requires_type (0008): TaxIdType.None carries no
        // TaxId, and any other TaxIdType requires one.
        var hasTaxId = !string.IsNullOrWhiteSpace(taxId);
        if (taxIdType == TaxIdType.None && hasTaxId)
        {
            throw new ArgumentException("TaxId must be null when TaxIdType is None.", nameof(taxId));
        }

        if (taxIdType != TaxIdType.None && !hasTaxId)
        {
            throw new ArgumentException("TaxId is required when TaxIdType is not None.", nameof(taxId));
        }

        // commerce-payments design.md "Customer instrument reference": reject
        // any 13-19 digit (PAN-shaped) value, mirroring the DB CHECK
        // customers_instrument_not_pan_shaped.
        if (billingInstrumentReference is not null && PanShapePattern.IsMatch(billingInstrumentReference))
        {
            throw new ArgumentException(
                "BillingInstrumentReference must not be a PAN-shaped (13-19 digit) value.",
                nameof(billingInstrumentReference));
        }

        Id = id;
        OrganizationId = organizationId;
        CustomerKind = customerKind;
        DisplayName = displayName;
        LegalName = legalName;
        TaxIdType = taxIdType;
        TaxId = taxId;
        TaxCondition = taxCondition;
        Phone = phone;
        Email = email;
        AddressStreet = addressStreet;
        AddressNumber = addressNumber;
        Neighborhood = neighborhood;
        Locality = locality;
        Province = province;
        PostalCode = postalCode;
        DeliveryNotes = deliveryNotes;
        DiscountPercentage = discountPercentage;
        PaymentTerms = paymentTerms;
        BillingInstrumentReference = billingInstrumentReference;
        Notes = notes;
        CreatedByUserId = createdByUserId;
        IsEnabled = isEnabled;
        CreatedAtUtc = createdAtUtc ?? DateTimeOffset.UtcNow;
    }

    public void Enable() => IsEnabled = true;

    public void Disable() => IsEnabled = false;

    private static readonly Regex PanShapePattern = new("^[0-9]{13,19}$", RegexOptions.Compiled);
}
