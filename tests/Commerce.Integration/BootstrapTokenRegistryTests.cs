using Commerce.Cloud.Api.Authentication;

namespace Commerce.Integration;

/// <summary>
/// Covers commerce-user-credentials task 1.5: in-memory, per-organization,
/// one-time bootstrap token issue/consume (design.md "Bootstrap token
/// storage"). No live Postgres dependency — pure in-memory unit coverage
/// with an injected clock.
/// </summary>
public sealed class BootstrapTokenRegistryTests
{
    [Fact]
    public void Issue_ThenConsume_WithCorrectToken_Succeeds()
    {
        var now = DateTimeOffset.UtcNow;
        var registry = new BootstrapTokenRegistry(() => now);
        var organizationId = Guid.NewGuid();

        var token = registry.Issue(organizationId);
        var consumed = registry.TryConsume(organizationId, token);

        Assert.True(consumed);
    }

    [Fact]
    public void Consume_Twice_SecondAttemptFails_OneTimeUse()
    {
        var now = DateTimeOffset.UtcNow;
        var registry = new BootstrapTokenRegistry(() => now);
        var organizationId = Guid.NewGuid();
        var token = registry.Issue(organizationId);

        var first = registry.TryConsume(organizationId, token);
        var second = registry.TryConsume(organizationId, token);

        Assert.True(first);
        Assert.False(second);
    }

    [Fact]
    public void Consume_AfterExpiry_Fails()
    {
        var now = DateTimeOffset.UtcNow;
        var registry = new BootstrapTokenRegistry(() => now);
        var organizationId = Guid.NewGuid();
        var token = registry.Issue(organizationId);

        now = now.AddMinutes(16); // > 15-minute expiry

        var consumed = registry.TryConsume(organizationId, token);

        Assert.False(consumed);
    }

    [Fact]
    public void Consume_WrongToken_Fails()
    {
        var now = DateTimeOffset.UtcNow;
        var registry = new BootstrapTokenRegistry(() => now);
        var organizationId = Guid.NewGuid();
        registry.Issue(organizationId);

        var consumed = registry.TryConsume(organizationId, "not-the-real-token");

        Assert.False(consumed);
    }

    [Fact]
    public void Reissue_InvalidatesPriorToken()
    {
        var now = DateTimeOffset.UtcNow;
        var registry = new BootstrapTokenRegistry(() => now);
        var organizationId = Guid.NewGuid();
        var firstToken = registry.Issue(organizationId);
        var secondToken = registry.Issue(organizationId);

        var consumedWithFirst = registry.TryConsume(organizationId, firstToken);
        var consumedWithSecond = registry.TryConsume(organizationId, secondToken);

        Assert.False(consumedWithFirst);
        Assert.True(consumedWithSecond);
    }

    [Fact]
    public void Consume_WrongOrganization_Fails_TokenScopedToOneOrg()
    {
        var now = DateTimeOffset.UtcNow;
        var registry = new BootstrapTokenRegistry(() => now);
        var organizationId = Guid.NewGuid();
        var otherOrganizationId = Guid.NewGuid();
        var token = registry.Issue(organizationId);

        var consumed = registry.TryConsume(otherOrganizationId, token);

        Assert.False(consumed);
    }
}
