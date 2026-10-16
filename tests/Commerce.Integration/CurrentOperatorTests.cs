using Commerce.Pos.Windows;

namespace Commerce.Integration;

/// <summary>
/// Covers commerce-pos-user-login task 1.8 (design.md "Cancel does not shut
/// down; `actorId` is total"): `ResolveActorId` is a total function with no
/// branch at the call site — it returns the fallback when unset and after
/// `Clear()`, and the operator's user id when set.
/// </summary>
public sealed class CurrentOperatorTests
{
    private static CachedOperator MakeOperator() => new(
        Guid.NewGuid(), "operator@example.com", Guid.NewGuid(),
        OperatorPinCredential.Derive("482913").Salt,
        OperatorPinCredential.Derive("482913").Subkey,
        DateTimeOffset.UtcNow);

    [Fact]
    public void ResolveActorId_NoOperatorSet_ReturnsFallback()
    {
        var current = new CurrentOperator();
        var fallback = Guid.NewGuid();

        Assert.Equal(fallback, current.ResolveActorId(fallback));
    }

    [Fact]
    public void ResolveActorId_AfterSet_ReturnsOperatorUserId()
    {
        var current = new CurrentOperator();
        var op = MakeOperator();
        current.Set(op);

        Assert.Equal(op.UserId, current.ResolveActorId(Guid.NewGuid()));
    }

    [Fact]
    public void ResolveActorId_AfterClear_ReturnsFallbackAgain()
    {
        var current = new CurrentOperator();
        var op = MakeOperator();
        current.Set(op);
        current.Clear();
        var fallback = Guid.NewGuid();

        Assert.Equal(fallback, current.ResolveActorId(fallback));
    }
}
