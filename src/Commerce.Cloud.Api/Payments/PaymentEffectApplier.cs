using System.Text.Json;
using Commerce.Cloud.Api.Tenancy;
using Commerce.Domain.Sync;
using Npgsql;

namespace Commerce.Cloud.Api.Payments;

/// <summary>
/// JSON shape of a <c>payload_kind = "PaymentRecorded"</c> sync envelope
/// payload — the branch-durable <c>PaymentEffect</c>'s wire shape.
/// </summary>
public sealed record PaymentRecordedPayload(
    Guid EntryId,
    string SubjectKind,
    Guid SubjectId,
    string EntryKind,
    string Method,
    decimal Amount,
    Guid? ReversesEntryId,
    Guid ActorId,
    DateTimeOffset RecordedAtUtc);

/// <summary>
/// Projects a <c>payload_kind = "PaymentRecorded"</c> envelope into
/// `payment_entries` INSIDE THE SAME transaction as the `sync_inbox` insert
/// (commerce-payments design.md "Payment-Effect Synchronization Path
/// Parallel to the Sale Outbox"). Duplicate `operation_id` ⇒
/// <see cref="InboundApplyOutcome.DuplicateIgnored"/>, projection skipped —
/// the same idempotency guarantee <see cref="Persistence.PostgresCloudInboxStore"/>
/// already provides for sale envelopes, extended to also write the payment
/// projection atomically. A manually-recorded (staff attestation) payment is
/// always Approved (Unit 3's <c>ManuallyRecordedApproval</c>), so this
/// applier writes `approval_state = 'Approved'` unconditionally — it never
/// re-runs approval logic; the branch already decided that.
/// </summary>
public sealed class PaymentEffectApplier
{
    private readonly NpgsqlDataSource _dataSource;

    public PaymentEffectApplier(NpgsqlDataSource dataSource) => _dataSource = dataSource;

    public async Task<InboundApplyResult> ApplyAsync(CloudTenantScope scope, SyncEnvelope envelope, CancellationToken ct)
    {
        if (scope.OrganizationId != envelope.OrganizationId)
        {
            return new InboundApplyResult(InboundApplyOutcome.Denied, envelope.OperationId);
        }

        await using var connection = await _dataSource.OpenConnectionAsync(ct);
        await using var tx = await connection.BeginTransactionAsync(ct);

        await using (var scopeCmd = new NpgsqlCommand("SELECT set_config('app.current_org_id', $1, true)", connection, tx))
        {
            scopeCmd.Parameters.AddWithValue(scope.OrganizationId.ToString());
            await scopeCmd.ExecuteNonQueryAsync(ct);
        }

        await using (var existsCmd = new NpgsqlCommand(
            "SELECT 1 FROM sync_inbox WHERE operation_id = $1", connection, tx))
        {
            existsCmd.Parameters.AddWithValue(envelope.OperationId);
            var existing = await existsCmd.ExecuteScalarAsync(ct);
            if (existing is not null)
            {
                await tx.CommitAsync(ct);
                return new InboundApplyResult(InboundApplyOutcome.DuplicateIgnored, envelope.OperationId);
            }
        }

        await using (var insertInboxCmd = new NpgsqlCommand(
            """
            INSERT INTO sync_inbox
                (operation_id, organization_id, branch_id, aggregate_id, aggregate_version,
                 actor_id, correlation_id, occurred_at_utc, payload_kind, payload, status)
            VALUES ($1, $2, $3, $4, $5, $6, $7, $8, $9, $10::jsonb, 'Pending')
            """, connection, tx))
        {
            insertInboxCmd.Parameters.AddWithValue(envelope.OperationId);
            insertInboxCmd.Parameters.AddWithValue(envelope.OrganizationId);
            insertInboxCmd.Parameters.AddWithValue(envelope.BranchId);
            insertInboxCmd.Parameters.AddWithValue(envelope.AggregateId);
            insertInboxCmd.Parameters.AddWithValue(envelope.AggregateVersion);
            insertInboxCmd.Parameters.AddWithValue(envelope.ActorId);
            insertInboxCmd.Parameters.AddWithValue(envelope.CorrelationId);
            insertInboxCmd.Parameters.AddWithValue(envelope.OccurredAtUtc);
            insertInboxCmd.Parameters.AddWithValue(envelope.PayloadKind);
            insertInboxCmd.Parameters.AddWithValue(envelope.Payload);
            await insertInboxCmd.ExecuteNonQueryAsync(ct);
        }

        var payload = JsonSerializer.Deserialize<PaymentRecordedPayload>(envelope.Payload)
            ?? throw new InvalidOperationException("PaymentRecorded payload could not be deserialized.");

        await using (var insertEntryCmd = new NpgsqlCommand(
            """
            INSERT INTO payment_entries
                (entry_id, organization_id, operation_id, subject_kind, subject_id,
                 entry_kind, method, amount, approval_state, reverses_entry_id, actor_id, recorded_at_utc)
            VALUES ($1, $2, $1, $3, $4, $5, $6, $7, 'Approved', $8, $9, $10)
            ON CONFLICT (operation_id) DO NOTHING
            """, connection, tx))
        {
            insertEntryCmd.Parameters.AddWithValue(payload.EntryId);
            insertEntryCmd.Parameters.AddWithValue(scope.OrganizationId);
            insertEntryCmd.Parameters.AddWithValue(payload.SubjectKind);
            insertEntryCmd.Parameters.AddWithValue(payload.SubjectId);
            insertEntryCmd.Parameters.AddWithValue(payload.EntryKind);
            insertEntryCmd.Parameters.AddWithValue(payload.Method);
            insertEntryCmd.Parameters.AddWithValue(payload.Amount);
            insertEntryCmd.Parameters.AddWithValue((object?)payload.ReversesEntryId ?? DBNull.Value);
            insertEntryCmd.Parameters.AddWithValue(payload.ActorId);
            insertEntryCmd.Parameters.AddWithValue(payload.RecordedAtUtc);
            await insertEntryCmd.ExecuteNonQueryAsync(ct);
        }

        await tx.CommitAsync(ct);
        return new InboundApplyResult(InboundApplyOutcome.Applied, envelope.OperationId);
    }
}
