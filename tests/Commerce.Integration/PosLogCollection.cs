namespace Commerce.Integration;

/// <summary>
/// `PosLog` is a process-wide static. Every test class that configures it, or
/// whose POS clients log while it may be configured, joins this collection and
/// runs alone: a test that points the logger at a temp directory and deletes it
/// must never race another test that is logging.
/// </summary>
[CollectionDefinition("PosLog", DisableParallelization = true)]
public sealed class PosLogCollection;
