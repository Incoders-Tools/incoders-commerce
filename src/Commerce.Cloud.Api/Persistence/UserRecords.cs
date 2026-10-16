namespace Commerce.Cloud.Api.Persistence;

/// <summary>
/// Credential-free directory projection (design.md "Org resolution at
/// sign-in"). Returned by the ONLY unscoped read method on
/// <see cref="PostgresUserAccountStore"/> — resolves an email to the
/// organization it belongs to, before any tenant scope exists.
/// </summary>
public sealed record UserDirectoryEntry(string EmailNormalized, Guid OrganizationId, Guid UserId);

/// <summary>
/// Org-scoped credential row used to verify a sign-in password. Carries
/// `SessionVersion` (commerce-password-recovery design.md "Session
/// invalidation") so the sign-in claim set can stamp the current value.
/// </summary>
public sealed record UserCredentialRecord(
    Guid Id, Guid OrganizationId, string Email, string PasswordHash, bool IsRevoked, int SessionVersion = 0);

/// <summary>
/// Input to <see cref="PostgresUserAccountStore.TryCreateAsync"/> — a new
/// user to insert into both `users` and `user_directory` in one transaction.
/// Never carries the platform system-administrator flag: that is set only
/// AFTER insert, via <see cref="PostgresUserAccountStore.PromoteToSystemAdminAsync"/>
/// (B1, frontend-modernization) — every row this record creates keeps the
/// column's own `DEFAULT false` at insert time.
/// </summary>
public sealed record NewUserAccount(
    Guid Id,
    string Email,
    string PasswordHash,
    IReadOnlyList<Guid> BranchScope,
    IReadOnlyList<Endpoints.RoleDto> Roles,
    Guid? CustomerId = null);
