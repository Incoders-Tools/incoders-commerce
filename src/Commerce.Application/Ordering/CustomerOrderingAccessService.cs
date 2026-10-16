using Commerce.Application.Access;
using Commerce.Domain.Identity;
using Commerce.Domain.Ordering;

namespace Commerce.Application.Ordering;

public enum OrderingAccessOutcomeStatus
{
    Allowed,
    Denied
}

public sealed record OrderingAccessOutcome(OrderingAccessOutcomeStatus Status, string Reason, CustomerOrderingAccess? Access);

/// <summary>
/// Enabling/revoking a customer's private-ordering access is itself a
/// management-authorized staff action (design.md "Management authority"). It
/// reuses <see cref="TenantAuthorizationService"/> for the entire
/// authorization/audit decision — no ad hoc check of its own (Component Reuse
/// Policy), mirroring <see cref="Commerce.Application.Management.CatalogManagementService"/>.
/// </summary>
public sealed class CustomerOrderingAccessService
{
    private static readonly ActionDefinition SetOrderingAccess = new(
        Name: "set-customer-ordering-access",
        IsSensitive: true,
        RequiredPermission: Permission.ManageUsers);

    private readonly TenantAuthorizationService _authorizationService;

    public CustomerOrderingAccessService(TenantAuthorizationService authorizationService)
    {
        _authorizationService = authorizationService;
    }

    public OrderingAccessOutcome Enable(UserAccount actor, CustomerOrderingAccess access, Guid targetBranchId, bool isOffline, Guid correlationId) =>
        Apply(actor, access, targetBranchId, isOffline, correlationId, access.Enable);

    public OrderingAccessOutcome Revoke(UserAccount actor, CustomerOrderingAccess access, Guid targetBranchId, bool isOffline, Guid correlationId) =>
        Apply(actor, access, targetBranchId, isOffline, correlationId, access.Revoke);

    private OrderingAccessOutcome Apply(
        UserAccount actor,
        CustomerOrderingAccess access,
        Guid targetBranchId,
        bool isOffline,
        Guid correlationId,
        Action mutate)
    {
        var accessResult = _authorizationService.Authorize(
            actor,
            new AccessRequest(access.OrganizationId, targetBranchId, SetOrderingAccess, isOffline, correlationId));

        if (!accessResult.Allowed)
        {
            return new OrderingAccessOutcome(OrderingAccessOutcomeStatus.Denied, accessResult.Reason, Access: null);
        }

        mutate();
        return new OrderingAccessOutcome(OrderingAccessOutcomeStatus.Allowed, accessResult.Reason, access);
    }
}
