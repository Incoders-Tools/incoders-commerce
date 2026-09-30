using Commerce.BranchNode;
using Commerce.Domain.CashSessions;

namespace Commerce.Integration;

/// <summary>
/// Sales require an open cash session (pos-cash-session), so tests that commit
/// sales through <see cref="BranchNodeService"/> open one explicitly with this
/// helper instead of weakening the rule.
/// </summary>
internal static class CashSessionTestSupport
{
    /// <summary>Opens a session for the given identities and returns its id (the open one when a session already is).</summary>
    public static Guid OpenSession(
        BranchNodeService service, Guid organizationId, Guid branchId, Guid operatorId, decimal openingFloat = 0m)
    {
        var result = service.OpenCashSession(organizationId, branchId, operatorId, openingFloat, Guid.NewGuid());
        return result.Session!.SessionId;
    }

    /// <summary>For tests whose sales use their own identities: opens a session (float 0) unless one is open, and returns the service.</summary>
    public static BranchNodeService WithOpenSession(this BranchNodeService service)
    {
        OpenSession(service, Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid());
        return service;
    }
}
