using Commerce.Domain.Catalog;

namespace Commerce.Application.Management;

public enum ManagementOutcomeStatus
{
    Allowed,
    Denied
}

/// <summary>
/// Identical outcome shape returned by the shared management contract
/// regardless of which channel (local or web) invoked it — the parity
/// guarantee this unit exists to prove.
/// </summary>
public sealed record ManagementOutcome(
    ManagementOutcomeStatus Status,
    string Reason,
    Product? UpdatedProduct);
