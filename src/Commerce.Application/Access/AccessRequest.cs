namespace Commerce.Application.Access;

/// <summary>
/// Target scope is compared against the actor's own authenticated
/// organization/branch grant — it is never trusted as a submitted tenant ID
/// (ADR-002 / design.md).
/// </summary>
public sealed record AccessRequest(
    Guid TargetOrganizationId,
    Guid TargetBranchId,
    ActionDefinition Action,
    bool IsOffline,
    Guid CorrelationId);
