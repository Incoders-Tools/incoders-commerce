namespace Commerce.Application.Access;

public enum FreshnessLabel
{
    NotApplicable,
    Fresh,
    Stale
}

public sealed record AccessResult(bool Allowed, string Reason, FreshnessLabel Freshness);
