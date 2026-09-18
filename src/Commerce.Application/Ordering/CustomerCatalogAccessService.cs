using Commerce.Application.Audit;
using Commerce.Domain.Catalog;
using Commerce.Domain.Ordering;

namespace Commerce.Application.Ordering;

/// <summary>
/// <see cref="CustomerId"/> is populated whenever a row was resolved (both
/// allow and the "credential-revoked" deny), so a caller composing a bigger
/// decision (e.g. <see cref="Commerce.Cloud.Api.Ordering.CloudOrderSubmissionService"/>'s
/// credential-binding check) can compare it against a request-declared
/// customer id without a second resolve/audit round trip. It is deliberately
/// null on "not-found" — nothing is known about a nonexistent/cross-org row
/// worth exposing.
/// </summary>
public sealed record CustomerAccessResult(bool Allowed, string Reason, Guid? CustomerId = null);

public sealed record CatalogueAccessResult(bool Allowed, string Reason, IReadOnlyList<Product> Products);

/// <summary>
/// Authorizes a customer's own bound ordering credential for catalogue/order
/// access (spec "Bound and Revocable Customer Access"). Distinct from
/// <see cref="Commerce.Application.Access.TenantAuthorizationService"/>, which
/// authorizes staff/branch actors with roles and branch scope — a customer
/// credential has neither and must never be modeled as one.
///
/// commerce-customer-identity design.md "The security fix, made
/// unrepresentable rather than validated-away": this service's public surface
/// does NOT accept a <see cref="CustomerOrderingAccess"/> built from request
/// data. It takes only an organization id, a credential, and a correlation
/// id, and resolves the access record itself through the injected
/// <see cref="ICustomerOrderingAccessResolver"/> — the ONLY source of a
/// <see cref="CustomerOrderingAccess"/> instance this service will ever act
/// on. A caller cannot rebuild one from a DTO and pass it in: there is no
/// overload that accepts one. Every decision (allow or deny) is audited via
/// the shared <see cref="IAuditSink"/> (Component Reuse Policy) so denial is
/// always auditable.
/// </summary>
public sealed class CustomerCatalogAccessService
{
    private readonly ICustomerOrderingAccessResolver _resolver;
    private readonly IAuditSink _auditSink;
    private readonly Func<DateTimeOffset> _clock;

    public CustomerCatalogAccessService(ICustomerOrderingAccessResolver resolver, IAuditSink auditSink, Func<DateTimeOffset>? clock = null)
    {
        _resolver = resolver;
        _auditSink = auditSink;
        _clock = clock ?? (() => DateTimeOffset.UtcNow);
    }

    public async Task<CustomerAccessResult> AuthorizeAsync(
        Guid requestedOrganizationId, Guid credential, Guid correlationId, CancellationToken ct)
    {
        var access = await _resolver.ResolveAsync(requestedOrganizationId, credential, ct);
        var result = Evaluate(access, requestedOrganizationId);

        _auditSink.Record(new Domain.Audit.AuditEntry(
            ActorId: result.CustomerId ?? Guid.Empty,
            OrganizationId: requestedOrganizationId,
            BranchId: Guid.Empty,
            Action: "customer-ordering-access",
            Outcome: result.Allowed ? "allowed" : "denied",
            OccurredAtUtc: _clock(),
            CorrelationId: correlationId,
            Reason: result.Reason));

        return result;
    }

    public async Task<CatalogueAccessResult> GetPermittedCatalogueAsync(
        Guid requestedOrganizationId,
        Guid credential,
        IReadOnlyList<Product> organizationCatalogue,
        Guid correlationId,
        CancellationToken ct)
    {
        var result = await AuthorizeAsync(requestedOrganizationId, credential, correlationId, ct);
        return result.Allowed
            ? new CatalogueAccessResult(true, result.Reason, organizationCatalogue)
            : new CatalogueAccessResult(false, result.Reason, Array.Empty<Product>());
    }

    /// <summary>
    /// Deny-reason collision is DELIBERATE (design.md): an unknown credential
    /// and a credential bound to another organization return the identical
    /// "not-found" so a probe cannot distinguish "doesn't exist" from "exists
    /// in another org". The organization comparison happens here, against the
    /// resolved row's OWN <see cref="CustomerOrderingAccess.OrganizationId"/> —
    /// the resolver itself never filters by it (asymmetric RLS, the
    /// `device_credentials` precedent).
    /// </summary>
    private static CustomerAccessResult Evaluate(CustomerOrderingAccess? access, Guid requestedOrganizationId)
    {
        if (access is null)
        {
            return new CustomerAccessResult(false, "not-found");
        }

        if (access.OrganizationId != requestedOrganizationId)
        {
            return new CustomerAccessResult(false, "not-found");
        }

        if (!access.IsEnabled)
        {
            return new CustomerAccessResult(false, "credential-revoked", access.CustomerId);
        }

        return new CustomerAccessResult(true, "allowed", access.CustomerId);
    }
}
