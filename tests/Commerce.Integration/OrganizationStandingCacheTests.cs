using Commerce.Cloud.Api.Authentication;
using Commerce.Cloud.Api.Persistence;

namespace Commerce.Integration;

/// <summary>
/// <see cref="OrganizationStandingCache"/> without a database (organization-account-standing T4 review): the TTL, the
/// invalidation, and the race where a read that started before a write must not cache the value it loaded.
/// </summary>
public sealed class OrganizationStandingCacheTests
{
    private static readonly Guid Organization = Guid.NewGuid();
    private static readonly OrganizationAccountStandingInputs Active = new(null, 30, null);
    private static readonly OrganizationAccountStandingInputs Suspended = new(null, 30, DateTimeOffset.UnixEpoch);

    [Fact]
    public async Task WithinTheTtl_ServesTheCachedInputs_AndAfterIt_ReloadsThem()
    {
        var now = DateTimeOffset.UnixEpoch;
        var loads = 0;
        var cache = OrganizationStandingCache.ForLoader((_, _) => { loads++; return Task.FromResult<OrganizationAccountStandingInputs?>(Active); }, () => now);

        await cache.GetAsync(Organization, CancellationToken.None);
        now = now.AddSeconds(59);
        await cache.GetAsync(Organization, CancellationToken.None);
        Assert.Equal(1, loads);

        now = now.AddSeconds(2);
        await cache.GetAsync(Organization, CancellationToken.None);
        Assert.Equal(2, loads);
    }

    [Fact]
    public async Task Invalidate_MakesTheNextReadLoadAgain()
    {
        var current = Active;
        var cache = OrganizationStandingCache.ForLoader((_, _) => Task.FromResult<OrganizationAccountStandingInputs?>(current));
        await cache.GetAsync(Organization, CancellationToken.None);

        current = Suspended;
        cache.Invalidate(Organization);

        Assert.Equal(Suspended, await cache.GetAsync(Organization, CancellationToken.None));
    }

    [Fact]
    public async Task AReadThatStartedBeforeAWrite_DoesNotCacheTheValueItLoaded()
    {
        // The first load returns the pre-write value only after the write has committed and invalidated the cache.
        var staleLoad = new TaskCompletionSource<OrganizationAccountStandingInputs?>();
        var loads = 0;
        var cache = OrganizationStandingCache.ForLoader((_, _) =>
            ++loads == 1 ? staleLoad.Task : Task.FromResult<OrganizationAccountStandingInputs?>(Suspended));

        var inFlight = cache.GetAsync(Organization, CancellationToken.None);
        cache.Invalidate(Organization);
        staleLoad.SetResult(Active);
        Assert.Equal(Active, await inFlight);

        Assert.Equal(Suspended, await cache.GetAsync(Organization, CancellationToken.None));
    }
}
