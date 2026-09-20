namespace Commerce.Pos.Windows;

/// <summary>
/// Every caller of <see cref="SyncRunner.RunAsync"/> (commerce-sync-ownership
/// design.md "Retry: where and how"): one body, four callers. Only
/// <see cref="Button"/> updates the POS UI — the other three stay invisible
/// to the local operator per the answered product question (d).
/// </summary>
public enum SyncTrigger
{
    Startup,
    Timer,
    PostSale,
    Button
}
