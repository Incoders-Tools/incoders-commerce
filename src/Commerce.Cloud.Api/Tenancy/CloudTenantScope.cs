namespace Commerce.Cloud.Api.Tenancy;

/// <summary>
/// Tenant scope derived from authenticated cloud credentials (ADR-002).
/// Never constructed from a caller-submitted organization ID — the caller
/// (e.g. the authentication middleware) is the only source of
/// <see cref="OrganizationId"/>. Mirrors the PostgreSQL non-owner runtime
/// role's claim-derived filter.
///
/// <see cref="IdentityOrganizationId"/> is set ONLY by
/// <see cref="TenantScopeEndpointFilter"/> when a verified system
/// administrator has selected a DIFFERENT target organization (platform-
/// administration spec "Sysadmin Acts On A Selected Organization") — it
/// carries the caller's OWN organization so
/// <see cref="Persistence.PostgresUserAccountStore.LoadActorAsync"/> can
/// still find the caller's own row (which lives in their own org, not the
/// target) while every business-data query keeps using
/// <see cref="OrganizationId"/> (the target). It is never set from a
/// caller-submitted value for anyone else.
///
/// <see cref="BranchId"/> is set ONLY by <see cref="TenantScopeEndpointFilter"/>
/// (B7 U1, tenant-access-foundation spec "Selected Branch Scopes Every
/// Branch-Owned Staff Request"), AFTER the organization above is resolved,
/// from the validated <c>X-Branch-Id</c> header (browser/staff callers) or
/// the device credential's own branch claim (device callers, which ignore
/// any header). It is <c>null</c> when no branch is selected — no
/// branch-owned endpoint requires one yet (that starts with each module's
/// own U4+ migration); handlers never accept a branch id from anywhere else.
/// </summary>
public sealed record CloudTenantScope(Guid OrganizationId, Guid? IdentityOrganizationId = null, Guid? BranchId = null)
{
    /// <summary>
    /// True when this scope targets an organization other than the caller's
    /// own — i.e. a system administrator acting on a selected organization.
    /// </summary>
    public bool IsActingOnSelectedOrganization =>
        IdentityOrganizationId is { } identityOrganizationId && identityOrganizationId != OrganizationId;

    /// <summary>
    /// The scope to use ONLY when loading the CALLER's own actor row for a
    /// permission check — always the caller's real organization, never the
    /// organization being acted on.
    /// </summary>
    public CloudTenantScope IdentityScope =>
        IsActingOnSelectedOrganization ? new CloudTenantScope(IdentityOrganizationId!.Value) : this;
}
