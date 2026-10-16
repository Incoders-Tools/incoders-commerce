using Commerce.Domain.Identity;

namespace Commerce.Application.Access;

/// <summary>
/// Describes an authorizable action: whether it must be audited, which
/// permission it requires, and whether it requires the offline admin-snapshot
/// freshness check when performed while offline.
/// </summary>
public sealed record ActionDefinition(
    string Name,
    bool IsSensitive,
    Permission RequiredPermission = Permission.None,
    bool RequiresElevatedOfflinePermission = false);
