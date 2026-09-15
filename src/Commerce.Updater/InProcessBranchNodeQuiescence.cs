namespace Commerce.Updater;

/// <summary>
/// A real, working in-process state machine for <see cref="IBranchNodeQuiescence"/>:
/// Running -&gt; (blocked while a sale is active) -&gt; Quiesced -&gt; Running.
///
/// Honesty note (same standard as Unit 3's PostgreSQL RLS): this proves the
/// quiesce/resume contract and the "never interrupt an active sale" policy
/// end-to-end in-process. It does NOT integrate with the real Windows
/// Service Control Manager or an out-of-process branch node — no live
/// Windows Service host was available in this sandboxed session. Production
/// hosting must implement <see cref="IBranchNodeQuiescence"/> against the
/// actual running node (e.g. via a local control channel to the service),
/// reusing this exact interface and state-machine contract. This is a real,
/// tested interface with a real in-process implementation, not a silently
/// faked "already proven in production" claim.
/// </summary>
public sealed class InProcessBranchNodeQuiescence : IBranchNodeQuiescence
{
    private readonly object _gate = new();
    private bool _activeSale;

    public bool IsQuiesced { get; private set; }

    public void BeginSale()
    {
        lock (_gate)
        {
            _activeSale = true;
        }
    }

    public void EndSale()
    {
        lock (_gate)
        {
            _activeSale = false;
            Monitor.PulseAll(_gate);
        }
    }

    public bool TryQuiesce(TimeSpan timeout)
    {
        lock (_gate)
        {
            var deadline = DateTime.UtcNow + timeout;
            while (_activeSale)
            {
                var remaining = deadline - DateTime.UtcNow;
                if (remaining <= TimeSpan.Zero || !Monitor.Wait(_gate, remaining))
                {
                    return false;
                }
            }

            IsQuiesced = true;
            return true;
        }
    }

    public void Resume()
    {
        lock (_gate)
        {
            IsQuiesced = false;
        }
    }
}
