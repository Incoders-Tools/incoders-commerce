namespace Commerce.BranchNode;

/// <summary>
/// Tender storage of the branch database (pos-scan-sale "Tender Recorded and
/// Synchronized With the Sale"): three nullable columns on <c>sale_effects</c>.
/// Additive and idempotent like the discount columns, so a <c>branch.db</c> from
/// before tenders opens, upgrades and keeps reading its old sales (no tender).
/// </summary>
public sealed partial class BranchSyncStore
{
    private void EnsureTenderStorageExists() =>
        EnsureColumns("sale_effects", "tender_method", "tender_amount_received", "tender_change");
}
