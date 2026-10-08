namespace Commerce.Domain.Audit;

/// <summary>Who performed an audited action.</summary>
public enum AuditActorKind
{
    /// <summary>A signed-in organization user (staff).</summary>
    OrgUser,

    /// <summary>A paired POS terminal acting on its own credential.</summary>
    Device,

    /// <summary>A customer acting with an ordering-access credential.</summary>
    Customer,
}
