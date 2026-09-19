namespace Commerce.Domain.Ordering;

/// <summary>
/// Bound, revocable ordering credential for exactly one customer in exactly
/// one organization (ADR-002 identity model; spec "Bound and Revocable
/// Customer Access"). The <see cref="Credential"/> is the unpredictable
/// access token; org/customer binding and enablement are checked on every
/// catalogue/order request, never assumed from a prior check.
/// </summary>
public sealed class CustomerOrderingAccess
{
    public Guid OrganizationId { get; }
    public Guid CustomerId { get; }
    public Guid Credential { get; }
    public bool IsEnabled { get; private set; }

    public CustomerOrderingAccess(Guid organizationId, Guid customerId, Guid credential, bool isEnabled = true)
    {
        OrganizationId = organizationId;
        CustomerId = customerId;
        Credential = credential;
        IsEnabled = isEnabled;
    }

    public void Enable() => IsEnabled = true;

    public void Revoke() => IsEnabled = false;
}
