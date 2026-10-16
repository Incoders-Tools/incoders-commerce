namespace Commerce.Cloud.Api.Auditing;

/// <summary>
/// The persisted shape of one `audit_log` row (commerce-role-taxonomy
/// proposal.md "Audit logging" / design.md "Audit table shape and RLS").
/// <see cref="OrganizationId"/> is <c>null</c> only for platform sign-in and
/// genesis, which have no owning organization — every org-scoped action
/// always carries one.
/// </summary>
public sealed record UserManagementAuditEntry(
    string ActorKind,
    Guid ActorId,
    Guid? OrganizationId,
    string EntityType,
    Guid EntityId,
    string Action,
    string? OldValueJson,
    string? NewValueJson);
