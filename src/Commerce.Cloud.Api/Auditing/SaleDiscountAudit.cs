using System.Text.Json;
using Commerce.Domain.Sync;
using Commerce.Domain.Sync.Payloads;

namespace Commerce.Cloud.Api.Auditing;

/// <summary>
/// Builds the audit record of a discounted sale ingested from a terminal
/// (pos-scan-sale "Discounts Recorded and Synchronized With Their
/// Authorization"). One record per discounted sale, written in the same
/// transaction as the `sync_inbox` insert, which already deduplicates by
/// operation id, so redelivery cannot write a second one. A sale with no
/// discounts, a payload from before discounts existed, or a payload that cannot
/// be read yields no record and never blocks ingestion.
///
/// The actor is the operator who authorized the discount (the marker on the
/// payload), falling back to the envelope actor. A discount without a marker is
/// still audited, as `sale.discount.unauthorized`, so it is visible instead of
/// silently accepted.
/// </summary>
public static class SaleDiscountAudit
{
    public const string AuthorizedAction = "sale.discount.authorized";
    public const string UnauthorizedAction = "sale.discount.unauthorized";

    public static UserManagementAuditEntry? TryBuild(SyncEnvelope envelope)
    {
        if (envelope.PayloadKind != "sale")
        {
            return null;
        }

        SalePayloadV1 payload;
        try
        {
            payload = SyncPayloadCodec.Deserialize<SalePayloadV1>(envelope.Payload);
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException or NotSupportedException)
        {
            return null;
        }

        // A sale-kind payload that carries no lines (older or minimal producers)
        // deserializes with a null list: it simply has nothing discounted.
        var discountedLines = (payload.Lines ?? [])
            .Where(l => l.LineDiscountPercent is not null || l.LineDiscountAmount is not null)
            .Select(l => new { lineNumber = l.LineNumber, discountPercent = l.LineDiscountPercent, discountAmount = l.LineDiscountAmount })
            .ToList();
        if (discountedLines.Count == 0 && payload.SaleDiscountPercent is null && payload.SaleDiscountAmount is null)
        {
            return null;
        }

        var authorization = payload.DiscountAuthorization;
        var detail = JsonSerializer.Serialize(new
        {
            branchId = envelope.BranchId,
            authorizationMethod = authorization?.Method,
            operatorId = authorization?.OperatorId,
            pinVersion = authorization?.PinVersion,
            totalAmount = payload.TotalAmount,
            saleDiscountPercent = payload.SaleDiscountPercent,
            saleDiscountAmount = payload.SaleDiscountAmount,
            lines = discountedLines,
        });

        return new UserManagementAuditEntry(
            ActorKind: "org-user",
            ActorId: authorization?.OperatorId ?? envelope.ActorId,
            OrganizationId: envelope.OrganizationId,
            EntityType: "sale",
            EntityId: payload.SaleId,
            Action: authorization is null ? UnauthorizedAction : AuthorizedAction,
            OldValueJson: null,
            NewValueJson: detail);
    }
}
