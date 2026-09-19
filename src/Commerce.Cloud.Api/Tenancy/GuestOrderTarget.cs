using Microsoft.Extensions.Configuration;

namespace Commerce.Cloud.Api.Tenancy;

/// <summary>
/// The single org/branch resolution point for anonymous requests
/// (commerce-guest-ordering design.md "Org/branch resolution point";
/// tenant-access-foundation spec.md "Anonymous Request Organization
/// Resolution"). Built once from <c>GuestOrdering:OrganizationId</c> /
/// <c>GuestOrdering:BranchId</c> configuration (env <c>GuestOrdering__*</c>)
/// and registered as a singleton. Missing or unparseable configuration means
/// the public surface is never mapped at all — no guest surface rather than
/// one pointed at a guessed branch. A future multi-org resolver replaces
/// this single type; no call site changes shape.
/// </summary>
public sealed record GuestOrderTarget(Guid OrganizationId, Guid DestinationBranchId)
{
    public CloudTenantScope Scope => new(OrganizationId);

    public static bool TryFromConfiguration(IConfiguration config, out GuestOrderTarget? target)
    {
        target = null;

        var organizationIdRaw = config["GuestOrdering:OrganizationId"];
        var branchIdRaw = config["GuestOrdering:BranchId"];

        if (string.IsNullOrWhiteSpace(organizationIdRaw) || string.IsNullOrWhiteSpace(branchIdRaw))
        {
            return false;
        }

        if (!Guid.TryParse(organizationIdRaw, out var organizationId) || organizationId == Guid.Empty)
        {
            return false;
        }

        if (!Guid.TryParse(branchIdRaw, out var destinationBranchId) || destinationBranchId == Guid.Empty)
        {
            return false;
        }

        target = new GuestOrderTarget(organizationId, destinationBranchId);
        return true;
    }
}
