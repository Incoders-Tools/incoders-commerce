using System.IO;
using System.Text.Json;
using Commerce.Application.Access;
using Commerce.Domain.Tenancy;

namespace Commerce.Pos.Windows;

/// <summary>
/// Persists this machine's branch/organization/installation identity across
/// process restarts (design.md "Device auth": application upgrades reuse the
/// same installation identity — <see cref="InstallationIdentityService"/>
/// only mints a fresh one, it never persists across processes on its own).
/// This is deliberately a flat local JSON file, matching the walking-skeleton
/// scope of this unit; no distribution/rotation tooling is added here.
/// </summary>
public sealed class LocalInstallationStore
{
    private readonly string _filePath;

    public LocalInstallationStore(string filePath)
    {
        _filePath = filePath;
    }

    public LocalInstallationRecord LoadOrCreate(InstallationIdentityService identityService)
    {
        if (File.Exists(_filePath))
        {
            var json = File.ReadAllText(_filePath);
            var existing = JsonSerializer.Deserialize<LocalInstallationRecord>(json);
            if (existing is not null)
            {
                return existing;
            }
        }

        var branchId = Guid.NewGuid();
        var organizationId = Guid.NewGuid();
        var installation = identityService.Register(branchId);

        var record = new LocalInstallationRecord(organizationId, branchId, installation.Id);
        File.WriteAllText(_filePath, JsonSerializer.Serialize(record));
        return record;
    }
}

public sealed record LocalInstallationRecord(Guid OrganizationId, Guid BranchId, Guid InstallationId);
