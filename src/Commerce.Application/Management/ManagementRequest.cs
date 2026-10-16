namespace Commerce.Application.Management;

/// <summary>
/// Single shared request shape for a catalog management write, submitted by
/// either the local (branch/Windows) or web (cloud) channel. Target scope is
/// validated by <see cref="Commerce.Application.Access.TenantAuthorizationService"/>
/// against the actor's own grant, never trusted as-is (ADR-002 / design.md
/// "Management authority").
/// </summary>
public sealed record ManagementRequest(
    Guid TargetOrganizationId,
    Guid TargetBranchId,
    Guid ProductId,
    string NewName,
    bool IsOffline,
    Guid CorrelationId);
