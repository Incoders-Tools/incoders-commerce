namespace Commerce.Domain.Ordering;

/// <summary>
/// The verified contact channel for a guest identity. Currently only
/// <see cref="Email"/> is supported; the channel is stored as a value so a
/// later channel (SMS/WhatsApp) widens this enum rather than reshaping the
/// schema (commerce-guest-ordering design.md, "Guest verification channel").
/// </summary>
public enum GuestContactChannel
{
    Email
}

/// <summary>
/// Identity captured directly on a guest <see cref="Order"/> because a guest
/// has no backing <c>Customer</c> row to read identity from
/// (commerce-guest-ordering spec, "Guest Identity Capture on the Order").
/// </summary>
/// <param name="DocumentId">The guest's DNI/identificación (free text; no format validation per design's Open Questions).</param>
/// <param name="Channel">The verified contact channel.</param>
/// <param name="ContactAddress">The verified contact address for that channel (e.g. an email address).</param>
/// <param name="DisplayName">The guest's display name.</param>
/// <param name="DeliveryNotes">Optional free-text delivery notes.</param>
public sealed record GuestContact(
    string DocumentId,
    GuestContactChannel Channel,
    string ContactAddress,
    string DisplayName,
    string? DeliveryNotes)
{
    public string DocumentId { get; } = string.IsNullOrWhiteSpace(DocumentId)
        ? throw new ArgumentException("DocumentId must not be blank.", nameof(DocumentId))
        : DocumentId;

    public string ContactAddress { get; } = string.IsNullOrWhiteSpace(ContactAddress)
        ? throw new ArgumentException("ContactAddress must not be blank.", nameof(ContactAddress))
        : ContactAddress;

    public string DisplayName { get; } = string.IsNullOrWhiteSpace(DisplayName)
        ? throw new ArgumentException("DisplayName must not be blank.", nameof(DisplayName))
        : DisplayName;
}
