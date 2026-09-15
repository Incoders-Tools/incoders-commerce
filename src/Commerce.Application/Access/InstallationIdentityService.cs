using Commerce.Domain.Tenancy;

namespace Commerce.Application.Access;

/// <summary>
/// Mints distinct branch installation identities. Application upgrades reuse
/// the same identity; only hardware/notebook replacement mints a new one,
/// while the prior identity remains revocable and traceable (ADR-002).
/// </summary>
public sealed class InstallationIdentityService
{
    private readonly Dictionary<Guid, Installation> _installations = new();

    public Installation Register(Guid branchId)
    {
        var installation = new Installation(Guid.NewGuid(), branchId);
        _installations[installation.Id] = installation;
        return installation;
    }

    public Installation ReplaceForHardwareChange(Installation priorInstallation)
    {
        priorInstallation.Revoke();

        var replacement = new Installation(
            Guid.NewGuid(),
            priorInstallation.BranchId,
            priorInstallation.Id);

        _installations[replacement.Id] = replacement;
        return replacement;
    }

    public Installation? Find(Guid installationId) =>
        _installations.TryGetValue(installationId, out var installation) ? installation : null;
}
