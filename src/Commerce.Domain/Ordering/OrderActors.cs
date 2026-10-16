namespace Commerce.Domain.Ordering;

/// <summary>
/// Non-staff actor sentinels used as <c>SyncEnvelope.ActorId</c> for orders
/// that carry no staff operator. <see cref="Guid.Empty"/> is deliberately
/// avoided: it reads as "unknown/missing staff user" everywhere else in this
/// repo (commerce-guest-ordering spec, "Non-Staff Actor Representation").
/// </summary>
public static class OrderActors
{
    /// <summary>
    /// A named, non-empty sentinel identifying an order submitted by a
    /// member of the public with no staff operator present.
    /// </summary>
    public static readonly Guid PublicGuest = new("00000000-0000-0000-0000-000000000001");
}
