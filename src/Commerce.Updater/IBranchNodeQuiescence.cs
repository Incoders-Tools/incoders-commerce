namespace Commerce.Updater;

/// <summary>
/// The contract the Updater depends on to safely stop and resume branch
/// writes around a migration (design.md: "blocks new sales/incoming sync,
/// waits for active work"). Production hosting (the Windows Service branch
/// node, see ADR-005) implements this against the real service control
/// manager / running node process; see <see cref="InProcessBranchNodeQuiescence"/>
/// for the honesty note on what is and is not proven in this session.
/// </summary>
public interface IBranchNodeQuiescence
{
    bool IsQuiesced { get; }

    /// <summary>
    /// Attempts to reach a safe, quiesced state within <paramref name="timeout"/>.
    /// Returns false — without mutating any state — if an active sale is
    /// still in progress when the timeout elapses (spec: "Active sale
    /// protection").
    /// </summary>
    bool TryQuiesce(TimeSpan timeout);

    void Resume();
}
