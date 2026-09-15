using Commerce.Cloud.Api.Tenancy;
using Commerce.Domain.Sync;

namespace Commerce.Cloud.Api;

/// <summary>
/// Cloud-side inbox/ACK receiver.
///
/// PRODUCTION NOTE: this is an in-memory RLS-EQUIVALENT test double, not the
/// real PostgreSQL adapter. No live PostgreSQL instance was available in this
/// sandboxed apply session (Docker daemon not running), so this class proves
/// the exact same deny/allow semantics that `deploy/dev/db/init-rls.sql`
/// enforces at the database level: default-deny (rows are invisible/rejected
/// unless the row's `organization_id` matches the authenticated scope), and a
/// non-owner runtime role that can never bypass that filter. Every method
/// here takes a `CloudTenantScope` derived from authenticated claims and
/// NEVER accepts a raw organization ID from the caller for row access —
/// exactly mirroring `current_setting('app.current_org_id')` in the real
/// policy. Wiring the real Npgsql-backed adapter against
/// `deploy/dev/db/init-rls.sql` is tracked as follow-up production work.
/// </summary>
public sealed class CloudInboxStore
{
    private readonly object _gate = new();
    private readonly Dictionary<Guid, (Guid OrganizationId, SyncEnvelope Envelope)> _inbox = new();
    private readonly Dictionary<Guid, SyncOperationStatus> _status = new();

    public InboundApplyResult TryApplyInbound(CloudTenantScope scope, SyncEnvelope envelope)
    {
        if (scope.OrganizationId != envelope.OrganizationId)
        {
            // RLS default-deny + non-owner role: an envelope claiming another
            // organization is rejected outright, never partially processed.
            return new InboundApplyResult(InboundApplyOutcome.Denied, envelope.OperationId);
        }

        lock (_gate)
        {
            if (_inbox.ContainsKey(envelope.OperationId))
            {
                return new InboundApplyResult(InboundApplyOutcome.DuplicateIgnored, envelope.OperationId);
            }

            _inbox[envelope.OperationId] = (scope.OrganizationId, envelope);
            _status[envelope.OperationId] = SyncOperationStatus.Pending;
            return new InboundApplyResult(InboundApplyOutcome.Applied, envelope.OperationId);
        }
    }

    public bool Acknowledge(CloudTenantScope scope, Guid operationId)
    {
        lock (_gate)
        {
            if (!_inbox.TryGetValue(operationId, out var row) || row.OrganizationId != scope.OrganizationId)
            {
                // Default-deny: a row outside the caller's scope is treated as
                // not found, never revealing cross-tenant existence.
                return false;
            }

            _status[operationId] = SyncOperationStatus.Acknowledged;
            return true;
        }
    }

    public SyncOperationStatus? GetStatus(CloudTenantScope scope, Guid operationId)
    {
        lock (_gate)
        {
            if (!_inbox.TryGetValue(operationId, out var row) || row.OrganizationId != scope.OrganizationId)
            {
                return null;
            }

            return _status[operationId];
        }
    }

    public IReadOnlyList<SyncEnvelope> GetInboxFor(CloudTenantScope scope)
    {
        lock (_gate)
        {
            return _inbox.Values
                .Where(row => row.OrganizationId == scope.OrganizationId)
                .Select(row => row.Envelope)
                .ToList();
        }
    }
}
