using Commerce.Application.Audit;
using Commerce.Domain.Catalog;
using Commerce.Domain.Ordering;

namespace Commerce.Application.Ordering;

public sealed record CustomerAccessResult(bool Allowed, string Reason);

public sealed record CatalogueAccessResult(bool Allowed, string Reason, IReadOnlyList<Product> Products);

/// <summary>
/// Authorizes a customer's own bound ordering credential for catalogue/order
/// access (spec "Bound and Revocable Customer Access"). Distinct from
/// <see cref="Commerce.Application.Access.TenantAuthorizationService"/>, which
/// authorizes staff/branch actors with roles and branch scope — a customer
/// credential has neither and must never be modeled as one. Every decision
/// (allow or deny) is audited via the shared <see cref="IAuditSink"/>
/// (Component Reuse Policy) so denial is always auditable.
/// </summary>
public sealed class CustomerCatalogAccessService
{
    private readonly IAuditSink _auditSink;
    private readonly Func<DateTimeOffset> _clock;

    public CustomerCatalogAccessService(IAuditSink auditSink, Func<DateTimeOffset>? clock = null)
    {
        _auditSink = auditSink;
        _clock = clock ?? (() => DateTimeOffset.UtcNow);
    }

    public CustomerAccessResult Authorize(CustomerOrderingAccess access, Guid requestedOrganizationId, Guid correlationId)
    {
        var result = Evaluate(access, requestedOrganizationId);

        _auditSink.Record(new Domain.Audit.AuditEntry(
            ActorId: access.CustomerId,
            OrganizationId: requestedOrganizationId,
            BranchId: Guid.Empty,
            Action: "customer-ordering-access",
            Outcome: result.Allowed ? "allowed" : "denied",
            OccurredAtUtc: _clock(),
            CorrelationId: correlationId,
            Reason: result.Reason));

        return result;
    }

    public CatalogueAccessResult GetPermittedCatalogue(
        CustomerOrderingAccess access,
        Guid requestedOrganizationId,
        IReadOnlyList<Product> organizationCatalogue,
        Guid correlationId)
    {
        var result = Authorize(access, requestedOrganizationId, correlationId);
        return result.Allowed
            ? new CatalogueAccessResult(true, result.Reason, organizationCatalogue)
            : new CatalogueAccessResult(false, result.Reason, Array.Empty<Product>());
    }

    private static CustomerAccessResult Evaluate(CustomerOrderingAccess access, Guid requestedOrganizationId)
    {
        if (!access.IsEnabled)
        {
            return new CustomerAccessResult(false, "credential-revoked");
        }

        if (access.OrganizationId != requestedOrganizationId)
        {
            return new CustomerAccessResult(false, "not-found");
        }

        return new CustomerAccessResult(true, "allowed");
    }
}
