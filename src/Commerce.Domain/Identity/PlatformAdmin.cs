namespace Commerce.Domain.Identity;

/// <summary>
/// A cross-organization operator identity (commerce-role-taxonomy
/// proposal.md "Platform Admin" / design.md "Platform-admin write path").
/// Deliberately carries NO <c>OrganizationId</c> and no org association at
/// all — this is what makes it structurally impossible to confuse with a
/// <see cref="UserAccount"/> or to accidentally scope by tenant. Its
/// capability lives entirely in the <c>PlatformAdminCookie</c> scheme and
/// the explicit, per-call target organization id it supplies — never in a
/// <see cref="Permission"/> flag.
/// </summary>
public sealed class PlatformAdmin
{
    public Guid Id { get; }
    public string Email { get; }

    public PlatformAdmin(Guid id, string email)
    {
        Id = id;
        Email = email;
    }
}
