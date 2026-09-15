namespace Commerce.Domain.Tenancy;

/// <summary>
/// A distinct branch installation identity. Application upgrades never mint a
/// new identity; only hardware/notebook replacement does (ADR-002).
/// </summary>
public sealed class Installation
{
    public Guid Id { get; }
    public Guid BranchId { get; }
    public Guid? ReplacesInstallationId { get; }
    public bool IsRevoked { get; private set; }

    public Installation(Guid id, Guid branchId, Guid? replacesInstallationId = null)
    {
        Id = id;
        BranchId = branchId;
        ReplacesInstallationId = replacesInstallationId;
    }

    public void Revoke() => IsRevoked = true;
}
