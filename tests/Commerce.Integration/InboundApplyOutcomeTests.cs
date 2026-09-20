using Commerce.Domain.Sync;

namespace Commerce.Integration;

/// <summary>
/// Covers commerce-sync-ownership design.md Unit 1: <see cref="InboundApplyOutcome.UnknownKind"/>
/// exists as a member distinct from <c>Applied</c>/<c>DuplicateIgnored</c>/<c>Denied</c>.
/// </summary>
public sealed class InboundApplyOutcomeTests
{
    [Fact]
    public void UnknownKind_IsDistinctFromEveryOtherMember()
    {
        var members = Enum.GetValues<InboundApplyOutcome>();

        Assert.Contains(InboundApplyOutcome.UnknownKind, members);
        Assert.NotEqual(InboundApplyOutcome.Applied, InboundApplyOutcome.UnknownKind);
        Assert.NotEqual(InboundApplyOutcome.DuplicateIgnored, InboundApplyOutcome.UnknownKind);
        Assert.NotEqual(InboundApplyOutcome.Denied, InboundApplyOutcome.UnknownKind);
    }
}
