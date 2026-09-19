using Commerce.Cloud.Api.Tenancy;
using Microsoft.Extensions.Configuration;

namespace Commerce.Integration;

/// <summary>
/// commerce-guest-ordering Phase 4 (Unit 4, task 4.2): pure config parsing
/// for the single org/branch resolution point (tenant-access-foundation
/// spec.md "Anonymous Request Organization Resolution"). No I/O.
/// </summary>
public sealed class GuestOrderTargetTests
{
    private static IConfiguration BuildConfig(string? organizationId, string? branchId)
    {
        var data = new Dictionary<string, string?>();
        if (organizationId is not null)
        {
            data["GuestOrdering:OrganizationId"] = organizationId;
        }
        if (branchId is not null)
        {
            data["GuestOrdering:BranchId"] = branchId;
        }
        return new ConfigurationBuilder().AddInMemoryCollection(data).Build();
    }

    [Fact]
    public void TryFromConfiguration_WithValidOrganizationAndBranch_Succeeds()
    {
        var organizationId = Guid.NewGuid();
        var branchId = Guid.NewGuid();
        var config = BuildConfig(organizationId.ToString(), branchId.ToString());

        var succeeded = GuestOrderTarget.TryFromConfiguration(config, out var target);

        Assert.True(succeeded);
        Assert.Equal(organizationId, target!.OrganizationId);
        Assert.Equal(branchId, target.DestinationBranchId);
    }

    [Fact]
    public void TryFromConfiguration_MissingOrganizationId_Fails()
    {
        var config = BuildConfig(null, Guid.NewGuid().ToString());

        var succeeded = GuestOrderTarget.TryFromConfiguration(config, out var target);

        Assert.False(succeeded);
        Assert.Null(target);
    }

    [Fact]
    public void TryFromConfiguration_MissingBranchId_Fails()
    {
        var config = BuildConfig(Guid.NewGuid().ToString(), null);

        var succeeded = GuestOrderTarget.TryFromConfiguration(config, out var target);

        Assert.False(succeeded);
        Assert.Null(target);
    }

    [Fact]
    public void TryFromConfiguration_UnparseableOrganizationId_Fails()
    {
        var config = BuildConfig("not-a-guid", Guid.NewGuid().ToString());

        var succeeded = GuestOrderTarget.TryFromConfiguration(config, out var target);

        Assert.False(succeeded);
        Assert.Null(target);
    }

    [Fact]
    public void TryFromConfiguration_EmptyGuidBranchId_Fails()
    {
        var config = BuildConfig(Guid.NewGuid().ToString(), Guid.Empty.ToString());

        var succeeded = GuestOrderTarget.TryFromConfiguration(config, out var target);

        Assert.False(succeeded);
        Assert.Null(target);
    }
}
