namespace Commerce.Pos.Windows;

/// <summary>
/// Singleton holding the currently logged-in operator on this terminal
/// (design.md "Cancel does not shut down; `actorId` is total").
/// <see cref="ResolveActorId"/> is a total function with no failure mode —
/// "never block a sale" is structural, enforced by this signature rather
/// than a guarded branch at the call site.
/// </summary>
public sealed class CurrentOperator
{
    public CachedOperator? Value { get; private set; }

    public void Set(CachedOperator op) => Value = op;

    public void Clear() => Value = null;

    public Guid ResolveActorId(Guid installationIdFallback) => Value?.UserId ?? installationIdFallback;
}
