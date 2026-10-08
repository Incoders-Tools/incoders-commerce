namespace Commerce.Domain.Tenancy;

public sealed class Organization
{
    public Guid Id { get; }
    public string Name { get; }

    /// <summary>
    /// Optional web branding (T5, "lo mas simple posible, a futuro
    /// ampliamos"): an absolute http/https URL, validated at the API
    /// boundary — never here, since nothing constructs this entity from
    /// caller input today (persistence goes through raw SQL rows and DTOs,
    /// see `PostgresOrganizationStore`). Date format, geolocation and usage
    /// plan are explicitly deferred and NOT modeled.
    /// </summary>
    public string? LogoUrl { get; }

    /// <summary>Optional `#rrggbb` hex color, same validation note as <see cref="LogoUrl"/>.</summary>
    public string? PrimaryColor { get; }

    public Organization(Guid id, string name, string? logoUrl = null, string? primaryColor = null)
    {
        Id = id;
        Name = name;
        LogoUrl = logoUrl;
        PrimaryColor = primaryColor;
    }
}
