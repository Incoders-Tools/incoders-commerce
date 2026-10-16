namespace Commerce.BranchNode;

/// <summary>
/// Two entries, no discovery, no reflection (design.md "Materialization
/// contract" — rejected alternative: visitor/dispatch on the sealed
/// <c>SyncEnvelope</c> record). An unknown <c>payload_kind</c> resolves to no
/// handler, which <see cref="BranchSyncStore.ApplyInbound"/> maps to
/// <c>InboundApplyOutcome.UnknownKind</c> with a full rollback.
/// </summary>
public static class InboundEffectRegistry
{
    public static IReadOnlyDictionary<string, IInboundEffectHandler> Default { get; } =
        new Dictionary<string, IInboundEffectHandler>
        {
            ["sale"] = new Handlers.SaleInboundHandler(),
            ["order"] = new Handlers.OrderInboundHandler(),
        };
}
