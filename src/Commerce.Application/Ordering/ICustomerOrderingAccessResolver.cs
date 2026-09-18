using Commerce.Domain.Ordering;

namespace Commerce.Application.Ordering;

/// <summary>
/// Resolves a customer's bound ordering credential from the PERSISTED store
/// (commerce-customer-identity design.md "How the persisted access record is
/// read"). This is the ONLY way <see cref="CustomerCatalogAccessService"/> can
/// obtain a <see cref="CustomerOrderingAccess"/> instance: there is no
/// constructor path from request data into the service anymore, so a
/// body-built access object is not merely unvalidated, it does not compile.
/// The lookup is by credential hash alone (asymmetric RLS, the
/// `device_credentials` precedent) — <paramref name="organizationId"/> is not
/// used to filter the row; the caller (<see cref="CustomerCatalogAccessService"/>)
/// compares it against the resolved row's own <see cref="CustomerOrderingAccess.OrganizationId"/>
/// so a cross-organization credential denies with the identical reason as an
/// unknown one.
/// </summary>
public interface ICustomerOrderingAccessResolver
{
    Task<CustomerOrderingAccess?> ResolveAsync(Guid organizationId, Guid credential, CancellationToken ct);
}
