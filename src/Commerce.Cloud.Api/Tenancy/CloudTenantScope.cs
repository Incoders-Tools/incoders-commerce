namespace Commerce.Cloud.Api.Tenancy;

/// <summary>
/// Tenant scope derived from authenticated cloud credentials (ADR-002).
/// Never constructed from a caller-submitted organization ID — the caller
/// (e.g. the authentication middleware) is the only source of this value.
/// Mirrors the PostgreSQL non-owner runtime role's claim-derived filter.
/// </summary>
public sealed record CloudTenantScope(Guid OrganizationId);
