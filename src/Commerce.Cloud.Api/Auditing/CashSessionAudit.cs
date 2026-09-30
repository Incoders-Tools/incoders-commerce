using System.Text.Json;
using Commerce.Domain.Sync;
using Commerce.Domain.Sync.Payloads;

namespace Commerce.Cloud.Api.Auditing;

/// <summary>
/// Builds the audit record of a cash session event ingested from a terminal
/// (pos-cash-session "Session Synchronized and Audited"): one record per opened
/// session and one per closed session, the latter carrying the expected cash,
/// counted cash and difference. Written in the same transaction as the
/// `sync_inbox` insert, which already deduplicates by operation id, so
/// redelivery cannot write a second one. A payload of another kind, or one that
/// cannot be read, yields no record and never blocks ingestion.
/// </summary>
public static class CashSessionAudit
{
    public const string OpenedAction = "cash-session.opened";
    public const string ClosedAction = "cash-session.closed";
    private const string EntityType = "cash-session";

    public static UserManagementAuditEntry? TryBuild(SyncEnvelope envelope)
    {
        try
        {
            return envelope.PayloadKind switch
            {
                CashSessionPayloadKinds.Opened => BuildOpened(envelope),
                CashSessionPayloadKinds.Closed => BuildClosed(envelope),
                _ => null,
            };
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException or NotSupportedException)
        {
            return null;
        }
    }

    private static UserManagementAuditEntry? BuildOpened(SyncEnvelope envelope)
    {
        var payload = SyncPayloadCodec.Deserialize<CashSessionOpenedPayloadV1>(envelope.Payload);
        // A payload from a reader that cannot understand it deserializes to defaults: no session, no audit.
        if (payload.SessionId == Guid.Empty)
        {
            return null;
        }

        return Entry(envelope, payload.SessionId, payload.OperatorId, OpenedAction, new
        {
            branchId = envelope.BranchId,
            openingFloat = payload.OpeningFloat,
            openedAtUtc = payload.OpenedAtUtc,
        });
    }

    private static UserManagementAuditEntry? BuildClosed(SyncEnvelope envelope)
    {
        var payload = SyncPayloadCodec.Deserialize<CashSessionClosedPayloadV1>(envelope.Payload);
        if (payload.SessionId == Guid.Empty)
        {
            return null;
        }

        return Entry(envelope, payload.SessionId, payload.ClosedByOperatorId, ClosedAction, new
        {
            branchId = envelope.BranchId,
            openingFloat = payload.OpeningFloat,
            saleCount = payload.SaleCount,
            cashKept = payload.CashKept,
            cardTotal = payload.CardTotal,
            qrTotal = payload.QrTotal,
            untenderedTotal = payload.UntenderedTotal,
            expectedCash = payload.ExpectedCash,
            countedCash = payload.CountedCash,
            difference = payload.Difference,
            closedAtUtc = payload.ClosedAtUtc,
        });
    }

    private static UserManagementAuditEntry Entry(SyncEnvelope envelope, Guid sessionId, Guid operatorId, string action, object detail) =>
        new(
            ActorKind: "org-user",
            ActorId: operatorId,
            OrganizationId: envelope.OrganizationId,
            EntityType: EntityType,
            EntityId: sessionId,
            Action: action,
            OldValueJson: null,
            NewValueJson: JsonSerializer.Serialize(detail));
}
