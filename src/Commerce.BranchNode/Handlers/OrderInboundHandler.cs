using Commerce.Domain.Sync;
using Microsoft.Data.Sqlite;

namespace Commerce.BranchNode.Handlers;

/// <summary>
/// Task 3.7: materializes an inbound "order" envelope into `inbound_orders`,
/// INSIDE the caller's transaction (design.md "Materialization contract") —
/// replacing the old bespoke, outside-the-transaction application that let
/// `DuplicateIgnored` confirm an order nothing had applied (Verified 4).
/// </summary>
public sealed class OrderInboundHandler : IInboundEffectHandler
{
    public string PayloadKind => "order";

    public void Apply(SyncEnvelope envelope, SqliteTransaction transaction)
    {
        using var command = transaction.Connection!.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            INSERT INTO inbound_orders (order_id, organization_id, destination_branch_id, payload, materialized_at_utc)
            VALUES ($orderId, $organizationId, $destinationBranchId, $payload, $materializedAt)
            ON CONFLICT(order_id) DO NOTHING;
            """;
        command.Parameters.AddWithValue("$orderId", envelope.AggregateId.ToString());
        command.Parameters.AddWithValue("$organizationId", envelope.OrganizationId.ToString());
        command.Parameters.AddWithValue("$destinationBranchId", envelope.BranchId.ToString());
        command.Parameters.AddWithValue("$payload", envelope.Payload);
        command.Parameters.AddWithValue("$materializedAt", DateTimeOffset.UtcNow.ToString("O"));
        command.ExecuteNonQuery();
    }
}
