namespace Commerce.Cloud.Api.Auditing;

/// <summary>The `audit_log.actor_kind` values.</summary>
public static class AuditActorKinds
{
    /// <summary>A signed-in organization user (also used for sales a terminal syncs on behalf of its operator).</summary>
    public const string OrgUser = "org-user";

    /// <summary>A paired POS terminal acting on its own credential, with no signed-in user.</summary>
    public const string Device = "device";
}
